using CaseGraph.Core.Abstractions;
using CaseGraph.Core.Models;
using CaseGraph.Infrastructure.Persistence;
using CaseGraph.Infrastructure.Persistence.Entities;
using Microsoft.EntityFrameworkCore;

namespace CaseGraph.Infrastructure.Services;

public sealed class JobQueryService : IJobQueryService
{
    private readonly IDbContextFactory<WorkspaceDbContext> _dbContextFactory;
    private readonly IWorkspaceDatabaseInitializer _databaseInitializer;

    public JobQueryService(
        IDbContextFactory<WorkspaceDbContext> dbContextFactory,
        IWorkspaceDatabaseInitializer databaseInitializer
    )
    {
        _dbContextFactory = dbContextFactory;
        _databaseInitializer = databaseInitializer;
    }

    public async Task<JobInfo?> GetLatestJobForEvidenceAsync(
        Guid caseId,
        Guid evidenceItemId,
        string jobType,
        CancellationToken ct
    )
    {
        if (caseId == Guid.Empty)
        {
            throw new ArgumentException("Case id is required.", nameof(caseId));
        }

        if (evidenceItemId == Guid.Empty)
        {
            throw new ArgumentException("Evidence item id is required.", nameof(evidenceItemId));
        }

        if (string.IsNullOrWhiteSpace(jobType))
        {
            throw new ArgumentException("Job type is required.", nameof(jobType));
        }

        await _databaseInitializer.EnsureInitializedAsync(ct);

        await using var db = await _dbContextFactory.CreateDbContextAsync(ct);
        var normalizedJobType = jobType.Trim();

        var latestJobId = await db.JobOrderKeys
            .AsNoTracking()
            .Where(job => job.CaseId == caseId)
            .Where(job => job.EvidenceItemId == evidenceItemId)
            .Where(job => job.JobType == normalizedJobType)
            .OrderByDescending(
                job =>
                    EF.Property<string?>(job, nameof(JobOrderKeyRecord.CompletedAtUtc))
                    ?? EF.Property<string?>(job, nameof(JobOrderKeyRecord.StartedAtUtc))
                    ?? EF.Property<string>(job, nameof(JobOrderKeyRecord.CreatedAtUtc))
            )
            .ThenByDescending(job => EF.Property<string>(job, nameof(JobOrderKeyRecord.CreatedAtUtc)))
            .ThenByDescending(job => job.JobId)
            .Select(job => (Guid?)job.JobId)
            .FirstOrDefaultAsync(ct);

        if (!latestJobId.HasValue)
        {
            return null;
        }

        var latestRecord = await db.Jobs
            .AsNoTracking()
            .FirstOrDefaultAsync(job => job.JobId == latestJobId.Value, ct);

        return latestRecord is null ? null : MapJobInfo(latestRecord);
    }

    public async Task<IReadOnlyList<JobInfo>> GetRecentJobsAsync(Guid? caseId, int take, CancellationToken ct)
    {
        await _databaseInitializer.EnsureInitializedAsync(ct);

        var boundedTake = take <= 0 ? 20 : take;

        await using var db = await _dbContextFactory.CreateDbContextAsync(ct);
        var query = db.JobOrderKeys.AsNoTracking().AsQueryable();
        if (caseId.HasValue)
        {
            query = query.Where(job => job.CaseId == caseId.Value);
        }

        var orderedJobIds = await query
            .OrderByDescending(
                job =>
                    EF.Property<string?>(job, nameof(JobOrderKeyRecord.CompletedAtUtc))
                    ?? EF.Property<string?>(job, nameof(JobOrderKeyRecord.StartedAtUtc))
                    ?? EF.Property<string>(job, nameof(JobOrderKeyRecord.CreatedAtUtc))
            )
            .ThenByDescending(job => EF.Property<string>(job, nameof(JobOrderKeyRecord.CreatedAtUtc)))
            .ThenByDescending(job => job.JobId)
            .Take(boundedTake)
            .Select(job => job.JobId)
            .ToListAsync(ct);

        if (orderedJobIds.Count == 0)
        {
            return Array.Empty<JobInfo>();
        }

        var records = await db.Jobs
            .AsNoTracking()
            .Where(job => orderedJobIds.Contains(job.JobId))
            .ToListAsync(ct);
        var recordsById = records.ToDictionary(job => job.JobId);

        var ordered = new List<JobInfo>(orderedJobIds.Count);
        foreach (var jobId in orderedJobIds)
        {
            if (recordsById.TryGetValue(jobId, out var record))
            {
                ordered.Add(MapJobInfo(record));
            }
        }

        return ordered;
    }

    private static JobInfo MapJobInfo(JobRecord record)
    {
        return new JobInfo
        {
            JobId = record.JobId,
            CreatedAtUtc = record.CreatedAtUtc,
            StartedAtUtc = record.StartedAtUtc,
            CompletedAtUtc = record.CompletedAtUtc,
            Status = Enum.TryParse<JobStatus>(record.Status, true, out var parsedStatus)
                ? parsedStatus
                : JobStatus.Failed,
            JobType = record.JobType,
            CaseId = record.CaseId,
            EvidenceItemId = record.EvidenceItemId,
            Progress = Math.Clamp(record.Progress, 0, 1),
            StatusMessage = record.StatusMessage,
            ErrorMessage = record.ErrorMessage,
            JsonPayload = record.JsonPayload,
            CorrelationId = record.CorrelationId,
            Operator = record.Operator
        };
    }
}
