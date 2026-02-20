using CaseGraph.Core.Abstractions;
using CaseGraph.Core.Diagnostics;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text;
using System.Windows;

namespace CaseGraph.App.Services;

public sealed class DiagnosticsService : IDiagnosticsService
{
    private readonly IWorkspacePathProvider _workspacePathProvider;
    private readonly DebugBundleBuilder _debugBundleBuilder;
    private readonly ICrashDumpService _crashDumpService;
    private readonly ISessionJournal _sessionJournal;
    private readonly IAppRuntimePaths _runtimePaths;

    public DiagnosticsService(
        IWorkspacePathProvider workspacePathProvider,
        DebugBundleBuilder debugBundleBuilder,
        ICrashDumpService crashDumpService,
        ISessionJournal sessionJournal,
        IAppRuntimePaths runtimePaths
    )
    {
        _workspacePathProvider = workspacePathProvider;
        _debugBundleBuilder = debugBundleBuilder;
        _crashDumpService = crashDumpService;
        _sessionJournal = sessionJournal;
        _runtimePaths = runtimePaths;
    }

    public DiagnosticsSnapshot GetSnapshot()
    {
        var (appVersion, gitCommit) = GetVersionMetadata();
        var crashDumpSettings = _crashDumpService.GetSettings();
        return new DiagnosticsSnapshot(
            appVersion,
            gitCommit,
            _workspacePathProvider.WorkspaceRoot,
            _workspacePathProvider.WorkspaceDbPath,
            _workspacePathProvider.CasesRoot,
            AppFileLogger.GetLogDirectory(),
            AppFileLogger.GetCurrentLogPath(),
            crashDumpSettings.Enabled,
            crashDumpSettings.DumpFolder,
            _sessionJournal.SessionDirectory,
            _sessionJournal.JournalPath,
            _sessionJournal.PreviousSessionEndedUnexpectedly
        );
    }

    public async Task<IReadOnlyList<string>> ReadLastLogLinesAsync(int take, CancellationToken ct)
    {
        if (take <= 0)
        {
            return Array.Empty<string>();
        }

        var snapshot = GetSnapshot();
        if (!File.Exists(snapshot.CurrentLogPath))
        {
            return Array.Empty<string>();
        }

        var allLines = await File.ReadAllLinesAsync(snapshot.CurrentLogPath, ct);
        if (allLines.Length <= take)
        {
            return allLines;
        }

        return allLines.Skip(allLines.Length - take).ToArray();
    }

    public string BuildDiagnosticsText(string context, string correlationId, Exception? ex)
    {
        var snapshot = GetSnapshot();
        var lastLines = AppFileLogger.ReadLastLogLines(50);

        var diagnostics = new StringBuilder();
        diagnostics.AppendLine($"Context: {context}");
        diagnostics.AppendLine($"CorrelationId: {correlationId}");
        diagnostics.AppendLine($"TimestampUtc: {DateTimeOffset.UtcNow:O}");
        diagnostics.AppendLine($"AppVersion: {snapshot.AppVersion}");
        diagnostics.AppendLine($"GitCommit: {snapshot.GitCommit}");
        diagnostics.AppendLine($"WorkspaceRoot: {snapshot.WorkspaceRoot}");
        diagnostics.AppendLine($"WorkspaceDbPath: {snapshot.WorkspaceDbPath}");
        diagnostics.AppendLine($"CasesRoot: {snapshot.CasesRoot}");
        diagnostics.AppendLine($"LogsDirectory: {snapshot.LogsDirectory}");
        diagnostics.AppendLine($"CurrentLogPath: {snapshot.CurrentLogPath}");
        diagnostics.AppendLine($"CrashDumpsEnabled: {snapshot.CrashDumpsEnabled}");
        diagnostics.AppendLine($"DumpsDirectory: {snapshot.DumpsDirectory}");
        diagnostics.AppendLine($"SessionDirectory: {snapshot.SessionDirectory}");
        diagnostics.AppendLine($"SessionJournalPath: {snapshot.SessionJournalPath}");
        diagnostics.AppendLine($"PreviousSessionEndedUnexpectedly: {snapshot.PreviousSessionEndedUnexpectedly}");
        diagnostics.AppendLine();
        diagnostics.AppendLine("Exception:");
        diagnostics.AppendLine(ex?.ToString() ?? "(none)");
        diagnostics.AppendLine();
        diagnostics.AppendLine("LastLogLines:");
        if (lastLines.Count == 0)
        {
            diagnostics.AppendLine("(none)");
        }
        else
        {
            foreach (var line in lastLines)
            {
                diagnostics.AppendLine(line);
            }
        }

        return diagnostics.ToString();
    }

    public void CopyDiagnostics(string diagnosticsText)
    {
        if (string.IsNullOrWhiteSpace(diagnosticsText))
        {
            return;
        }

        Clipboard.SetText(diagnosticsText);
    }

    public void OpenLogsFolder()
    {
        var snapshot = GetSnapshot();
        Directory.CreateDirectory(snapshot.LogsDirectory);

        Process.Start(
            new ProcessStartInfo
            {
                FileName = snapshot.LogsDirectory,
                UseShellExecute = true
            }
        );
    }

    public void SetCrashDumpsEnabled(bool enabled)
    {
        _crashDumpService.SetEnabled(enabled);
    }

    public void OpenDumpsFolder()
    {
        Directory.CreateDirectory(_runtimePaths.DumpsDirectory);
        Process.Start(
            new ProcessStartInfo
            {
                FileName = _runtimePaths.DumpsDirectory,
                UseShellExecute = true
            }
        );
    }

    public async Task<string> ExportDebugBundleAsync(string outputZipPath, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(outputZipPath);

        var snapshot = GetSnapshot();
        var logTail = await ReadLastLogLinesAsync(2000, ct);
        var configurationFiles = ResolveConfigurationFiles(snapshot.WorkspaceRoot);

        AppFileLogger.LogEvent(
            eventName: "DebugBundleExportStarted",
            level: "INFO",
            message: "Debug bundle export started.",
            fields: new Dictionary<string, object?>
            {
                ["outputZipPath"] = outputZipPath
            }
        );

        var request = new DebugBundleBuildRequest(
            OutputZipPath: outputZipPath,
            LogsDirectory: snapshot.LogsDirectory,
            WorkspaceRoot: snapshot.WorkspaceRoot,
            WorkspaceDbPath: snapshot.WorkspaceDbPath,
            AppVersion: snapshot.AppVersion,
            GitCommit: snapshot.GitCommit,
            LastLogLines: logTail,
            ConfigurationFiles: configurationFiles,
            DumpsDirectory: snapshot.DumpsDirectory,
            SessionDirectory: snapshot.SessionDirectory
        );

        try
        {
            var result = await _debugBundleBuilder.BuildAsync(request, ct);
            AppFileLogger.LogEvent(
                eventName: "DebugBundleExported",
                level: "INFO",
                message: "Debug bundle exported.",
                fields: new Dictionary<string, object?>
                {
                    ["bundlePath"] = result.BundlePath,
                    ["includedEntryCount"] = result.IncludedEntries.Count
                }
            );

            return result.BundlePath;
        }
        catch (OperationCanceledException)
        {
            AppFileLogger.LogEvent(
                eventName: "DebugBundleExportCanceled",
                level: "INFO",
                message: "Debug bundle export canceled.",
                fields: new Dictionary<string, object?>
                {
                    ["outputZipPath"] = outputZipPath
                }
            );
            throw;
        }
        catch (Exception ex)
        {
            AppFileLogger.LogEvent(
                eventName: "DebugBundleExportFailed",
                level: "ERROR",
                message: "Debug bundle export failed.",
                ex: ex,
                fields: new Dictionary<string, object?>
                {
                    ["outputZipPath"] = outputZipPath
                }
            );
            throw;
        }
    }

    private static IReadOnlyList<string> ResolveConfigurationFiles(string workspaceRoot)
    {
        var candidates = new[]
        {
            Path.Combine(AppContext.BaseDirectory, "appsettings.json"),
            Path.Combine(AppContext.BaseDirectory, "appsettings.Development.json"),
            Path.Combine(AppContext.BaseDirectory, "CaseGraph.App.runtimeconfig.json"),
            Path.Combine(workspaceRoot, "workspace.settings.json"),
            Path.Combine(workspaceRoot, "settings.json")
        };

        return candidates
            .Where(File.Exists)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static (string Version, string GitCommit) GetVersionMetadata()
    {
        var assembly = Assembly.GetEntryAssembly() ?? Assembly.GetExecutingAssembly();
        var version = assembly.GetName().Version?.ToString() ?? "unknown";
        var informationalVersion = assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion;
        if (!string.IsNullOrWhiteSpace(informationalVersion))
        {
            version = informationalVersion!;
        }

        var commit = Environment.GetEnvironmentVariable("GIT_COMMIT");
        if (string.IsNullOrWhiteSpace(commit))
        {
            var plusIndex = version.IndexOf('+');
            if (plusIndex >= 0 && plusIndex + 1 < version.Length)
            {
                commit = version[(plusIndex + 1)..];
            }
        }

        return (version, string.IsNullOrWhiteSpace(commit) ? "(unavailable)" : commit);
    }
}
