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

    public DiagnosticsService(IWorkspacePathProvider workspacePathProvider)
    {
        _workspacePathProvider = workspacePathProvider;
    }

    public DiagnosticsSnapshot GetSnapshot()
    {
        var (appVersion, gitCommit) = GetVersionMetadata();
        return new DiagnosticsSnapshot(
            appVersion,
            gitCommit,
            _workspacePathProvider.WorkspaceRoot,
            _workspacePathProvider.WorkspaceDbPath,
            _workspacePathProvider.CasesRoot,
            AppFileLogger.GetLogDirectory(),
            AppFileLogger.GetCurrentLogPath()
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
