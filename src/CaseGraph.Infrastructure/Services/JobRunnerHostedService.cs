using CaseGraph.Core.Abstractions;
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
        await _workspaceDbInitializer.InitializeAsync(stoppingToken);
        await _jobQueueService.PrimeQueueAsync(stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            Guid jobId;
            try
            {
                jobId = await _jobQueueService.DequeueAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }

            try
            {
                await _jobQueueService.ExecuteAsync(jobId, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch
            {
                // Continue processing queued jobs after unexpected execution errors.
            }
        }
    }
}
