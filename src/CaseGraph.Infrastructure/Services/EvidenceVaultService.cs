using CaseGraph.Core.Abstractions;
using CaseGraph.Core.Models;
using System.Buffers;
using System.Security.Cryptography;
using System.Text.Json;

namespace CaseGraph.Infrastructure.Services;

public sealed class EvidenceVaultService : IEvidenceVaultService
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true
    };

    private readonly IClock _clock;
    private readonly ICaseWorkspaceService _caseWorkspaceService;
    private readonly string _casesRoot;

    public EvidenceVaultService(
        IClock clock,
        ICaseWorkspaceService caseWorkspaceService,
        string? workspaceRootOverride = null
    )
    {
        _clock = clock;
        _caseWorkspaceService = caseWorkspaceService;

        var workspaceRoot = workspaceRootOverride
            ?? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "CaseGraphOffline"
            );

        _casesRoot = Path.Combine(workspaceRoot, "cases");
        Directory.CreateDirectory(_casesRoot);
    }

    public async Task<EvidenceItem> ImportEvidenceFileAsync(
        CaseInfo caseInfo,
        string filePath,
        IProgress<double>? progress,
        CancellationToken ct
    )
    {
        if (string.IsNullOrWhiteSpace(filePath))
        {
            throw new ArgumentException("A file path is required.", nameof(filePath));
        }

        if (!File.Exists(filePath))
        {
            throw new FileNotFoundException("Evidence file does not exist.", filePath);
        }

        var evidenceItemId = Guid.NewGuid();
        var fileName = Path.GetFileName(filePath);
        var addedAtUtc = _clock.UtcNow.ToUniversalTime();
        var caseRootPath = GetCaseRootPath(caseInfo.CaseId);
        var vaultItemPath = Path.Combine(caseRootPath, "vault", evidenceItemId.ToString("D"));
        var originalDirectoryPath = Path.Combine(vaultItemPath, "original");
        var storedFilePath = Path.Combine(originalDirectoryPath, fileName);
        var manifestFilePath = Path.Combine(vaultItemPath, "manifest.json");
        var sourceType = ResolveSourceType(fileName);
        var fileExtension = Path.GetExtension(fileName);

        Directory.CreateDirectory(originalDirectoryPath);

        long sizeBytes;
        string sha256Hex;
        await using (var sourceStream = CreateReadStream(filePath))
        await using (var destinationStream = CreateWriteStream(storedFilePath))
        {
            sizeBytes = sourceStream.Length;
            sha256Hex = await CopyAndHashAsync(
                sourceStream,
                destinationStream,
                sizeBytes,
                progress,
                ct
            );
        }

        var evidenceItem = new EvidenceItem
        {
            EvidenceItemId = evidenceItemId,
            CaseId = caseInfo.CaseId,
            DisplayName = Path.GetFileNameWithoutExtension(fileName),
            OriginalPath = filePath,
            OriginalFileName = fileName,
            AddedAtUtc = addedAtUtc,
            SizeBytes = sizeBytes,
            Sha256Hex = sha256Hex,
            FileExtension = fileExtension,
            SourceType = sourceType,
            ManifestRelativePath = ToRelativePath("vault", evidenceItemId.ToString("D"), "manifest.json"),
            StoredRelativePath = ToRelativePath("vault", evidenceItemId.ToString("D"), "original", fileName)
        };

        var manifest = new EvidenceManifest
        {
            SchemaVersion = 1,
            EvidenceItemId = evidenceItem.EvidenceItemId,
            CaseId = evidenceItem.CaseId,
            AddedAtUtc = evidenceItem.AddedAtUtc.ToUniversalTime().ToString("O"),
            Operator = Environment.UserName,
            OriginalPath = evidenceItem.OriginalPath,
            OriginalFileName = evidenceItem.OriginalFileName,
            StoredRelativePath = evidenceItem.StoredRelativePath,
            SizeBytes = evidenceItem.SizeBytes,
            Sha256Hex = evidenceItem.Sha256Hex,
            FileExtension = evidenceItem.FileExtension,
            SourceType = evidenceItem.SourceType
        };

        await using (var manifestStream = CreateWriteStream(manifestFilePath))
        {
            await JsonSerializer.SerializeAsync(manifestStream, manifest, SerializerOptions, ct);
            await manifestStream.FlushAsync(ct);
        }

        var (storedCaseInfo, storedEvidence) = await _caseWorkspaceService.LoadCaseAsync(caseInfo.CaseId, ct);
        storedEvidence.Add(evidenceItem);
        await _caseWorkspaceService.SaveCaseAsync(storedCaseInfo, storedEvidence, ct);

        return evidenceItem;
    }

    public async Task<(bool ok, string message)> VerifyEvidenceAsync(
        CaseInfo caseInfo,
        EvidenceItem item,
        IProgress<double>? progress,
        CancellationToken ct
    )
    {
        var storedFilePath = Path.Combine(
            GetCaseRootPath(caseInfo.CaseId),
            item.StoredRelativePath.Replace('/', Path.DirectorySeparatorChar)
        );

        if (!File.Exists(storedFilePath))
        {
            return (false, "Stored evidence file is missing.");
        }

        await using var stream = CreateReadStream(storedFilePath);
        var computedHash = await ComputeHashAsync(stream, progress, ct);

        if (string.Equals(computedHash, item.Sha256Hex, StringComparison.OrdinalIgnoreCase))
        {
            return (true, "Integrity verification succeeded.");
        }

        return (false, "SHA-256 mismatch. Stored file contents changed.");
    }

    private string GetCaseRootPath(Guid caseId)
    {
        return Path.Combine(_casesRoot, caseId.ToString("D"));
    }

    private static string ResolveSourceType(string fileName)
    {
        var extension = Path.GetExtension(fileName).ToLowerInvariant();
        return extension switch
        {
            ".ufdr" => "UFDR",
            ".zip" => "ZIP",
            ".xlsx" => "XLSX",
            ".xls" => "XLSX",
            ".plist" => "PLIST",
            _ => "OTHER"
        };
    }

    private static string ToRelativePath(params string[] segments)
    {
        return string.Join("/", segments.Select(s => s.Replace('\\', '/')));
    }

    private static FileStream CreateReadStream(string path)
    {
        return new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 1024 * 64,
            FileOptions.Asynchronous | FileOptions.SequentialScan
        );
    }

    private static FileStream CreateWriteStream(string path)
    {
        return new FileStream(
            path,
            FileMode.Create,
            FileAccess.Write,
            FileShare.None,
            bufferSize: 1024 * 64,
            FileOptions.Asynchronous | FileOptions.SequentialScan
        );
    }

    private static async Task<string> CopyAndHashAsync(
        Stream source,
        Stream destination,
        long totalBytes,
        IProgress<double>? progress,
        CancellationToken ct
    )
    {
        using var hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = ArrayPool<byte>.Shared.Rent(1024 * 64);
        var bytesProcessed = 0L;

        try
        {
            while (true)
            {
                var bytesRead = await source.ReadAsync(buffer.AsMemory(0, buffer.Length), ct);
                if (bytesRead == 0)
                {
                    break;
                }

                await destination.WriteAsync(buffer.AsMemory(0, bytesRead), ct);
                hasher.AppendData(buffer, 0, bytesRead);
                bytesProcessed += bytesRead;

                if (totalBytes > 0)
                {
                    progress?.Report(bytesProcessed / (double)totalBytes);
                }
            }

            progress?.Report(1.0);

            return Convert.ToHexString(hasher.GetHashAndReset()).ToLowerInvariant();
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static async Task<string> ComputeHashAsync(
        Stream stream,
        IProgress<double>? progress,
        CancellationToken ct
    )
    {
        using var hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = ArrayPool<byte>.Shared.Rent(1024 * 64);
        var bytesProcessed = 0L;
        var totalBytes = stream.Length;

        try
        {
            while (true)
            {
                var bytesRead = await stream.ReadAsync(buffer.AsMemory(0, buffer.Length), ct);
                if (bytesRead == 0)
                {
                    break;
                }

                hasher.AppendData(buffer, 0, bytesRead);
                bytesProcessed += bytesRead;

                if (totalBytes > 0)
                {
                    progress?.Report(bytesProcessed / (double)totalBytes);
                }
            }

            progress?.Report(1.0);

            return Convert.ToHexString(hasher.GetHashAndReset()).ToLowerInvariant();
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private sealed class EvidenceManifest
    {
        public int SchemaVersion { get; set; }

        public Guid EvidenceItemId { get; set; }

        public Guid CaseId { get; set; }

        public string AddedAtUtc { get; set; } = string.Empty;

        public string Operator { get; set; } = string.Empty;

        public string OriginalPath { get; set; } = string.Empty;

        public string OriginalFileName { get; set; } = string.Empty;

        public string StoredRelativePath { get; set; } = string.Empty;

        public long SizeBytes { get; set; }

        public string Sha256Hex { get; set; } = string.Empty;

        public string FileExtension { get; set; } = string.Empty;

        public string SourceType { get; set; } = string.Empty;
    }
}
