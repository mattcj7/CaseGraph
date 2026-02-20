namespace CaseGraph.App.Services;

public interface IDiagnosticsService
{
    DiagnosticsSnapshot GetSnapshot();

    Task<IReadOnlyList<string>> ReadLastLogLinesAsync(int take, CancellationToken ct);

    string BuildDiagnosticsText(string context, string correlationId, Exception? ex);

    void CopyDiagnostics(string diagnosticsText);

    void OpenLogsFolder();

    void SetCrashDumpsEnabled(bool enabled);

    void OpenDumpsFolder();

    Task<string> ExportDebugBundleAsync(string outputZipPath, CancellationToken ct);
}

public sealed record DiagnosticsSnapshot(
    string AppVersion,
    string GitCommit,
    string WorkspaceRoot,
    string WorkspaceDbPath,
    string CasesRoot,
    string LogsDirectory,
    string CurrentLogPath,
    bool CrashDumpsEnabled,
    string DumpsDirectory,
    string SessionDirectory,
    string SessionJournalPath,
    bool PreviousSessionEndedUnexpectedly
);
