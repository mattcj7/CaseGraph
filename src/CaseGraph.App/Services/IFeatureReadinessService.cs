namespace CaseGraph.App.Services;

public interface IFeatureReadinessService
{
    Task<ReadinessResult> EnsureReadyAsync(
        ReadinessFeature feature,
        Guid? caseId,
        bool requiresMessageSearchIndex,
        IProgress<ReadinessProgress>? progress,
        CancellationToken ct
    );
}
