using CaseGraph.Core.Models;

namespace CaseGraph.Core.Abstractions;

public interface IJobQueryService
{
    Task<JobInfo?> GetLatestJobForEvidenceAsync(
        Guid caseId,
        Guid evidenceItemId,
        string jobType,
        CancellationToken ct
    );

    Task<IReadOnlyList<JobInfo>> GetRecentJobsAsync(Guid? caseId, int take, CancellationToken ct);
}
