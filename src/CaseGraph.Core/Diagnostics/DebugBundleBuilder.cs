using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace CaseGraph.Core.Diagnostics;

public sealed class DebugBundleBuilder
{
    public async Task<DebugBundleBuildResult> BuildAsync(
        DebugBundleBuildRequest request,
        CancellationToken ct
    )
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.OutputZipPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.WorkspaceRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.WorkspaceDbPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.LogsDirectory);

        ct.ThrowIfCancellationRequested();

        var outputDirectory = Path.GetDirectoryName(request.OutputZipPath);
        if (!string.IsNullOrWhiteSpace(outputDirectory))
        {
            Directory.CreateDirectory(outputDirectory);
        }

        if (File.Exists(request.OutputZipPath))
        {
            File.Delete(request.OutputZipPath);
        }

        var includedEntries = new List<string>();

        await using var outputStream = new FileStream(
            request.OutputZipPath,
            FileMode.CreateNew,
            FileAccess.ReadWrite,
            FileShare.None
        );
        using var zip = new ZipArchive(outputStream, ZipArchiveMode.Create, leaveOpen: true);

        ct.ThrowIfCancellationRequested();
        AddDirectoryIfExists(zip, request.LogsDirectory, "logs", includedEntries, ct);
        AddFileIfExists(
            zip,
            request.WorkspaceDbPath,
            "workspace/workspace.db",
            includedEntries,
            ct
        );
        AddFileIfExists(
            zip,
            $"{request.WorkspaceDbPath}-wal",
            "workspace/workspace.db-wal",
            includedEntries,
            ct
        );
        AddFileIfExists(
            zip,
            $"{request.WorkspaceDbPath}-shm",
            "workspace/workspace.db-shm",
            includedEntries,
            ct
        );

        foreach (var configPath in request.ConfigurationFiles)
        {
            var configFileName = Path.GetFileName(configPath);
            if (string.IsNullOrWhiteSpace(configFileName))
            {
                continue;
            }

            AddFileIfExists(
                zip,
                configPath,
                $"config/{configFileName}",
                includedEntries,
                ct
            );
        }

        var diagnosticsPayload = new
        {
            generatedAtUtc = DateTimeOffset.UtcNow.ToString("O"),
            appVersion = request.AppVersion,
            gitCommit = request.GitCommit,
            osDescription = RuntimeInformation.OSDescription,
            dotnetVersion = Environment.Version.ToString(),
            workspaceRoot = request.WorkspaceRoot,
            workspaceDbPath = request.WorkspaceDbPath,
            logTail = request.LastLogLines
        };

        var diagnosticsEntry = zip.CreateEntry("diagnostics.json", CompressionLevel.Fastest);
        await using (var diagnosticsStream = diagnosticsEntry.Open())
        {
            await JsonSerializer.SerializeAsync(diagnosticsStream, diagnosticsPayload, cancellationToken: ct);
        }

        includedEntries.Add("diagnostics.json");
        return new DebugBundleBuildResult(request.OutputZipPath, includedEntries);
    }

    private static void AddDirectoryIfExists(
        ZipArchive zip,
        string sourceDirectory,
        string zipPrefix,
        ICollection<string> includedEntries,
        CancellationToken ct
    )
    {
        if (!Directory.Exists(sourceDirectory))
        {
            return;
        }

        var files = Directory.GetFiles(sourceDirectory, "*", SearchOption.AllDirectories);
        foreach (var filePath in files)
        {
            ct.ThrowIfCancellationRequested();
            var relativePath = Path.GetRelativePath(sourceDirectory, filePath);
            var normalizedRelative = relativePath.Replace(Path.DirectorySeparatorChar, '/');
            AddFileIfExists(
                zip,
                filePath,
                $"{zipPrefix}/{normalizedRelative}",
                includedEntries,
                ct
            );
        }
    }

    private static void AddFileIfExists(
        ZipArchive zip,
        string sourcePath,
        string entryName,
        ICollection<string> includedEntries,
        CancellationToken ct
    )
    {
        if (!File.Exists(sourcePath))
        {
            return;
        }

        ct.ThrowIfCancellationRequested();
        var entry = zip.CreateEntry(entryName, CompressionLevel.Fastest);
        using var entryStream = entry.Open();
        using var sourceStream = File.OpenRead(sourcePath);
        sourceStream.CopyTo(entryStream);
        includedEntries.Add(entryName);
    }
}

public sealed record DebugBundleBuildRequest(
    string OutputZipPath,
    string LogsDirectory,
    string WorkspaceRoot,
    string WorkspaceDbPath,
    string AppVersion,
    string GitCommit,
    IReadOnlyList<string> LastLogLines,
    IReadOnlyList<string> ConfigurationFiles
);

public sealed record DebugBundleBuildResult(
    string BundlePath,
    IReadOnlyCollection<string> IncludedEntries
);
