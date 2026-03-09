namespace CaseGraph.App.Services;

public interface ICaseOpenReadinessService
{
    Task<ReadinessResult> EnsureReadyAsync(
        Guid caseId,
        IProgress<ReadinessProgress>? progress,
        CancellationToken ct
    );
}
