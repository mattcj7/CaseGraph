namespace CaseGraph.App.Services;

public enum MaintenanceTaskState
{
    Idle,
    Pending,
    Running,
    Completed,
    Failed
}

public enum MaintenanceTaskCategory
{
    MessageSearchIndex,
    SearchPreparation,
    TimelinePreparation,
    ReportsPreparation,
    IncidentWindowPreparation
}

public enum ReadinessBannerTone
{
    Info,
    Success,
    Error
}

public sealed record MaintenanceTaskRequest(
    string TaskKey,
    string DisplayName,
    MaintenanceTaskCategory Category,
    Guid? CaseId = null,
    ReadinessFeature? Feature = null,
    bool SupportsCancellation = false,
    string? PendingStatusText = null,
    string? RunningStatusText = null
);

public sealed record MaintenanceProgressUpdate(
    string StatusText,
    string? DetailText = null
);

public sealed record MaintenanceTaskSnapshot(
    string TaskKey,
    string DisplayName,
    MaintenanceTaskCategory Category,
    MaintenanceTaskState State,
    Guid? CaseId = null,
    ReadinessFeature? Feature = null,
    string? StatusText = null,
    string? DetailText = null,
    string? ErrorMessage = null,
    DateTimeOffset? RequestedAtUtc = null,
    DateTimeOffset? StartedAtUtc = null,
    DateTimeOffset? CompletedAtUtc = null,
    DateTimeOffset? LastUpdatedAtUtc = null,
    int RequestCount = 0
)
{
    public bool IsActive => State is MaintenanceTaskState.Pending or MaintenanceTaskState.Running;
}

public sealed record MaintenanceRequestResult(
    bool WasQueued,
    bool WasDeduplicated,
    Task ExecutionTask,
    MaintenanceTaskSnapshot Snapshot
);

public sealed record ReadinessBannerState(
    bool IsVisible,
    ReadinessBannerTone Tone,
    string Title,
    string Message,
    bool IsBusy = false
)
{
    public static ReadinessBannerState Hidden { get; } = new(
        IsVisible: false,
        Tone: ReadinessBannerTone.Info,
        Title: string.Empty,
        Message: string.Empty
    );
}

public static class MaintenanceTaskKeys
{
    public static string MessageSearchIndex(Guid caseId)
    {
        return $"message-search-index:{caseId:D}";
    }
}

public static class ReadinessBannerStateFactory
{
    public static ReadinessBannerState FromMaintenance(
        ReadinessFeature feature,
        MaintenanceTaskSnapshot? snapshot,
        bool blocksCurrentAction
    )
    {
        if (snapshot is null || snapshot.State == MaintenanceTaskState.Idle)
        {
            return ReadinessBannerState.Hidden;
        }

        var featureName = ToDisplayName(feature);
        return snapshot.State switch
        {
            MaintenanceTaskState.Pending => new ReadinessBannerState(
                true,
                ReadinessBannerTone.Info,
                blocksCurrentAction
                    ? $"Preparing {featureName}"
                    : $"{featureName} maintenance queued",
                blocksCurrentAction
                    ? snapshot.DetailText
                        ?? "Maintenance is queued before this action can continue."
                    : snapshot.DetailText
                        ?? "Background maintenance has been queued. You can keep using this page while it runs.",
                IsBusy: true
            ),
            MaintenanceTaskState.Running => new ReadinessBannerState(
                true,
                ReadinessBannerTone.Info,
                blocksCurrentAction
                    ? $"{featureName} maintenance in progress"
                    : $"{featureName} background maintenance in progress",
                blocksCurrentAction
                    ? snapshot.DetailText
                        ?? "Maintenance is running before this action can continue."
                    : snapshot.DetailText
                        ?? "Background maintenance is running. You can keep using this page while it completes.",
                IsBusy: true
            ),
            MaintenanceTaskState.Completed => new ReadinessBannerState(
                true,
                ReadinessBannerTone.Success,
                $"{featureName} ready",
                snapshot.DetailText
                    ?? snapshot.StatusText
                    ?? "Background maintenance completed successfully."
            ),
            MaintenanceTaskState.Failed => new ReadinessBannerState(
                true,
                ReadinessBannerTone.Error,
                $"{featureName} maintenance failed",
                snapshot.ErrorMessage
                    ?? snapshot.DetailText
                    ?? "Retry the action. If it fails again, check diagnostics logs."
            ),
            _ => ReadinessBannerState.Hidden
        };
    }

    private static string ToDisplayName(ReadinessFeature feature)
    {
        return feature switch
        {
            ReadinessFeature.Search => "Search",
            ReadinessFeature.Timeline => "Timeline",
            ReadinessFeature.Reports => "Reports",
            ReadinessFeature.IncidentWindow => "Incident Window",
            _ => feature.ToString()
        };
    }
}
