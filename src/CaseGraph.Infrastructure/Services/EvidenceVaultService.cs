using CaseGraph.Core.Abstractions;
using CaseGraph.Core.Diagnostics;
using CaseGraph.Core.Models;
using System.Buffers;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;

namespace CaseGraph.Infrastructure.Services;

public sealed class EvidenceVaultService : IEvidenceVaultService
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true
    };

    private const int ArchiveExtractionSchemaVersion = 1;

    private readonly IClock _clock;
    private readonly IWorkspacePathProvider _pathProvider;
    private readonly ICaseWorkspaceService _caseWorkspaceService;
    private readonly IAuditLogService _auditLogService;

    public EvidenceVaultService(
        IClock clock,
        IWorkspacePathProvider pathProvider,
        ICaseWorkspaceService caseWorkspaceService,
        IAuditLogService auditLogService
    )
    {
        _clock = clock;
        _pathProvider = pathProvider;
        _caseWorkspaceService = caseWorkspaceService;
        _auditLogService = auditLogService;

        Directory.CreateDirectory(_pathProvider.CasesRoot);
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
        var copyProgress = sourceType == "ZIP"
            ? new Progress<double>(value => progress?.Report(Math.Clamp(value, 0, 1) * 0.85))
            : progress;
        await using (var sourceStream = CreateReadStream(filePath))
        await using (var destinationStream = CreateWriteStream(storedFilePath))
        {
            sizeBytes = sourceStream.Length;
            sha256Hex = await CopyAndHashAsync(
                sourceStream,
                destinationStream,
                sizeBytes,
                copyProgress,
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

        ArchiveExtractionResult? archiveExtraction = null;
        if (string.Equals(sourceType, "ZIP", StringComparison.Ordinal))
        {
            progress?.Report(0.9);
            archiveExtraction = await EnsureArchiveExtractedAsync(caseInfo.CaseId, evidenceItem, ct);
            progress?.Report(1.0);
        }

        var (storedCaseInfo, storedEvidence) = await _caseWorkspaceService.LoadCaseAsync(caseInfo.CaseId, ct);
        storedEvidence.Add(evidenceItem);
        await _caseWorkspaceService.SaveCaseAsync(storedCaseInfo, storedEvidence, ct);

        await _auditLogService.AddAsync(
            new AuditEvent
            {
                TimestampUtc = addedAtUtc,
                Operator = Environment.UserName,
                ActionType = "EvidenceImported",
                CaseId = caseInfo.CaseId,
                EvidenceItemId = evidenceItem.EvidenceItemId,
                Summary = $"Imported evidence file \"{evidenceItem.OriginalFileName}\".",
                JsonPayload = JsonSerializer.Serialize(new
                {
                    evidenceItem.OriginalFileName,
                    evidenceItem.SourceType,
                    evidenceItem.SizeBytes,
                    ExtractedEntryCount = archiveExtraction?.ExtractedRelativePaths.Count ?? 0,
                    ArchiveWarningCount = archiveExtraction?.Warnings.Count ?? 0,
                    Sha256Short = evidenceItem.Sha256Hex.Length >= 12
                        ? evidenceItem.Sha256Hex[..12]
                        : evidenceItem.Sha256Hex
                })
            },
            ct
        );

        return evidenceItem;
    }

    public async Task<ArchiveExtractionResult?> EnsureArchiveExtractedAsync(
        Guid caseId,
        EvidenceItem item,
        CancellationToken ct
    )
    {
        ArgumentNullException.ThrowIfNull(item);

        if (!string.Equals(item.FileExtension, ".zip", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(item.SourceType, "ZIP", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var caseRootPath = GetCaseRootPath(caseId);
        var storedAbsolutePath = Path.Combine(
            caseRootPath,
            item.StoredRelativePath.Replace('/', Path.DirectorySeparatorChar)
        );
        if (!File.Exists(storedAbsolutePath))
        {
            throw new FileNotFoundException("Stored archive file is missing.", storedAbsolutePath);
        }

        var derivedRootPath = Path.Combine(
            caseRootPath,
            "vault",
            item.EvidenceItemId.ToString("D"),
            "derived",
            "extracted"
        );
        var extractionManifestPath = Path.Combine(
            caseRootPath,
            "vault",
            item.EvidenceItemId.ToString("D"),
            "derived",
            "archive-extraction.json"
        );

        var cached = await TryReadExtractionManifestAsync(
            extractionManifestPath,
            item.EvidenceItemId,
            item.Sha256Hex,
            derivedRootPath,
            ct
        );
        if (cached is not null)
        {
            AppFileLogger.LogEvent(
                eventName: "ArchiveExtractionReused",
                level: "INFO",
                message: "Reused previously extracted archive contents.",
                fields: new Dictionary<string, object?>
                {
                    ["caseId"] = caseId.ToString("D"),
                    ["evidenceItemId"] = item.EvidenceItemId.ToString("D"),
                    ["fileName"] = item.OriginalFileName,
                    ["entryCount"] = cached.ExtractedRelativePaths.Count,
                    ["warningCount"] = cached.Warnings.Count
                }
            );
            return cached;
        }

        AppFileLogger.LogEvent(
            eventName: "ArchiveExtractionStarted",
            level: "INFO",
            message: "Archive extraction started.",
            fields: new Dictionary<string, object?>
            {
                ["caseId"] = caseId.ToString("D"),
                ["evidenceItemId"] = item.EvidenceItemId.ToString("D"),
                ["fileName"] = item.OriginalFileName
            }
        );

        Directory.CreateDirectory(Path.GetDirectoryName(extractionManifestPath)!);
        if (Directory.Exists(derivedRootPath))
        {
            Directory.Delete(derivedRootPath, recursive: true);
        }

        Directory.CreateDirectory(derivedRootPath);

        var extractedRelativePaths = new List<string>();
        var warnings = new List<string>();

        try
        {
            await using var archiveStream = CreateReadStream(storedAbsolutePath);
            using var archive = new ZipArchive(archiveStream, ZipArchiveMode.Read, leaveOpen: false);
            foreach (var entry in archive.Entries.OrderBy(item => item.FullName, StringComparer.Ordinal))
            {
                ct.ThrowIfCancellationRequested();

                if (string.IsNullOrEmpty(entry.Name))
                {
                    continue;
                }

                var normalizedRelativePath = NormalizeArchiveEntryPath(entry.FullName);
                if (normalizedRelativePath is null)
                {
                    var warning = $"Skipped unsafe archive entry \"{entry.FullName}\".";
                    warnings.Add(warning);
                    LogArchiveEntryIssue("ArchiveExtractionEntrySkipped", caseId, item, entry.FullName, warning);
                    continue;
                }

                var destinationPath = ResolveExtractedDestinationPath(derivedRootPath, normalizedRelativePath);
                if (destinationPath is null)
                {
                    var warning = $"Skipped out-of-bounds archive entry \"{entry.FullName}\".";
                    warnings.Add(warning);
                    LogArchiveEntryIssue("ArchiveExtractionEntrySkipped", caseId, item, entry.FullName, warning);
                    continue;
                }

                Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);

                try
                {
                    await using var entryStream = entry.Open();
                    await using var destinationStream = CreateWriteStream(destinationPath);
                    await entryStream.CopyToAsync(destinationStream, ct);
                    await destinationStream.FlushAsync(ct);
                    extractedRelativePaths.Add(normalizedRelativePath);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    var warning = $"Failed to extract archive entry \"{entry.FullName}\": {ex.GetBaseException().Message}";
                    warnings.Add(warning);
                    LogArchiveEntryIssue(
                        "ArchiveExtractionEntryFailed",
                        caseId,
                        item,
                        entry.FullName,
                        warning,
                        ex
                    );
                }
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (InvalidDataException ex)
        {
            var warning = $"Archive could not be opened for extraction: {ex.GetBaseException().Message}";
            warnings.Add(warning);
            AppFileLogger.LogEvent(
                eventName: "ArchiveExtractionFailed",
                level: "WARN",
                message: "Archive extraction failed; the original evidence item was preserved.",
                ex: ex,
                fields: new Dictionary<string, object?>
                {
                    ["caseId"] = caseId.ToString("D"),
                    ["evidenceItemId"] = item.EvidenceItemId.ToString("D"),
                    ["fileName"] = item.OriginalFileName
                }
            );
        }

        var extractionManifest = new ArchiveExtractionManifest
        {
            SchemaVersion = ArchiveExtractionSchemaVersion,
            EvidenceItemId = item.EvidenceItemId,
            CaseId = caseId,
            ExtractedAtUtc = _clock.UtcNow.ToUniversalTime().ToString("O"),
            SourceSha256Hex = item.Sha256Hex,
            ExtractedRootRelativePath = ToRelativePath(
                "vault",
                item.EvidenceItemId.ToString("D"),
                "derived",
                "extracted"
            ),
            ExtractedRelativePaths = extractedRelativePaths,
            Warnings = warnings
        };

        await using (var manifestStream = CreateWriteStream(extractionManifestPath))
        {
            await JsonSerializer.SerializeAsync(manifestStream, extractionManifest, SerializerOptions, ct);
            await manifestStream.FlushAsync(ct);
        }

        AppFileLogger.LogEvent(
            eventName: "ArchiveExtractionCompleted",
            level: "INFO",
            message: "Archive extraction completed.",
            fields: new Dictionary<string, object?>
            {
                ["caseId"] = caseId.ToString("D"),
                ["evidenceItemId"] = item.EvidenceItemId.ToString("D"),
                ["fileName"] = item.OriginalFileName,
                ["entryCount"] = extractedRelativePaths.Count,
                ["warningCount"] = warnings.Count
            }
        );

        return new ArchiveExtractionResult(
            derivedRootPath,
            extractedRelativePaths
                .OrderBy(path => path, StringComparer.Ordinal)
                .ToArray(),
            warnings.ToArray()
        );
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
            const string missingMessage = "Stored evidence file is missing.";
            await WriteVerifyAuditAsync(caseInfo, item, false, missingMessage, ct);
            return (false, missingMessage);
        }

        var expectedHash = await ResolveExpectedHashAsync(caseInfo.CaseId, item, ct);
        await using var stream = CreateReadStream(storedFilePath);
        var computedHash = await ComputeHashAsync(stream, progress, ct);

        if (string.Equals(computedHash, expectedHash, StringComparison.OrdinalIgnoreCase))
        {
            const string successMessage = "Integrity verification succeeded.";
            await WriteVerifyAuditAsync(caseInfo, item, true, successMessage, ct);
            return (true, successMessage);
        }

        const string failMessage = "SHA-256 mismatch. Stored evidence vault copy differs from recorded hash. Chain-of-custody warning: investigate storage integrity and re-import if required.";
        await WriteVerifyAuditAsync(caseInfo, item, false, failMessage, ct);
        return (false, failMessage);
    }

    private async Task<string> ResolveExpectedHashAsync(
        Guid caseId,
        EvidenceItem item,
        CancellationToken ct
    )
    {
        if (string.IsNullOrWhiteSpace(item.ManifestRelativePath))
        {
            return item.Sha256Hex;
        }

        var manifestPath = Path.Combine(
            GetCaseRootPath(caseId),
            item.ManifestRelativePath.Replace('/', Path.DirectorySeparatorChar)
        );
        if (!File.Exists(manifestPath))
        {
            return item.Sha256Hex;
        }

        await using var stream = CreateReadStream(manifestPath);
        using var json = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
        if (json.RootElement.TryGetProperty("Sha256Hex", out var shaElement))
        {
            var fromManifest = shaElement.GetString();
            if (!string.IsNullOrWhiteSpace(fromManifest))
            {
                return fromManifest.Trim();
            }
        }

        return item.Sha256Hex;
    }

    private async Task WriteVerifyAuditAsync(
        CaseInfo caseInfo,
        EvidenceItem item,
        bool ok,
        string message,
        CancellationToken ct
    )
    {
        await _auditLogService.AddAsync(
            new AuditEvent
            {
                TimestampUtc = _clock.UtcNow.ToUniversalTime(),
                Operator = Environment.UserName,
                ActionType = ok ? "EvidenceVerifiedOk" : "EvidenceVerifiedFail",
                CaseId = caseInfo.CaseId,
                EvidenceItemId = item.EvidenceItemId,
                Summary = $"{(ok ? "Integrity OK" : "Integrity FAIL")} for \"{item.OriginalFileName}\".",
                JsonPayload = JsonSerializer.Serialize(new
                {
                    item.OriginalFileName,
                    item.StoredRelativePath,
                    message
                })
            },
            ct
        );
    }

    private string GetCaseRootPath(Guid caseId)
    {
        return Path.Combine(_pathProvider.CasesRoot, caseId.ToString("D"));
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

    private static string? NormalizeArchiveEntryPath(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var normalized = value.Replace('\\', '/').Trim();
        if (normalized.StartsWith("/", StringComparison.Ordinal))
        {
            normalized = normalized.TrimStart('/');
        }

        if (normalized.Length == 0)
        {
            return null;
        }

        var segments = normalized.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (segments.Length == 0)
        {
            return null;
        }

        foreach (var segment in segments)
        {
            if (segment is "." or "..")
            {
                return null;
            }

            if (segment.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            {
                return null;
            }
        }

        return string.Join("/", segments);
    }

    private static string? ResolveExtractedDestinationPath(string extractedRootPath, string relativePath)
    {
        var fullRoot = Path.GetFullPath(extractedRootPath);
        var fullPath = Path.GetFullPath(
            Path.Combine(extractedRootPath, relativePath.Replace('/', Path.DirectorySeparatorChar))
        );
        var rootWithSeparator = fullRoot.EndsWith(Path.DirectorySeparatorChar)
            ? fullRoot
            : fullRoot + Path.DirectorySeparatorChar;

        return fullPath.StartsWith(rootWithSeparator, StringComparison.OrdinalIgnoreCase)
            ? fullPath
            : null;
    }

    private async Task<ArchiveExtractionResult?> TryReadExtractionManifestAsync(
        string manifestPath,
        Guid evidenceItemId,
        string sourceSha256Hex,
        string extractedRootPath,
        CancellationToken ct
    )
    {
        if (!File.Exists(manifestPath) || !Directory.Exists(extractedRootPath))
        {
            return null;
        }

        await using var stream = CreateReadStream(manifestPath);
        var manifest = await JsonSerializer.DeserializeAsync<ArchiveExtractionManifest>(stream, SerializerOptions, ct);
        if (manifest is null
            || manifest.SchemaVersion != ArchiveExtractionSchemaVersion
            || manifest.EvidenceItemId != evidenceItemId
            || !string.Equals(manifest.SourceSha256Hex, sourceSha256Hex, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return new ArchiveExtractionResult(
            extractedRootPath,
            manifest.ExtractedRelativePaths
                .OrderBy(path => path, StringComparer.Ordinal)
                .ToArray(),
            manifest.Warnings.ToArray()
        );
    }

    private static void LogArchiveEntryIssue(
        string eventName,
        Guid caseId,
        EvidenceItem item,
        string entryPath,
        string warning,
        Exception? ex = null
    )
    {
        AppFileLogger.LogEvent(
            eventName: eventName,
            level: "WARN",
            message: warning,
            ex: ex,
            fields: new Dictionary<string, object?>
            {
                ["caseId"] = caseId.ToString("D"),
                ["evidenceItemId"] = item.EvidenceItemId.ToString("D"),
                ["fileName"] = item.OriginalFileName,
                ["entryPath"] = entryPath
            }
        );
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

    private sealed class ArchiveExtractionManifest
    {
        public int SchemaVersion { get; set; }

        public Guid EvidenceItemId { get; set; }

        public Guid CaseId { get; set; }

        public string ExtractedAtUtc { get; set; } = string.Empty;

        public string SourceSha256Hex { get; set; } = string.Empty;

        public string ExtractedRootRelativePath { get; set; } = string.Empty;

        public List<string> ExtractedRelativePaths { get; set; } = new();

        public List<string> Warnings { get; set; } = new();
    }
}
