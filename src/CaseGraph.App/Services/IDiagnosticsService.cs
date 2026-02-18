namespace CaseGraph.App.Services;

public interface IDiagnosticsService
{
    DiagnosticsSnapshot GetSnapshot();

    Task<IReadOnlyList<string>> ReadLastLogLinesAsync(int take, CancellationToken ct);

    string BuildDiagnosticsText(string context, string correlationId, Exception? ex);

    void CopyDiagnostics(string diagnosticsText);

    void OpenLogsFolder();
}

public sealed record DiagnosticsSnapshot(
    string AppVersion,
    string GitCommit,
    string WorkspaceRoot,
    string WorkspaceDbPath,
    string CasesRoot,
    string LogsDirectory,
    string CurrentLogPath
);
