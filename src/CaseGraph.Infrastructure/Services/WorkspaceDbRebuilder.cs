using CaseGraph.Core.Abstractions;
using CaseGraph.Core.Diagnostics;
using CaseGraph.Infrastructure.Persistence;
using CaseGraph.Infrastructure.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using System.Text.Json;

namespace CaseGraph.Infrastructure.Services;

public sealed class WorkspaceDbRebuilder
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly IDbContextFactory<WorkspaceDbContext> _dbContextFactory;
    private readonly IWorkspacePathProvider _workspacePathProvider;
    private readonly IWorkspaceWriteGate _workspaceWriteGate;
    private readonly IClock _clock;

    public WorkspaceDbRebuilder(
        IDbContextFactory<WorkspaceDbContext> dbContextFactory,
        IWorkspacePathProvider workspacePathProvider,
        IWorkspaceWriteGate workspaceWriteGate,
        IClock clock
    )
    {
        _dbContextFactory = dbContextFactory;
        _workspacePathProvider = workspacePathProvider;
        _workspaceWriteGate = workspaceWriteGate;
        _clock = clock;
    }

    public async Task<(int casesRebuilt, int evidenceRebuilt)> RebuildAsync(CancellationToken ct)
    {
        var rebuiltCases = new Dictionary<Guid, CaseRecord>();
        var rebuiltEvidence = new Dictionary<Guid, EvidenceItemRecord>();

        if (!Directory.Exists(_workspacePathProvider.CasesRoot))
        {
            return (0, 0);
        }

        foreach (var caseDirectory in Directory.EnumerateDirectories(_workspacePathProvider.CasesRoot))
        {
            ct.ThrowIfCancellationRequested();

            if (!Guid.TryParse(Path.GetFileName(caseDirectory), out var folderCaseId))
            {
                continue;
            }

            var caseRecord = await ReadCaseRecordAsync(caseDirectory, folderCaseId, ct);
            rebuiltCases[caseRecord.CaseId] = caseRecord;

            var vaultRoot = Path.Combine(caseDirectory, "vault");
            if (!Directory.Exists(vaultRoot))
            {
                continue;
            }

            foreach (var manifestPath in Directory.EnumerateFiles(vaultRoot, "manifest.json", SearchOption.AllDirectories))
            {
                ct.ThrowIfCancellationRequested();

                var manifest = await ReadManifestAsync(manifestPath, ct);
                if (manifest is null || manifest.EvidenceItemId == Guid.Empty)
                {
                    continue;
                }

                var caseId = manifest.CaseId == Guid.Empty ? folderCaseId : manifest.CaseId;
                if (!rebuiltCases.ContainsKey(caseId))
                {
                    rebuiltCases[caseId] = new CaseRecord
                    {
                        CaseId = caseId,
                        Name = $"Recovered Case {caseId:D}",
                        CreatedAtUtc = _clock.UtcNow.ToUniversalTime(),
                        LastOpenedAtUtc = null
                    };
                }

                var relativeManifestPath = Path.GetRelativePath(caseDirectory, manifestPath)
                    .Replace('\\', '/');

                var storedRelativePath = string.IsNullOrWhiteSpace(manifest.StoredRelativePath)
                    ? InferStoredRelativePathFromManifest(manifestPath, caseDirectory, manifest.OriginalFileName)
                    : manifest.StoredRelativePath.Replace('\\', '/');
                var originalFileName = manifest.OriginalFileName ?? string.Empty;

                rebuiltEvidence[manifest.EvidenceItemId] = new EvidenceItemRecord
                {
                    EvidenceItemId = manifest.EvidenceItemId,
                    CaseId = caseId,
                    DisplayName = Path.GetFileNameWithoutExtension(originalFileName),
                    OriginalPath = manifest.OriginalPath ?? string.Empty,
                    OriginalFileName = originalFileName,
                    AddedAtUtc = ParseDateTimeOffset(manifest.AddedAtUtc) ?? _clock.UtcNow.ToUniversalTime(),
                    SizeBytes = manifest.SizeBytes,
                    Sha256Hex = manifest.Sha256Hex ?? string.Empty,
                    FileExtension = manifest.FileExtension ?? Path.GetExtension(originalFileName),
                    SourceType = manifest.SourceType ?? "OTHER",
                    ManifestRelativePath = relativeManifestPath,
                    StoredRelativePath = storedRelativePath
                };
            }
        }

        await _workspaceWriteGate.ExecuteWriteAsync(
            operationName: "WorkspaceDbRebuilder.Rebuild",
            async writeCt =>
            {
                await using var db = await _dbContextFactory.CreateDbContextAsync(writeCt);
                await using var transaction = await db.Database.BeginTransactionAsync(writeCt);

                var existingAuditEvents = await db.AuditEvents.ToListAsync(writeCt);
                if (existingAuditEvents.Count > 0)
                {
                    db.AuditEvents.RemoveRange(existingAuditEvents);
                }

                var existingEvidence = await db.EvidenceItems.ToListAsync(writeCt);
                if (existingEvidence.Count > 0)
                {
                    db.EvidenceItems.RemoveRange(existingEvidence);
                }

                var existingCases = await db.Cases.ToListAsync(writeCt);
                if (existingCases.Count > 0)
                {
                    db.Cases.RemoveRange(existingCases);
                }

                db.Cases.AddRange(rebuiltCases.Values);
                db.EvidenceItems.AddRange(rebuiltEvidence.Values);

                db.AuditEvents.Add(new AuditEventRecord
                {
                    AuditEventId = Guid.NewGuid(),
                    TimestampUtc = _clock.UtcNow.ToUniversalTime(),
                    Operator = Environment.UserName,
                    ActionType = "WorkspaceDbRebuilt",
                    Summary = $"Recovered {rebuiltCases.Count} case(s) and {rebuiltEvidence.Count} evidence item(s) from disk manifests.",
                    JsonPayload = JsonSerializer.Serialize(new
                    {
                        CasesRebuilt = rebuiltCases.Count,
                        EvidenceRebuilt = rebuiltEvidence.Count
                    })
                });

                await db.SaveChangesAsync(writeCt);
                await transaction.CommitAsync(writeCt);
            },
            ct,
            correlationId: AppFileLogger.GetScopeValue("correlationId")
        );

        return (rebuiltCases.Count, rebuiltEvidence.Count);
    }

    private async Task<CaseRecord> ReadCaseRecordAsync(
        string caseDirectory,
        Guid folderCaseId,
        CancellationToken ct
    )
    {
        var caseJsonPath = Path.Combine(caseDirectory, "case.json");
        if (!File.Exists(caseJsonPath))
        {
            return new CaseRecord
            {
                CaseId = folderCaseId,
                Name = $"Recovered Case {folderCaseId:D}",
                CreatedAtUtc = _clock.UtcNow.ToUniversalTime(),
                LastOpenedAtUtc = null
            };
        }

        try
        {
            await using var stream = new FileStream(
                caseJsonPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 1024 * 64,
                FileOptions.Asynchronous | FileOptions.SequentialScan
            );
            var document = await JsonSerializer.DeserializeAsync<CaseWorkspaceDocument>(
                stream,
                SerializerOptions,
                ct
            );

            var caseInfo = document?.CaseInfo;
            if (caseInfo is null)
            {
                throw new InvalidDataException("case.json did not contain CaseInfo.");
            }

            var caseId = caseInfo.CaseId == Guid.Empty ? folderCaseId : caseInfo.CaseId;
            return new CaseRecord
            {
                CaseId = caseId,
                Name = string.IsNullOrWhiteSpace(caseInfo.Name)
                    ? $"Recovered Case {caseId:D}"
                    : caseInfo.Name.Trim(),
                CreatedAtUtc = caseInfo.CreatedAtUtc == default
                    ? _clock.UtcNow.ToUniversalTime()
                    : caseInfo.CreatedAtUtc,
                LastOpenedAtUtc = caseInfo.LastOpenedAtUtc
            };
        }
        catch
        {
            return new CaseRecord
            {
                CaseId = folderCaseId,
                Name = $"Recovered Case {folderCaseId:D}",
                CreatedAtUtc = _clock.UtcNow.ToUniversalTime(),
                LastOpenedAtUtc = null
            };
        }
    }

    private static async Task<EvidenceManifestDocument?> ReadManifestAsync(
        string manifestPath,
        CancellationToken ct
    )
    {
        try
        {
            await using var stream = new FileStream(
                manifestPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 1024 * 64,
                FileOptions.Asynchronous | FileOptions.SequentialScan
            );

            return await JsonSerializer.DeserializeAsync<EvidenceManifestDocument>(
                stream,
                SerializerOptions,
                ct
            );
        }
        catch
        {
            return null;
        }
    }

    private static string InferStoredRelativePathFromManifest(
        string manifestPath,
        string caseDirectory,
        string? originalFileName
    )
    {
        var manifestDirectory = Path.GetDirectoryName(manifestPath);
        if (string.IsNullOrWhiteSpace(manifestDirectory))
        {
            return string.Empty;
        }

        var originalDirectory = Path.Combine(manifestDirectory, "original");
        if (!Directory.Exists(originalDirectory))
        {
            return string.Empty;
        }

        if (!string.IsNullOrWhiteSpace(originalFileName))
        {
            var exactPath = Path.Combine(originalDirectory, originalFileName);
            if (File.Exists(exactPath))
            {
                return Path.GetRelativePath(caseDirectory, exactPath).Replace('\\', '/');
            }
        }

        var firstFile = Directory.EnumerateFiles(originalDirectory).FirstOrDefault();
        if (firstFile is null)
        {
            return string.Empty;
        }

        return Path.GetRelativePath(caseDirectory, firstFile).Replace('\\', '/');
    }

    private static DateTimeOffset? ParseDateTimeOffset(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        if (
            DateTimeOffset.TryParse(
                value,
                System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.AssumeUniversal,
                out var parsed
            )
        )
        {
            return parsed.ToUniversalTime();
        }

        return null;
    }

    private sealed class CaseWorkspaceDocument
    {
        public CaseInfoDocument? CaseInfo { get; set; }
    }

    private sealed class CaseInfoDocument
    {
        public Guid CaseId { get; set; }

        public string Name { get; set; } = string.Empty;

        public DateTimeOffset CreatedAtUtc { get; set; }

        public DateTimeOffset? LastOpenedAtUtc { get; set; }
    }

    private sealed class EvidenceManifestDocument
    {
        public Guid EvidenceItemId { get; set; }

        public Guid CaseId { get; set; }

        public string AddedAtUtc { get; set; } = string.Empty;

        public string? OriginalPath { get; set; }

        public string? OriginalFileName { get; set; }

        public string? StoredRelativePath { get; set; }

        public long SizeBytes { get; set; }

        public string? Sha256Hex { get; set; }

        public string? FileExtension { get; set; }

        public string? SourceType { get; set; }
    }
}
