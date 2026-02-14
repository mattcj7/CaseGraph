using CaseGraph.Core.Abstractions;
using CaseGraph.Core.Diagnostics;
using Microsoft.Extensions.Hosting;

namespace CaseGraph.Infrastructure.Services;

public sealed class JobRunnerHostedService : BackgroundService
{
    private readonly IWorkspaceDbInitializer _workspaceDbInitializer;
    private readonly JobQueueService _jobQueueService;

    public JobRunnerHostedService(
        IWorkspaceDbInitializer workspaceDbInitializer,
        JobQueueService jobQueueService
    )
    {
        _workspaceDbInitializer = workspaceDbInitializer;
        _jobQueueService = jobQueueService;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        AppFileLogger.Log("[JobRunner] Hosted service starting.");
        await _workspaceDbInitializer.InitializeAsync(stoppingToken);
        await _jobQueueService.PrimeQueueAsync(stoppingToken);
        AppFileLogger.Log("[JobRunner] Queue primed.");

        while (!stoppingToken.IsCancellationRequested)
        {
            Guid jobId;
            try
            {
                jobId = await _jobQueueService.DequeueAsync(stoppingToken);
                AppFileLogger.Log($"[JobRunner] Dequeued jobId={jobId:D}");
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                AppFileLogger.Log("[JobRunner] Dequeue canceled by host shutdown.");
                return;
            }

            try
            {
                AppFileLogger.Log($"[JobRunner] Executing jobId={jobId:D}");
                await _jobQueueService.ExecuteAsync(jobId, stoppingToken);
                AppFileLogger.Log($"[JobRunner] Execution finished jobId={jobId:D}");
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                AppFileLogger.Log("[JobRunner] Execution canceled by host shutdown.");
                return;
            }
            catch (Exception ex)
            {
                // Continue processing queued jobs after unexpected execution errors.
                AppFileLogger.LogException($"[JobRunner] Unexpected execution failure jobId={jobId:D}", ex);
            }
        }
    }
}
