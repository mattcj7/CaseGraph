using CaseGraph.Core.Abstractions;
using CaseGraph.Core.Diagnostics;
using CaseGraph.Core.Models;
using CaseGraph.Infrastructure.Persistence;
using CaseGraph.Infrastructure.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using System.Text.Json;

namespace CaseGraph.Infrastructure.Services;

public sealed class CaseWorkspaceService : ICaseWorkspaceService
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true
    };

    private readonly IClock _clock;
    private readonly IWorkspacePathProvider _pathProvider;
    private readonly IDbContextFactory<WorkspaceDbContext> _dbContextFactory;
    private readonly IWorkspaceDatabaseInitializer _databaseInitializer;
    private readonly IWorkspaceWriteGate _workspaceWriteGate;
    private readonly IAuditLogService _auditLogService;

    private readonly SemaphoreSlim _legacyImportLock = new(1, 1);
    private bool _legacyImportComplete;

    public CaseWorkspaceService(
        IClock clock,
        IWorkspacePathProvider pathProvider,
        IDbContextFactory<WorkspaceDbContext> dbContextFactory,
        IWorkspaceDatabaseInitializer databaseInitializer,
        IWorkspaceWriteGate workspaceWriteGate,
        IAuditLogService auditLogService
    )
    {
        _clock = clock;
        _pathProvider = pathProvider;
        _dbContextFactory = dbContextFactory;
        _databaseInitializer = databaseInitializer;
        _workspaceWriteGate = workspaceWriteGate;
        _auditLogService = auditLogService;

        Directory.CreateDirectory(_pathProvider.WorkspaceRoot);
        Directory.CreateDirectory(_pathProvider.CasesRoot);
    }

    public async Task<IReadOnlyList<CaseInfo>> ListCasesAsync(CancellationToken ct)
    {
        await _databaseInitializer.EnsureInitializedAsync(ct);
        await ImportLegacyCasesIfNeededAsync(ct);

        await using var db = await _dbContextFactory.CreateDbContextAsync(ct);
        var records = await db.Cases
            .AsNoTracking()
            .ToListAsync(ct);

        return records
            .OrderByDescending(c => c.LastOpenedAtUtc ?? c.CreatedAtUtc)
            .Select(MapCaseInfo)
            .ToList();
    }

    public async Task<CaseInfo> CreateCaseAsync(string name, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new ArgumentException("Case name is required.", nameof(name));
        }

        var now = _clock.UtcNow.ToUniversalTime();
        var caseInfo = new CaseInfo
        {
            CaseId = Guid.NewGuid(),
            Name = name.Trim(),
            CreatedAtUtc = now,
            LastOpenedAtUtc = now
        };

        await SaveCaseAsync(caseInfo, Array.Empty<EvidenceItem>(), ct);

        await _auditLogService.AddAsync(
            new AuditEvent
            {
                TimestampUtc = now,
                Operator = Environment.UserName,
                ActionType = "CaseCreated",
                CaseId = caseInfo.CaseId,
                Summary = $"Created case \"{caseInfo.Name}\".",
                JsonPayload = JsonSerializer.Serialize(new
                {
                    caseInfo.CaseId,
                    caseInfo.Name
                })
            },
            ct
        );

        return caseInfo;
    }

    public async Task<CaseInfo> OpenCaseAsync(Guid caseId, CancellationToken ct)
    {
        var (caseInfo, evidence) = await LoadCaseAsync(caseId, ct);
        caseInfo.LastOpenedAtUtc = _clock.UtcNow.ToUniversalTime();

        await SaveCaseAsync(caseInfo, evidence, ct);

        await _auditLogService.AddAsync(
            new AuditEvent
            {
                TimestampUtc = _clock.UtcNow.ToUniversalTime(),
                Operator = Environment.UserName,
                ActionType = "CaseOpened",
                CaseId = caseInfo.CaseId,
                Summary = $"Opened case \"{caseInfo.Name}\"."
            },
            ct
        );

        return caseInfo;
    }

    public async Task SaveCaseAsync(
        CaseInfo caseInfo,
        IReadOnlyList<EvidenceItem> evidence,
        CancellationToken ct
    )
    {
        await _databaseInitializer.EnsureInitializedAsync(ct);
        await ImportLegacyCasesIfNeededAsync(ct);

        await _workspaceWriteGate.ExecuteWriteAsync(
            operationName: "CaseWorkspace.SaveCase",
            async writeCt =>
            {
                await using var db = await _dbContextFactory.CreateDbContextAsync(writeCt);
                await using var transaction = await db.Database.BeginTransactionAsync(writeCt);

                var caseRecord = await db.Cases
                    .FirstOrDefaultAsync(c => c.CaseId == caseInfo.CaseId, writeCt);

                if (caseRecord is null)
                {
                    caseRecord = new CaseRecord
                    {
                        CaseId = caseInfo.CaseId,
                        Name = caseInfo.Name,
                        CreatedAtUtc = caseInfo.CreatedAtUtc,
                        LastOpenedAtUtc = caseInfo.LastOpenedAtUtc
                    };
                    db.Cases.Add(caseRecord);
                }
                else
                {
                    caseRecord.Name = caseInfo.Name;
                    caseRecord.CreatedAtUtc = caseInfo.CreatedAtUtc;
                    caseRecord.LastOpenedAtUtc = caseInfo.LastOpenedAtUtc;
                }

                var incomingIds = evidence.Select(e => e.EvidenceItemId).ToHashSet();
                var existingRecords = await db.EvidenceItems
                    .Where(e => e.CaseId == caseInfo.CaseId)
                    .ToListAsync(writeCt);

                var recordsToRemove = existingRecords
                    .Where(e => !incomingIds.Contains(e.EvidenceItemId))
                    .ToList();
                if (recordsToRemove.Count > 0)
                {
                    db.EvidenceItems.RemoveRange(recordsToRemove);
                }

                foreach (var evidenceItem in evidence)
                {
                    var evidenceRecord = existingRecords
                        .FirstOrDefault(e => e.EvidenceItemId == evidenceItem.EvidenceItemId);

                    if (evidenceRecord is null)
                    {
                        evidenceRecord = new EvidenceItemRecord
                        {
                            EvidenceItemId = evidenceItem.EvidenceItemId,
                            CaseId = caseInfo.CaseId
                        };
                        db.EvidenceItems.Add(evidenceRecord);
                    }

                    evidenceRecord.CaseId = caseInfo.CaseId;
                    evidenceRecord.DisplayName = evidenceItem.DisplayName;
                    evidenceRecord.OriginalPath = evidenceItem.OriginalPath;
                    evidenceRecord.OriginalFileName = evidenceItem.OriginalFileName;
                    evidenceRecord.AddedAtUtc = evidenceItem.AddedAtUtc;
                    evidenceRecord.SizeBytes = evidenceItem.SizeBytes;
                    evidenceRecord.Sha256Hex = evidenceItem.Sha256Hex;
                    evidenceRecord.FileExtension = evidenceItem.FileExtension;
                    evidenceRecord.SourceType = evidenceItem.SourceType;
                    evidenceRecord.ManifestRelativePath = evidenceItem.ManifestRelativePath;
                    evidenceRecord.StoredRelativePath = evidenceItem.StoredRelativePath;
                }

                await db.SaveChangesAsync(writeCt);
                await transaction.CommitAsync(writeCt);
            },
            ct,
            correlationId: AppFileLogger.GetScopeValue("correlationId"),
            fields: new Dictionary<string, object?>
            {
                ["caseId"] = caseInfo.CaseId.ToString("D")
            }
        );

        await WriteCaseFileSnapshotAsync(caseInfo, evidence, ct);
    }

    public async Task<(CaseInfo caseInfo, List<EvidenceItem> evidence)> LoadCaseAsync(
        Guid caseId,
        CancellationToken ct
    )
    {
        await _databaseInitializer.EnsureInitializedAsync(ct);
        await ImportLegacyCasesIfNeededAsync(ct);

        await using var db = await _dbContextFactory.CreateDbContextAsync(ct);
        var caseRecord = await db.Cases
            .AsNoTracking()
            .FirstOrDefaultAsync(c => c.CaseId == caseId, ct);

        if (caseRecord is null)
        {
            throw new FileNotFoundException($"Case was not found for {caseId:D}.");
        }

        var evidenceRecords = await db.EvidenceItems
            .AsNoTracking()
            .Where(e => e.CaseId == caseId)
            .ToListAsync(ct);

        var evidence = evidenceRecords
            .OrderByDescending(e => e.AddedAtUtc)
            .Select(MapEvidenceItem)
            .ToList();

        return (MapCaseInfo(caseRecord), evidence);
    }

    private async Task ImportLegacyCasesIfNeededAsync(CancellationToken ct)
    {
        if (_legacyImportComplete)
        {
            return;
        }

        await _legacyImportLock.WaitAsync(ct);
        try
        {
            if (_legacyImportComplete)
            {
                return;
            }

            await using var db = await _dbContextFactory.CreateDbContextAsync(ct);
            var existingCaseIds = await db.Cases
                .Select(c => c.CaseId)
                .ToListAsync(ct);
            var existingSet = existingCaseIds.ToHashSet();

            foreach (var caseDirectory in Directory.EnumerateDirectories(_pathProvider.CasesRoot))
            {
                ct.ThrowIfCancellationRequested();

                var caseFilePath = Path.Combine(caseDirectory, "case.json");
                if (!File.Exists(caseFilePath))
                {
                    continue;
                }

                await using var stream = CreateFileStream(caseFilePath, FileMode.Open, FileAccess.Read);
                var document = await JsonSerializer.DeserializeAsync<CaseWorkspaceDocument>(
                    stream,
                    SerializerOptions,
                    ct
                );

                if (document?.CaseInfo is null || existingSet.Contains(document.CaseInfo.CaseId))
                {
                    continue;
                }

                db.Cases.Add(new CaseRecord
                {
                    CaseId = document.CaseInfo.CaseId,
                    Name = document.CaseInfo.Name,
                    CreatedAtUtc = document.CaseInfo.CreatedAtUtc,
                    LastOpenedAtUtc = document.CaseInfo.LastOpenedAtUtc
                });

                foreach (var evidenceItem in document.Evidence)
                {
                    db.EvidenceItems.Add(new EvidenceItemRecord
                    {
                        EvidenceItemId = evidenceItem.EvidenceItemId,
                        CaseId = evidenceItem.CaseId,
                        DisplayName = evidenceItem.DisplayName,
                        OriginalPath = evidenceItem.OriginalPath,
                        OriginalFileName = evidenceItem.OriginalFileName,
                        AddedAtUtc = evidenceItem.AddedAtUtc,
                        SizeBytes = evidenceItem.SizeBytes,
                        Sha256Hex = evidenceItem.Sha256Hex,
                        FileExtension = evidenceItem.FileExtension,
                        SourceType = evidenceItem.SourceType,
                        ManifestRelativePath = evidenceItem.ManifestRelativePath,
                        StoredRelativePath = evidenceItem.StoredRelativePath
                    });
                }

                existingSet.Add(document.CaseInfo.CaseId);
            }

            await _workspaceWriteGate.ExecuteWriteAsync(
                operationName: "CaseWorkspace.ImportLegacyCases",
                writeCt => db.SaveChangesAsync(writeCt),
                ct,
                correlationId: AppFileLogger.GetScopeValue("correlationId")
            );
            _legacyImportComplete = true;
        }
        finally
        {
            _legacyImportLock.Release();
        }
    }

    private async Task WriteCaseFileSnapshotAsync(
        CaseInfo caseInfo,
        IReadOnlyList<EvidenceItem> evidence,
        CancellationToken ct
    )
    {
        var caseDirectory = GetCaseDirectory(caseInfo.CaseId);
        Directory.CreateDirectory(caseDirectory);
        Directory.CreateDirectory(Path.Combine(caseDirectory, "vault"));

        var caseFilePath = Path.Combine(caseDirectory, "case.json");
        var tempFilePath = $"{caseFilePath}.tmp";

        var document = new CaseWorkspaceDocument
        {
            CaseInfo = caseInfo,
            Evidence = evidence.ToList()
        };

        await using (var stream = CreateFileStream(tempFilePath, FileMode.Create, FileAccess.Write))
        {
            await JsonSerializer.SerializeAsync(stream, document, SerializerOptions, ct);
            await stream.FlushAsync(ct);
        }

        File.Move(tempFilePath, caseFilePath, true);
    }

    private string GetCaseDirectory(Guid caseId)
    {
        return Path.Combine(_pathProvider.CasesRoot, caseId.ToString("D"));
    }

    private static CaseInfo MapCaseInfo(CaseRecord record)
    {
        return new CaseInfo
        {
            CaseId = record.CaseId,
            Name = record.Name,
            CreatedAtUtc = record.CreatedAtUtc,
            LastOpenedAtUtc = record.LastOpenedAtUtc
        };
    }

    private static EvidenceItem MapEvidenceItem(EvidenceItemRecord record)
    {
        return new EvidenceItem
        {
            EvidenceItemId = record.EvidenceItemId,
            CaseId = record.CaseId,
            DisplayName = record.DisplayName,
            OriginalPath = record.OriginalPath,
            OriginalFileName = record.OriginalFileName,
            AddedAtUtc = record.AddedAtUtc,
            SizeBytes = record.SizeBytes,
            Sha256Hex = record.Sha256Hex,
            FileExtension = record.FileExtension,
            SourceType = record.SourceType,
            ManifestRelativePath = record.ManifestRelativePath,
            StoredRelativePath = record.StoredRelativePath
        };
    }

    private static FileStream CreateFileStream(string path, FileMode mode, FileAccess access)
    {
        var fileShare = access == FileAccess.Read ? FileShare.Read : FileShare.None;
        return new FileStream(
            path,
            mode,
            access,
            fileShare,
            bufferSize: 1024 * 64,
            FileOptions.Asynchronous | FileOptions.SequentialScan
        );
    }

    private sealed class CaseWorkspaceDocument
    {
        public CaseInfo CaseInfo { get; set; } = new();

        public List<EvidenceItem> Evidence { get; set; } = new();
    }
}
