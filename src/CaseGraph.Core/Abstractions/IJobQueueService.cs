using CaseGraph.Core.Models;

namespace CaseGraph.Core.Abstractions;

public interface IJobQueueService
{
    Task<Guid> EnqueueAsync(JobEnqueueRequest request, CancellationToken ct);

    Task CancelAsync(Guid jobId, CancellationToken ct);

    Task<IReadOnlyList<JobInfo>> GetRecentAsync(Guid? caseId, int take, CancellationToken ct);

    IObservable<JobInfo> JobUpdates { get; }
}
