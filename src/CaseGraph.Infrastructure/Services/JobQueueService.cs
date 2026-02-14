using CaseGraph.Core.Abstractions;
using CaseGraph.Core.Diagnostics;
using CaseGraph.Core.Models;
using CaseGraph.Infrastructure.Persistence;
using CaseGraph.Infrastructure.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using System.Collections.Concurrent;
using System.Text.Json;
using System.Threading.Channels;

namespace CaseGraph.Infrastructure.Services;

public sealed class JobQueueService : IJobQueueService
{
    public const string EvidenceImportJobType = "EvidenceImport";
    public const string EvidenceVerifyJobType = "EvidenceVerify";
    public const string MessagesIngestJobType = "MessagesIngest";
    public const string TestLongRunningJobType = "TestLongRunningDelay";

    private static readonly JsonSerializerOptions PayloadSerializerOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly IDbContextFactory<WorkspaceDbContext> _dbContextFactory;
    private readonly IWorkspaceDatabaseInitializer _databaseInitializer;
    private readonly IClock _clock;
    private readonly ICaseWorkspaceService _caseWorkspaceService;
    private readonly IEvidenceVaultService _evidenceVaultService;
    private readonly IMessageIngestService _messageIngestService;
    private readonly IAuditLogService _auditLogService;
    private readonly Channel<Guid> _dispatchChannel;
    private readonly ConcurrentDictionary<Guid, CancellationTokenSource> _runningJobs = new();
    private readonly ConcurrentDictionary<Guid, byte> _pendingRunningCancels = new();
    private readonly JobInfoObservable _jobUpdates = new();
    private readonly SemaphoreSlim _primeSemaphore = new(1, 1);

    private bool _isPrimed;

    public JobQueueService(
        IDbContextFactory<WorkspaceDbContext> dbContextFactory,
        IWorkspaceDatabaseInitializer databaseInitializer,
        IClock clock,
        ICaseWorkspaceService caseWorkspaceService,
        IEvidenceVaultService evidenceVaultService,
        IMessageIngestService messageIngestService,
        IAuditLogService auditLogService
    )
    {
        _dbContextFactory = dbContextFactory;
        _databaseInitializer = databaseInitializer;
        _clock = clock;
        _caseWorkspaceService = caseWorkspaceService;
        _evidenceVaultService = evidenceVaultService;
        _messageIngestService = messageIngestService;
        _auditLogService = auditLogService;
        _dispatchChannel = Channel.CreateUnbounded<Guid>(
            new UnboundedChannelOptions
            {
                SingleReader = true,
                SingleWriter = false
            }
        );
    }

    public IObservable<JobInfo> JobUpdates => _jobUpdates;

    public async Task<Guid> EnqueueAsync(JobEnqueueRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (!IsSupportedJobType(request.JobType))
        {
            throw new NotSupportedException($"Unsupported job type \"{request.JobType}\".");
        }

        await _databaseInitializer.EnsureInitializedAsync(ct);

        var now = _clock.UtcNow.ToUniversalTime();
        var jobRecord = new JobRecord
        {
            JobId = Guid.NewGuid(),
            CreatedAtUtc = now,
            Status = JobStatus.Queued.ToString(),
            JobType = request.JobType.Trim(),
            CaseId = request.CaseId,
            EvidenceItemId = request.EvidenceItemId,
            Progress = 0,
            StatusMessage = "Queued.",
            JsonPayload = string.IsNullOrWhiteSpace(request.JsonPayload)
                ? "{}"
                : request.JsonPayload,
            CorrelationId = Guid.NewGuid().ToString("N"),
            Operator = Environment.UserName
        };

        await using (var db = await _dbContextFactory.CreateDbContextAsync(ct))
        {
            db.Jobs.Add(jobRecord);
            await db.SaveChangesAsync(ct);
        }

        var queuedInfo = MapJobInfo(jobRecord);
        _jobUpdates.Publish(queuedInfo);
        AppFileLogger.Log(
            $"[JobQueue] Enqueued jobId={jobRecord.JobId:D} type={jobRecord.JobType} case={jobRecord.CaseId?.ToString("D") ?? "(none)"} evidence={jobRecord.EvidenceItemId?.ToString("D") ?? "(none)"} correlation={jobRecord.CorrelationId}"
        );
        await WriteLifecycleAuditAsync(
            queuedInfo,
            "JobQueued",
            $"{queuedInfo.JobType} job queued.",
            ct
        );

        await _dispatchChannel.Writer.WriteAsync(jobRecord.JobId, ct);
        return jobRecord.JobId;
    }

    public async Task CancelAsync(Guid jobId, CancellationToken ct)
    {
        await _databaseInitializer.EnsureInitializedAsync(ct);

        await using var db = await _dbContextFactory.CreateDbContextAsync(ct);
        var jobRecord = await db.Jobs.FirstOrDefaultAsync(j => j.JobId == jobId, ct);
        if (jobRecord is null)
        {
            return;
        }

        if (jobRecord.Status == JobStatus.Queued.ToString())
        {
            var now = _clock.UtcNow.ToUniversalTime();
            jobRecord.Status = JobStatus.Canceled.ToString();
            jobRecord.CompletedAtUtc = now;
            jobRecord.Progress = 1;
            jobRecord.StatusMessage = "Canceled";
            jobRecord.ErrorMessage = null;

            await db.SaveChangesAsync(ct);

            var canceledInfo = MapJobInfo(jobRecord);
            _jobUpdates.Publish(canceledInfo);
            AppFileLogger.Log(
                $"[JobQueue] Cancel requested jobId={jobRecord.JobId:D} state=Queued action=MarkedCanceled"
            );
            await WriteLifecycleAuditAsync(
                canceledInfo,
                "JobCanceled",
                $"{canceledInfo.JobType} job canceled before execution.",
                ct
            );
            return;
        }

        if (IsTerminalStatus(jobRecord.Status))
        {
            AppFileLogger.Log(
                $"[JobQueue] Cancel requested jobId={jobRecord.JobId:D} state={jobRecord.Status} action=AlreadyTerminal"
            );
            return;
        }

        if (jobRecord.Status != JobStatus.Running.ToString())
        {
            AppFileLogger.Log(
                $"[JobQueue] Cancel requested jobId={jobRecord.JobId:D} state={jobRecord.Status} action=Ignored"
            );
            return;
        }

        jobRecord.StatusMessage = "Cancellation requested.";
        await db.SaveChangesAsync(ct);
        _jobUpdates.Publish(MapJobInfo(jobRecord));

        if (_runningJobs.TryGetValue(jobId, out var runningJobCts))
        {
            runningJobCts.Cancel();
            _pendingRunningCancels.TryRemove(jobId, out _);
            AppFileLogger.Log(
                $"[JobQueue] Cancel requested jobId={jobRecord.JobId:D} state=Running action=CtsCanceled"
            );
        }
        else
        {
            _pendingRunningCancels[jobId] = 0;
            AppFileLogger.Log(
                $"[JobQueue] Cancel requested jobId={jobRecord.JobId:D} state=Running action=PendingUntilCtsAvailable"
            );
        }
    }

    public async Task<IReadOnlyList<JobInfo>> GetRecentAsync(Guid? caseId, int take, CancellationToken ct)
    {
        await _databaseInitializer.EnsureInitializedAsync(ct);

        var boundedTake = take <= 0 ? 20 : take;

        await using var db = await _dbContextFactory.CreateDbContextAsync(ct);
        var query = db.Jobs.AsNoTracking().AsQueryable();
        if (caseId.HasValue)
        {
            query = query.Where(job => job.CaseId == caseId.Value);
        }

        var records = await query.ToListAsync(ct);

        return records
            .OrderByDescending(job => job.CreatedAtUtc)
            .Take(boundedTake)
            .Select(MapJobInfo)
            .ToList();
    }

    public async Task PrimeQueueAsync(CancellationToken ct)
    {
        if (_isPrimed)
        {
            return;
        }

        await _primeSemaphore.WaitAsync(ct);
        try
        {
            if (_isPrimed)
            {
                return;
            }

            await _databaseInitializer.EnsureInitializedAsync(ct);

            await using var db = await _dbContextFactory.CreateDbContextAsync(ct);
            var queuedJobIds = (await db.Jobs
                .AsNoTracking()
                .Where(job => job.Status == JobStatus.Queued.ToString())
                .ToListAsync(ct))
                .OrderBy(job => job.CreatedAtUtc)
                .Select(job => job.JobId)
                .ToList();

            foreach (var jobId in queuedJobIds)
            {
                await _dispatchChannel.Writer.WriteAsync(jobId, ct);
            }

            _isPrimed = true;
        }
        finally
        {
            _primeSemaphore.Release();
        }
    }

    public ValueTask<Guid> DequeueAsync(CancellationToken ct)
    {
        return _dispatchChannel.Reader.ReadAsync(ct);
    }

    public async Task ExecuteAsync(Guid jobId, CancellationToken stoppingToken)
    {
        await _databaseInitializer.EnsureInitializedAsync(stoppingToken);

        await using (var db = await _dbContextFactory.CreateDbContextAsync(stoppingToken))
        {
            var jobRecord = await db.Jobs.FirstOrDefaultAsync(j => j.JobId == jobId, stoppingToken);
            if (jobRecord is null || jobRecord.Status != JobStatus.Queued.ToString())
            {
                return;
            }

            var now = _clock.UtcNow.ToUniversalTime();
            jobRecord.Status = JobStatus.Running.ToString();
            jobRecord.StartedAtUtc ??= now;
            jobRecord.StatusMessage = $"Running {jobRecord.JobType}...";
            await db.SaveChangesAsync(stoppingToken);

            var runningInfo = MapJobInfo(jobRecord);
            _jobUpdates.Publish(runningInfo);
            AppFileLogger.Log(
                $"[JobQueue] Started jobId={runningInfo.JobId:D} type={runningInfo.JobType} correlation={runningInfo.CorrelationId}"
            );
            await WriteLifecycleAuditAsync(
                runningInfo,
                "JobStarted",
                $"{runningInfo.JobType} job started.",
                stoppingToken
            );
        }

        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        if (!_runningJobs.TryAdd(jobId, linkedCts))
        {
            return;
        }

        if (_pendingRunningCancels.TryRemove(jobId, out _))
        {
            linkedCts.Cancel();
        }

        try
        {
            var jobToExecute = await GetJobRecordAsync(jobId, linkedCts.Token);
            if (jobToExecute is null)
            {
                return;
            }

            var executionStartedAt = DateTimeOffset.UtcNow;
            switch (jobToExecute.JobType)
            {
                case EvidenceImportJobType:
                    await ExecuteEvidenceImportAsync(jobToExecute, linkedCts.Token);
                    await CompleteSucceededAsync(
                        jobId,
                        "Evidence import completed.",
                        CancellationToken.None
                    );
                    break;
                case EvidenceVerifyJobType:
                    await ExecuteEvidenceVerifyAsync(jobToExecute, linkedCts.Token);
                    await CompleteSucceededAsync(
                        jobId,
                        "Evidence verify completed.",
                        CancellationToken.None
                    );
                    break;
                case MessagesIngestJobType:
                {
                    var ingestResult = await ExecuteMessagesIngestAsync(jobToExecute, linkedCts.Token);
                    await CompleteSucceededAsync(
                        jobId,
                        ComposeMessagesIngestSuccessSummary(ingestResult),
                        CancellationToken.None
                    );
                    await WriteMessagesIngestSummaryAsync(jobToExecute, ingestResult.MessagesExtracted, linkedCts.Token);
                    break;
                }
                case TestLongRunningJobType:
                    await ExecuteTestLongRunningAsync(jobToExecute, linkedCts.Token);
                    await CompleteSucceededAsync(
                        jobId,
                        "Test long-running job completed.",
                        CancellationToken.None
                    );
                    break;
                default:
                    throw new NotSupportedException($"Unsupported job type \"{jobToExecute.JobType}\".");
            }

            AppFileLogger.Log(
                $"[JobQueue] Completed jobId={jobToExecute.JobId:D} type={jobToExecute.JobType} elapsedMs={(DateTimeOffset.UtcNow - executionStartedAt).TotalMilliseconds:0}"
            );
        }
        catch (OperationCanceledException) when (linkedCts.IsCancellationRequested)
        {
            await CompleteCanceledAsync(jobId, CancellationToken.None);
            AppFileLogger.Log($"[JobQueue] Canceled jobId={jobId:D}");
        }
        catch (Exception ex)
        {
            await CompleteFailedAsync(jobId, ex.Message, CancellationToken.None);
            AppFileLogger.LogException($"[JobQueue] Failed jobId={jobId:D}", ex);
        }
        finally
        {
            await EnsureTerminalStateWrittenAsync(jobId, linkedCts.IsCancellationRequested, CancellationToken.None);
            _runningJobs.TryRemove(jobId, out _);
            _pendingRunningCancels.TryRemove(jobId, out _);
        }
    }

    private async Task ExecuteEvidenceImportAsync(JobRecord jobRecord, CancellationToken ct)
    {
        var payload = JsonSerializer.Deserialize<EvidenceImportPayload>(
            jobRecord.JsonPayload,
            PayloadSerializerOptions
        );

        if (payload is null || payload.SchemaVersion != 1 || payload.CaseId == Guid.Empty)
        {
            throw new InvalidOperationException("Invalid EvidenceImport payload.");
        }

        if (payload.Files is null || payload.Files.Count == 0)
        {
            throw new InvalidOperationException("EvidenceImport payload does not include files.");
        }

        var (caseInfo, _) = await _caseWorkspaceService.LoadCaseAsync(payload.CaseId, ct);
        var totalFiles = payload.Files.Count;

        for (var fileIndex = 0; fileIndex < totalFiles; fileIndex++)
        {
            ct.ThrowIfCancellationRequested();

            var filePath = payload.Files[fileIndex];
            var fileName = Path.GetFileName(filePath);
            await ReportProgressAsync(
                jobRecord.JobId,
                fileIndex / (double)totalFiles,
                $"Importing {fileIndex + 1}/{totalFiles}: {fileName}",
                ct
            );

            var fileProgress = new Progress<double>(progress =>
            {
                _ = SafeReportProgressAsync(
                    jobRecord.JobId,
                    (fileIndex + progress) / totalFiles,
                    $"Importing {fileIndex + 1}/{totalFiles}: {fileName}",
                    ct
                );
            });

            await _evidenceVaultService.ImportEvidenceFileAsync(caseInfo, filePath, fileProgress, ct);
        }
    }

    private async Task ExecuteEvidenceVerifyAsync(JobRecord jobRecord, CancellationToken ct)
    {
        var payload = JsonSerializer.Deserialize<EvidenceVerifyPayload>(
            jobRecord.JsonPayload,
            PayloadSerializerOptions
        );

        if (
            payload is null
            || payload.SchemaVersion != 1
            || payload.CaseId == Guid.Empty
            || payload.EvidenceItemId == Guid.Empty
        )
        {
            throw new InvalidOperationException("Invalid EvidenceVerify payload.");
        }

        var (caseInfo, evidence) = await _caseWorkspaceService.LoadCaseAsync(payload.CaseId, ct);
        var evidenceItem = evidence.FirstOrDefault(item => item.EvidenceItemId == payload.EvidenceItemId);
        if (evidenceItem is null)
        {
            throw new FileNotFoundException(
                $"Evidence item was not found for {payload.EvidenceItemId:D}."
            );
        }

        var verifyProgress = new Progress<double>(progress =>
        {
            _ = SafeReportProgressAsync(
                jobRecord.JobId,
                progress,
                $"Verifying {evidenceItem.OriginalFileName}...",
                ct
            );
        });

        await ReportProgressAsync(jobRecord.JobId, 0, $"Verifying {evidenceItem.OriginalFileName}...", ct);
        var (ok, message) = await _evidenceVaultService.VerifyEvidenceAsync(
            caseInfo,
            evidenceItem,
            verifyProgress,
            ct
        );

        if (!ok)
        {
            throw new InvalidOperationException(message);
        }
    }

    private async Task<MessageIngestResult> ExecuteMessagesIngestAsync(JobRecord jobRecord, CancellationToken ct)
    {
        var payload = JsonSerializer.Deserialize<MessagesIngestPayload>(
            jobRecord.JsonPayload,
            PayloadSerializerOptions
        );

        if (
            payload is null
            || payload.SchemaVersion != 1
            || payload.CaseId == Guid.Empty
            || payload.EvidenceItemId == Guid.Empty
        )
        {
            throw new InvalidOperationException("Invalid MessagesIngest payload.");
        }

        await _databaseInitializer.EnsureInitializedAsync(ct);
        await using var db = await _dbContextFactory.CreateDbContextAsync(ct);
        var evidence = await db.EvidenceItems
            .AsNoTracking()
            .FirstOrDefaultAsync(
                item => item.EvidenceItemId == payload.EvidenceItemId && item.CaseId == payload.CaseId,
                ct
            );

        if (evidence is null)
        {
            throw new FileNotFoundException(
                $"Evidence item was not found for {payload.EvidenceItemId:D}."
            );
        }

        await ReportProgressAsync(
            jobRecord.JobId,
            0.05,
            $"Parsing messages from {evidence.OriginalFileName}...",
            ct
        );

        AppFileLogger.Log(
            $"[MessagesIngest] Job start jobId={jobRecord.JobId:D} correlation={jobRecord.CorrelationId} case={payload.CaseId:D} evidence={payload.EvidenceItemId:D} ext={evidence.FileExtension} file={evidence.OriginalFileName}"
        );

        var ingestStopwatch = System.Diagnostics.Stopwatch.StartNew();
        var lastLoggedPercent = -1;
        var lastLoggedAt = DateTimeOffset.MinValue;

        var detailedProgress = new Progress<MessageIngestProgress>(update =>
        {
            var clampedProgress = Math.Clamp(update.FractionComplete, 0, 1);
            var statusMessage = BuildIngestStatusMessage(update);
            _ = SafeReportProgressAsync(
                jobRecord.JobId,
                clampedProgress,
                statusMessage,
                ct
            );

            var percent = (int)Math.Round(clampedProgress * 100, MidpointRounding.AwayFromZero);
            var now = DateTimeOffset.UtcNow;
            if (percent >= lastLoggedPercent + 5 || (now - lastLoggedAt).TotalSeconds >= 2)
            {
                lastLoggedPercent = percent;
                lastLoggedAt = now;
                AppFileLogger.Log(
                    $"[MessagesIngest] Progress jobId={jobRecord.JobId:D} correlation={jobRecord.CorrelationId} {statusMessage} ({percent}%)"
                );
            }
        });

        var ingestResult = await _messageIngestService.IngestMessagesDetailedFromEvidenceAsync(
            payload.CaseId,
            evidence,
            detailedProgress,
            logContext: $"job={jobRecord.JobId:D} correlation={jobRecord.CorrelationId}",
            ct
        );

        await ReportProgressAsync(
            jobRecord.JobId,
            1,
            ComposeMessagesIngestSuccessSummary(ingestResult),
            ct
        );

        AppFileLogger.Log(
            $"[MessagesIngest] Job complete jobId={jobRecord.JobId:D} correlation={jobRecord.CorrelationId} parsed={ingestResult.MessagesExtracted} threads={ingestResult.ThreadsCreated} elapsedMs={ingestStopwatch.ElapsedMilliseconds} status=\"{ComposeMessagesIngestSuccessSummary(ingestResult)}\""
        );

        return ingestResult;
    }

    private async Task ExecuteTestLongRunningAsync(JobRecord jobRecord, CancellationToken ct)
    {
        var payload = JsonSerializer.Deserialize<TestLongRunningPayload>(
            jobRecord.JsonPayload,
            PayloadSerializerOptions
        );

        if (payload is null || payload.SchemaVersion != 1 || payload.DelayMilliseconds <= 0)
        {
            throw new InvalidOperationException("Invalid TestLongRunningDelay payload.");
        }

        const int steps = 20;
        var delayPerStep = Math.Max(1, payload.DelayMilliseconds / steps);

        for (var step = 1; step <= steps; step++)
        {
            ct.ThrowIfCancellationRequested();
            await Task.Delay(delayPerStep, ct);
            await ReportProgressAsync(
                jobRecord.JobId,
                step / (double)steps,
                "Executing deterministic test long-running job...",
                ct
            );
        }
    }

    private async Task SafeReportProgressAsync(
        Guid jobId,
        double progress,
        string statusMessage,
        CancellationToken ct
    )
    {
        try
        {
            await ReportProgressAsync(jobId, progress, statusMessage, ct);
        }
        catch (OperationCanceledException)
        {
            // Ignore transient callback updates once the job has been canceled.
        }
    }

    private async Task ReportProgressAsync(
        Guid jobId,
        double progress,
        string statusMessage,
        CancellationToken ct
    )
    {
        var clampedProgress = Math.Clamp(progress, 0, 1);

        await _databaseInitializer.EnsureInitializedAsync(ct);
        await using var db = await _dbContextFactory.CreateDbContextAsync(ct);
        var jobRecord = await db.Jobs.FirstOrDefaultAsync(j => j.JobId == jobId, ct);
        if (jobRecord is null || jobRecord.Status != JobStatus.Running.ToString())
        {
            return;
        }

        if (
            Math.Abs(jobRecord.Progress - clampedProgress) < 0.005
            && string.Equals(jobRecord.StatusMessage, statusMessage, StringComparison.Ordinal)
        )
        {
            return;
        }

        jobRecord.Progress = clampedProgress;
        jobRecord.StatusMessage = statusMessage;
        await db.SaveChangesAsync(ct);

        _jobUpdates.Publish(MapJobInfo(jobRecord));
    }

    private async Task CompleteSucceededAsync(Guid jobId, string statusMessage, CancellationToken ct)
    {
        var completedInfo = await CompleteAsync(
            jobId,
            JobStatus.Succeeded,
            $"Succeeded: {statusMessage}",
            errorMessage: null,
            ct
        );

        if (completedInfo is null)
        {
            return;
        }

        await WriteLifecycleAuditAsync(
            completedInfo,
            "JobSucceeded",
            $"{completedInfo.JobType} job succeeded.",
            ct
        );
    }

    private async Task CompleteFailedAsync(Guid jobId, string errorMessage, CancellationToken ct)
    {
        var normalizedError = string.IsNullOrWhiteSpace(errorMessage)
            ? "Unhandled error."
            : errorMessage.Trim();
        var completedInfo = await CompleteAsync(
            jobId,
            JobStatus.Failed,
            $"Failed: {normalizedError}",
            normalizedError,
            ct
        );

        if (completedInfo is null)
        {
            return;
        }

        await WriteLifecycleAuditAsync(
            completedInfo,
            "JobFailed",
            $"{completedInfo.JobType} job failed.",
            ct
        );
    }

    private async Task CompleteCanceledAsync(Guid jobId, CancellationToken ct)
    {
        var completedInfo = await CompleteAsync(
            jobId,
            JobStatus.Canceled,
            "Canceled",
            errorMessage: null,
            ct
        );

        if (completedInfo is null)
        {
            return;
        }

        await WriteLifecycleAuditAsync(
            completedInfo,
            "JobCanceled",
            $"{completedInfo.JobType} job canceled.",
            ct
        );
    }

    private async Task<JobInfo?> CompleteAsync(
        Guid jobId,
        JobStatus status,
        string statusMessage,
        string? errorMessage,
        CancellationToken ct
    )
    {
        await _databaseInitializer.EnsureInitializedAsync(ct);

        await using var db = await _dbContextFactory.CreateDbContextAsync(ct);
        var jobRecord = await db.Jobs.FirstOrDefaultAsync(j => j.JobId == jobId, ct);
        if (jobRecord is null || IsTerminalStatus(jobRecord.Status))
        {
            return null;
        }

        jobRecord.Status = status.ToString();
        jobRecord.CompletedAtUtc = _clock.UtcNow.ToUniversalTime();
        jobRecord.StatusMessage = statusMessage;
        jobRecord.ErrorMessage = errorMessage;
        jobRecord.Progress = 1;

        await db.SaveChangesAsync(ct);

        var jobInfo = MapJobInfo(jobRecord);
        _jobUpdates.Publish(jobInfo);
        return jobInfo;
    }

    private async Task<JobRecord?> GetJobRecordAsync(Guid jobId, CancellationToken ct)
    {
        await _databaseInitializer.EnsureInitializedAsync(ct);
        await using var db = await _dbContextFactory.CreateDbContextAsync(ct);
        return await db.Jobs
            .AsNoTracking()
            .FirstOrDefaultAsync(j => j.JobId == jobId, ct);
    }

    private async Task WriteLifecycleAuditAsync(
        JobInfo job,
        string actionType,
        string summary,
        CancellationToken ct
    )
    {
        await _auditLogService.AddAsync(
            new AuditEvent
            {
                TimestampUtc = _clock.UtcNow.ToUniversalTime(),
                Operator = Environment.UserName,
                ActionType = actionType,
                CaseId = job.CaseId,
                EvidenceItemId = job.EvidenceItemId,
                Summary = summary,
                JsonPayload = JsonSerializer.Serialize(new
                {
                    job.JobId,
                    job.JobType,
                    Status = job.Status.ToString(),
                    job.StatusMessage,
                    job.Progress,
                    job.CorrelationId
                })
            },
            ct
        );
    }

    private async Task WriteMessagesIngestSummaryAsync(
        JobRecord jobRecord,
        int ingestedCount,
        CancellationToken ct
    )
    {
        if (jobRecord.CaseId is null || jobRecord.EvidenceItemId is null)
        {
            return;
        }

        await _databaseInitializer.EnsureInitializedAsync(ct);
        await using var db = await _dbContextFactory.CreateDbContextAsync(ct);
        var byPlatform = await db.MessageEvents
            .AsNoTracking()
            .Where(e => e.CaseId == jobRecord.CaseId && e.EvidenceItemId == jobRecord.EvidenceItemId)
            .GroupBy(e => e.Platform)
            .Select(g => new
            {
                Platform = g.Key,
                Count = g.Count()
            })
            .ToListAsync(ct);

        await _auditLogService.AddAsync(
            new AuditEvent
            {
                TimestampUtc = _clock.UtcNow.ToUniversalTime(),
                Operator = Environment.UserName,
                ActionType = "MessagesIngested",
                CaseId = jobRecord.CaseId,
                EvidenceItemId = jobRecord.EvidenceItemId,
                Summary = ingestedCount == 0
                    ? "Messages ingest completed with no parsed messages."
                    : $"Messages ingest completed with {ingestedCount} parsed message(s).",
                JsonPayload = JsonSerializer.Serialize(new
                {
                    jobRecord.JobId,
                    TotalMessages = ingestedCount,
                    ByPlatform = byPlatform.ToDictionary(item => item.Platform, item => item.Count)
                })
            },
            ct
        );
    }

    private static bool IsSupportedJobType(string jobType)
    {
        return jobType is EvidenceImportJobType
            or EvidenceVerifyJobType
            or MessagesIngestJobType
            or TestLongRunningJobType;
    }

    private static bool IsTerminalStatus(string status)
    {
        return status is nameof(JobStatus.Succeeded)
            or nameof(JobStatus.Failed)
            or nameof(JobStatus.Canceled)
            or nameof(JobStatus.Abandoned);
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

    private static string BuildIngestStatusMessage(MessageIngestProgress progress)
    {
        var phase = string.IsNullOrWhiteSpace(progress.Phase)
            ? "Parsing \"Messages\"..."
            : progress.Phase.Trim();

        if (progress.Processed.HasValue && progress.Total.HasValue && progress.Total.Value > 0)
        {
            return $"{phase} ({progress.Processed.Value} / {progress.Total.Value})";
        }

        return phase;
    }

    private static string ComposeMessagesIngestSuccessSummary(MessageIngestResult ingestResult)
    {
        if (!string.IsNullOrWhiteSpace(ingestResult.SummaryOverride))
        {
            return ingestResult.SummaryOverride.Trim();
        }

        return $"Extracted {ingestResult.MessagesExtracted} message(s).";
    }

    private async Task EnsureTerminalStateWrittenAsync(
        Guid jobId,
        bool cancellationRequested,
        CancellationToken ct
    )
    {
        await _databaseInitializer.EnsureInitializedAsync(ct);
        await using var db = await _dbContextFactory.CreateDbContextAsync(ct);
        var jobRecord = await db.Jobs.FirstOrDefaultAsync(j => j.JobId == jobId, ct);
        if (jobRecord is null || IsTerminalStatus(jobRecord.Status))
        {
            return;
        }

        jobRecord.Status = cancellationRequested ? JobStatus.Canceled.ToString() : JobStatus.Failed.ToString();
        jobRecord.CompletedAtUtc = _clock.UtcNow.ToUniversalTime();
        jobRecord.Progress = 1;
        jobRecord.StatusMessage = cancellationRequested
            ? "Canceled"
            : "Failed: Terminal state repair after unexpected runner flow.";
        jobRecord.ErrorMessage = cancellationRequested
            ? null
            : "Terminal state repair after unexpected runner flow.";
        await db.SaveChangesAsync(ct);
        _jobUpdates.Publish(MapJobInfo(jobRecord));
    }

    private sealed class EvidenceImportPayload
    {
        public int SchemaVersion { get; set; }

        public Guid CaseId { get; set; }

        public List<string> Files { get; set; } = new();
    }

    private sealed class EvidenceVerifyPayload
    {
        public int SchemaVersion { get; set; }

        public Guid CaseId { get; set; }

        public Guid EvidenceItemId { get; set; }
    }

    private sealed class MessagesIngestPayload
    {
        public int SchemaVersion { get; set; }

        public Guid CaseId { get; set; }

        public Guid EvidenceItemId { get; set; }
    }

    private sealed class TestLongRunningPayload
    {
        public int SchemaVersion { get; set; }

        public int DelayMilliseconds { get; set; }
    }
}
