namespace CaseGraph.App.Services;

public enum ReadinessPhase
{
    Startup,
    CaseOpen,
    FeatureOpen
}

public enum ReadinessFeature
{
    Search,
    Timeline,
    Reports,
    IncidentWindow
}

public sealed record ReadinessProgress(
    ReadinessPhase Phase,
    string StatusText,
    string DetailText,
    double? Progress = null,
    Guid? CaseId = null,
    ReadinessFeature? Feature = null
);

public sealed record ReadinessResult(bool WorkPerformed, string Summary);
