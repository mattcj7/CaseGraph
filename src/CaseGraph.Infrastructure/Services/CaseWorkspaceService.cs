using CaseGraph.Core.Abstractions;
using CaseGraph.Core.Models;
using System.Text.Json;

namespace CaseGraph.Infrastructure.Services;

public sealed class CaseWorkspaceService : ICaseWorkspaceService
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true
    };

    private readonly IClock _clock;
    private readonly string _casesRoot;

    public CaseWorkspaceService(IClock clock, string? workspaceRootOverride = null)
    {
        _clock = clock;

        var workspaceRoot = workspaceRootOverride
            ?? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "CaseGraphOffline"
            );

        _casesRoot = Path.Combine(workspaceRoot, "cases");
        Directory.CreateDirectory(_casesRoot);
    }

    public async Task<IReadOnlyList<CaseInfo>> ListCasesAsync(CancellationToken ct)
    {
        var cases = new List<CaseInfo>();

        foreach (var caseDirectory in Directory.EnumerateDirectories(_casesRoot))
        {
            ct.ThrowIfCancellationRequested();

            var caseFilePath = Path.Combine(caseDirectory, "case.json");
            if (!File.Exists(caseFilePath))
            {
                continue;
            }

            var document = await ReadCaseDocumentAsync(caseFilePath, ct);
            if (document is null)
            {
                continue;
            }

            cases.Add(document.CaseInfo);
        }

        return cases
            .OrderByDescending(c => c.LastOpenedAtUtc ?? c.CreatedAtUtc)
            .ToList();
    }

    public async Task<CaseInfo> CreateCaseAsync(string name, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new ArgumentException("Case name is required.", nameof(name));
        }

        var now = _clock.UtcNow;
        var caseInfo = new CaseInfo
        {
            CaseId = Guid.NewGuid(),
            Name = name.Trim(),
            CreatedAtUtc = now,
            LastOpenedAtUtc = now
        };

        await SaveCaseAsync(caseInfo, Array.Empty<EvidenceItem>(), ct);

        return caseInfo;
    }

    public async Task<CaseInfo> OpenCaseAsync(Guid caseId, CancellationToken ct)
    {
        var (caseInfo, evidence) = await LoadCaseAsync(caseId, ct);
        caseInfo.LastOpenedAtUtc = _clock.UtcNow;

        await SaveCaseAsync(caseInfo, evidence, ct);

        return caseInfo;
    }

    public async Task SaveCaseAsync(
        CaseInfo caseInfo,
        IReadOnlyList<EvidenceItem> evidence,
        CancellationToken ct
    )
    {
        var caseDirectory = GetCaseDirectory(caseInfo.CaseId);
        Directory.CreateDirectory(caseDirectory);
        Directory.CreateDirectory(Path.Combine(caseDirectory, "vault"));

        var document = new CaseWorkspaceDocument
        {
            CaseInfo = caseInfo,
            Evidence = evidence.ToList()
        };

        var caseFilePath = Path.Combine(caseDirectory, "case.json");
        var tempFilePath = $"{caseFilePath}.tmp";

        await using (var stream = CreateFileStream(tempFilePath, FileMode.Create, FileAccess.Write))
        {
            await JsonSerializer.SerializeAsync(stream, document, SerializerOptions, ct);
            await stream.FlushAsync(ct);
        }

        File.Move(tempFilePath, caseFilePath, true);
    }

    public async Task<(CaseInfo caseInfo, List<EvidenceItem> evidence)> LoadCaseAsync(
        Guid caseId,
        CancellationToken ct
    )
    {
        var caseFilePath = Path.Combine(GetCaseDirectory(caseId), "case.json");
        if (!File.Exists(caseFilePath))
        {
            throw new FileNotFoundException($"Case file not found for case {caseId}.", caseFilePath);
        }

        var document = await ReadCaseDocumentAsync(caseFilePath, ct)
            ?? throw new InvalidDataException($"Case file is invalid: {caseFilePath}");

        return (document.CaseInfo, document.Evidence);
    }

    private async Task<CaseWorkspaceDocument?> ReadCaseDocumentAsync(
        string caseFilePath,
        CancellationToken ct
    )
    {
        await using var stream = CreateFileStream(caseFilePath, FileMode.Open, FileAccess.Read);
        return await JsonSerializer.DeserializeAsync<CaseWorkspaceDocument>(
            stream,
            SerializerOptions,
            ct
        );
    }

    private string GetCaseDirectory(Guid caseId)
    {
        return Path.Combine(_casesRoot, caseId.ToString("D"));
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
