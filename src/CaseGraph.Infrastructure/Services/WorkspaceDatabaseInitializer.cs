using CaseGraph.Core.Abstractions;
using CaseGraph.Infrastructure.Persistence;
using CaseGraph.Infrastructure.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using System.Text.Json;

namespace CaseGraph.Infrastructure.Services;

public sealed class WorkspaceDatabaseInitializer : IWorkspaceDatabaseInitializer
{
    private readonly IDbContextFactory<WorkspaceDbContext> _dbContextFactory;
    private readonly SemaphoreSlim _semaphore = new(1, 1);
    private bool _initialized;

    public WorkspaceDatabaseInitializer(IDbContextFactory<WorkspaceDbContext> dbContextFactory)
    {
        _dbContextFactory = dbContextFactory;
    }

    public async Task EnsureInitializedAsync(CancellationToken ct)
    {
        if (_initialized)
        {
            return;
        }

        await _semaphore.WaitAsync(ct);
        try
        {
            if (_initialized)
            {
                return;
            }

            await using var db = await _dbContextFactory.CreateDbContextAsync(ct);
            await db.Database.EnsureCreatedAsync(ct);
            await MarkRunningJobsAsAbandonedAsync(db, ct);
            _initialized = true;
        }
        finally
        {
            _semaphore.Release();
        }
    }

    private static async Task MarkRunningJobsAsAbandonedAsync(
        WorkspaceDbContext db,
        CancellationToken ct
    )
    {
        var runningJobs = await db.Jobs
            .Where(job => job.Status == "Running")
            .ToListAsync(ct);

        if (runningJobs.Count == 0)
        {
            return;
        }

        var now = DateTimeOffset.UtcNow;
        foreach (var job in runningJobs)
        {
            job.Status = "Abandoned";
            job.CompletedAtUtc = now;
            job.StatusMessage = "App shutdown before completion.";
            job.ErrorMessage ??= "Job abandoned after unexpected app shutdown.";

            db.AuditEvents.Add(
                new AuditEventRecord
                {
                    AuditEventId = Guid.NewGuid(),
                    TimestampUtc = now,
                    Operator = string.IsNullOrWhiteSpace(job.Operator)
                        ? Environment.UserName
                        : job.Operator,
                    ActionType = "JobAbandoned",
                    CaseId = job.CaseId,
                    EvidenceItemId = job.EvidenceItemId,
                    Summary = $"{job.JobType} job abandoned after app shutdown.",
                    JsonPayload = JsonSerializer.Serialize(new
                    {
                        job.JobId,
                        job.JobType,
                        job.CorrelationId,
                        job.StatusMessage
                    })
                }
            );
        }

        await db.SaveChangesAsync(ct);
    }
}
