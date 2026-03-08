namespace CaseGraph.Core.Diagnostics;

public interface IStartupStageReporter
{
    StartupStageSnapshot Current { get; }

    event EventHandler<StartupStageSnapshot>? Changed;

    void ReportStage(string stageKey, string stageText, string? detailText = null);

    void ReportCompleted(string stageKey, string stageText, string? detailText = null);

    void ReportFailure(
        string stageKey,
        string stageText,
        string detailText,
        string? diagnosticsPath = null
    );
}

public enum StartupStageStatus
{
    Running,
    Completed,
    Failed
}

public sealed record StartupStageSnapshot(
    string StageKey,
    string StageText,
    string? DetailText,
    TimeSpan Elapsed,
    StartupStageStatus Status,
    string? DiagnosticsPath
);

public static class StartupStageKeys
{
    public const string Starting = "Starting";
    public const string BuildingHost = "BuildingHost";
    public const string OpeningWorkspace = "OpeningWorkspace";
    public const string LoadingMigrations = "LoadingMigrations";
    public const string EnsuringMessageSearchIndex = "EnsuringMessageSearchIndex";
    public const string VerifyingSchema = "VerifyingSchema";
    public const string FinalizingStartup = "FinalizingStartup";
    public const string OpeningMainWindow = "OpeningMainWindow";
    public const string StartupCompleted = "StartupCompleted";
    public const string StartupFailed = "StartupFailed";
}
