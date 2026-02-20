using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Data.Sqlite;

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

        var correlationId = AppFileLogger.GetScopeValue("correlationId");
        if (string.IsNullOrWhiteSpace(correlationId))
        {
            correlationId = AppFileLogger.NewCorrelationId();
        }

        using var correlationScope = AppFileLogger.BeginScope(
            new Dictionary<string, object?>
            {
                ["correlationId"] = correlationId,
                ["workspaceDbPath"] = request.WorkspaceDbPath,
                ["outputZipPath"] = request.OutputZipPath
            }
        );

        AppFileLogger.LogEvent(
            eventName: "DebugBundleBuildStarted",
            level: "INFO",
            message: "Debug bundle build started."
        );

        string? snapshotPath = null;
        try
        {
            snapshotPath = CreateSnapshotPath();
            await CreateWorkspaceSnapshotAsync(request.WorkspaceDbPath, snapshotPath, ct);

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
            if (!string.IsNullOrWhiteSpace(request.DumpsDirectory))
            {
                AddDirectoryIfExists(zip, request.DumpsDirectory!, "dumps", includedEntries, ct);
            }

            if (!string.IsNullOrWhiteSpace(request.SessionDirectory))
            {
                AddDirectoryIfExists(zip, request.SessionDirectory!, "session", includedEntries, ct);
            }
            AddFileIfExists(
                zip,
                snapshotPath,
                "workspace.snapshot.db",
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
                workspaceSnapshotEntry = "workspace.snapshot.db",
                logTail = request.LastLogLines
            };

            var diagnosticsEntry = zip.CreateEntry("diagnostics.json", CompressionLevel.Fastest);
            await using (var diagnosticsStream = diagnosticsEntry.Open())
            {
                await JsonSerializer.SerializeAsync(diagnosticsStream, diagnosticsPayload, cancellationToken: ct);
            }

            includedEntries.Add("diagnostics.json");

            AppFileLogger.LogEvent(
                eventName: "DebugBundleBuildCompleted",
                level: "INFO",
                message: "Debug bundle build completed.",
                fields: new Dictionary<string, object?>
                {
                    ["includedEntryCount"] = includedEntries.Count
                }
            );

            return new DebugBundleBuildResult(request.OutputZipPath, includedEntries);
        }
        catch (OperationCanceledException)
        {
            AppFileLogger.LogEvent(
                eventName: "DebugBundleBuildCanceled",
                level: "INFO",
                message: "Debug bundle build canceled."
            );
            throw;
        }
        catch (Exception ex)
        {
            AppFileLogger.LogEvent(
                eventName: "DebugBundleBuildFailed",
                level: "ERROR",
                message: "Debug bundle build failed.",
                ex: ex
            );
            throw;
        }
        finally
        {
            DeleteSnapshotBestEffort(snapshotPath);
        }
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

    private static async Task CreateWorkspaceSnapshotAsync(
        string sourceDbPath,
        string snapshotPath,
        CancellationToken ct
    )
    {
        if (!File.Exists(sourceDbPath))
        {
            throw new FileNotFoundException(
                $"Workspace database was not found at \"{sourceDbPath}\".",
                sourceDbPath
            );
        }

        var snapshotDirectory = Path.GetDirectoryName(snapshotPath);
        if (!string.IsNullOrWhiteSpace(snapshotDirectory))
        {
            Directory.CreateDirectory(snapshotDirectory);
        }

        var sourceBuilder = new SqliteConnectionStringBuilder
        {
            DataSource = sourceDbPath,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false
        };
        var snapshotBuilder = new SqliteConnectionStringBuilder
        {
            DataSource = snapshotPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false
        };

        await using (var sourceConnection = new SqliteConnection(sourceBuilder.ToString()))
        {
            sourceConnection.DefaultTimeout = 30;
            await sourceConnection.OpenAsync(ct);

            await using (var snapshotConnection = new SqliteConnection(snapshotBuilder.ToString()))
            {
                snapshotConnection.DefaultTimeout = 30;
                await snapshotConnection.OpenAsync(ct);
                sourceConnection.BackupDatabase(snapshotConnection);
            }
        }

        SqliteConnection.ClearAllPools();

        var snapshotInfo = new FileInfo(snapshotPath);
        if (!snapshotInfo.Exists || snapshotInfo.Length == 0)
        {
            throw new IOException("SQLite snapshot export failed to produce a non-empty snapshot file.");
        }
    }

    private static string CreateSnapshotPath()
    {
        var snapshotRoot = Path.Combine(Path.GetTempPath(), "CaseGraphDebug", "Snapshots");
        Directory.CreateDirectory(snapshotRoot);
        return Path.Combine(snapshotRoot, $"workspace.snapshot.{Guid.NewGuid():N}.db");
    }

    private static void DeleteSnapshotBestEffort(string? snapshotPath)
    {
        if (string.IsNullOrWhiteSpace(snapshotPath))
        {
            return;
        }

        try
        {
            if (File.Exists(snapshotPath))
            {
                File.Delete(snapshotPath);
            }

            var directory = Path.GetDirectoryName(snapshotPath);
            if (!string.IsNullOrWhiteSpace(directory)
                && Directory.Exists(directory)
                && !Directory.EnumerateFileSystemEntries(directory).Any())
            {
                Directory.Delete(directory);
            }
        }
        catch
        {
            // Best-effort cleanup only.
        }
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
    IReadOnlyList<string> ConfigurationFiles,
    string? DumpsDirectory = null,
    string? SessionDirectory = null
);

public sealed record DebugBundleBuildResult(
    string BundlePath,
    IReadOnlyCollection<string> IncludedEntries
);
