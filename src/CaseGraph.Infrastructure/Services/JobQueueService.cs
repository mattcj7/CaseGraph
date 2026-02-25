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
    public const string TargetPresenceIndexRebuildJobType = "TargetPresenceIndexRebuild";
    public const string TestLongRunningJobType = "TestLongRunningDelay";

    private static readonly JsonSerializerOptions PayloadSerializerOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };
    private static readonly TimeSpan ProgressPersistMinimumInterval = TimeSpan.FromMilliseconds(300);
    private const double ProgressPersistMinimumDelta = 0.10;

    private readonly IDbContextFactory<WorkspaceDbContext> _dbContextFactory;
    private readonly IWorkspaceDatabaseInitializer _databaseInitializer;
    private readonly IClock _clock;
    private readonly ICaseWorkspaceService _caseWorkspaceService;
    private readonly IEvidenceVaultService _evidenceVaultService;
    private readonly IMessageIngestService _messageIngestService;
    private readonly ITargetMessagePresenceIndexService? _targetMessagePresenceIndexService;
    private readonly IAuditLogService _auditLogService;
    private readonly IJobQueryService _jobQueryService;
    private readonly IWorkspaceWriteGate _workspaceWriteGate;
    private readonly Channel<Guid> _dispatchChannel;
    private readonly ConcurrentDictionary<Guid, CancellationTokenSource> _runningJobs = new();
    private readonly ConcurrentDictionary<Guid, byte> _pendingRunningCancels = new();
    private readonly ConcurrentDictionary<Guid, byte> _progressDropWarnings = new();
    private readonly ConcurrentDictionary<Guid, PersistedProgressState> _persistedProgressStates = new();
    private readonly ConcurrentDictionary<Guid, JobInfo> _latestJobInfos = new();
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
        IAuditLogService auditLogService,
        IJobQueryService jobQueryService,
        IWorkspaceWriteGate workspaceWriteGate,
        ITargetMessagePresenceIndexService? targetMessagePresenceIndexService = null
    )
    {
        _dbContextFactory = dbContextFactory;
        _databaseInitializer = databaseInitializer;
        _clock = clock;
        _caseWorkspaceService = caseWorkspaceService;
        _evidenceVaultService = evidenceVaultService;
        _messageIngestService = messageIngestService;
        _targetMessagePresenceIndexService = targetMessagePresenceIndexService;
        _auditLogService = auditLogService;
        _jobQueryService = jobQueryService;
        _workspaceWriteGate = workspaceWriteGate;
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

        if (string.Equals(request.JobType, TestLongRunningJobType, StringComparison.Ordinal)
            && !IsDebugBuild())
        {
            throw new NotSupportedException(
                "TestLongRunningDelay jobs are only available in DEBUG builds."
            );
        }

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

        var enqueueOutcome = await ExecuteJobWriteWithRetryAsync(
            operationName: "Enqueue",
            jobId: jobRecord.JobId,
            correlationId: jobRecord.CorrelationId,
            async writeCt =>
            {
                await using var db = await _dbContextFactory.CreateDbContextAsync(writeCt);
                if (ShouldDeduplicateEvidenceVerify(request))
                {
                    var existing = await FindActiveEvidenceVerifyDuplicateAsync(
                        db,
                        request.CaseId!.Value,
                        request.EvidenceItemId!.Value,
                        writeCt
                    );

                    if (existing is not null)
                    {
                        return EnqueueWriteOutcome.Deduplicated(existing);
                    }
                }

                db.Jobs.Add(jobRecord);
                await db.SaveChangesAsync(writeCt);
                return EnqueueWriteOutcome.Queued(jobRecord);
            },
            ct
        );

        if (enqueueOutcome.DeduplicatedJob is not null)
        {
            LogJobEvent(
                enqueueOutcome.DeduplicatedJob,
                eventName: "JobEnqueueDeduplicated",
                level: "INFO",
                message: "Reused existing active verify job."
            );
            return enqueueOutcome.DeduplicatedJob.JobId;
        }

        var queuedInfo = MapJobInfo(enqueueOutcome.QueuedJob!);
        PublishJobUpdate(queuedInfo);
        LogJobEvent(jobRecord, "JobQueued", "INFO", "Job queued.");
        await WriteLifecycleAuditAsync(
            queuedInfo,
            "JobQueued",
            $"{queuedInfo.JobType} job queued.",
            ct
        );

        await _dispatchChannel.Writer.WriteAsync(queuedInfo.JobId, ct);
        return queuedInfo.JobId;
    }

    public async Task CancelAsync(Guid jobId, CancellationToken ct)
    {
        var cancelOutcome = await ExecuteJobWriteWithRetryAsync(
            operationName: "Cancel",
            jobId: jobId,
            correlationId: null,
            async writeCt =>
            {
                await using var db = await _dbContextFactory.CreateDbContextAsync(writeCt);
                var jobRecord = await db.Jobs.FirstOrDefaultAsync(j => j.JobId == jobId, writeCt);
                if (jobRecord is null)
                {
                    return CancelWriteOutcome.NotFound();
                }

                if (jobRecord.Status == JobStatus.Queued.ToString())
                {
                    var now = _clock.UtcNow.ToUniversalTime();
                    jobRecord.Status = JobStatus.Canceled.ToString();
                    jobRecord.CompletedAtUtc = now;
                    jobRecord.Progress = 1;
                    jobRecord.StatusMessage = "Canceled";
                    jobRecord.ErrorMessage = null;
                    await db.SaveChangesAsync(writeCt);
                    return CancelWriteOutcome.Completed(CancelAction.MarkedCanceled, MapJobInfo(jobRecord));
                }

                if (IsTerminalStatus(jobRecord.Status))
                {
                    return CancelWriteOutcome.Completed(CancelAction.AlreadyTerminal, MapJobInfo(jobRecord));
                }

                if (jobRecord.Status != JobStatus.Running.ToString())
                {
                    return CancelWriteOutcome.Completed(CancelAction.IgnoredNotRunning, MapJobInfo(jobRecord));
                }

                jobRecord.StatusMessage = "Cancellation requested.";
                await db.SaveChangesAsync(writeCt);
                return CancelWriteOutcome.Completed(
                    CancelAction.MarkedCancelRequested,
                    MapJobInfo(jobRecord)
                );
            },
            ct
        );

        if (!cancelOutcome.Found || cancelOutcome.JobInfo is null)
        {
            return;
        }

        var canceledInfo = cancelOutcome.JobInfo;
        using var jobScope = AppFileLogger.BeginJobScope(
            canceledInfo.JobId,
            canceledInfo.JobType,
            canceledInfo.CorrelationId,
            canceledInfo.CaseId,
            canceledInfo.EvidenceItemId
        );

        switch (cancelOutcome.Action)
        {
            case CancelAction.MarkedCanceled:
                PublishJobUpdate(canceledInfo);
                _persistedProgressStates.TryRemove(jobId, out _);
                AppFileLogger.LogEvent(
                    eventName: "JobCancelRequested",
                    level: "INFO",
                    message: "Cancel request marked queued job as canceled.",
                    fields: new Dictionary<string, object?>
                    {
                        ["state"] = JobStatus.Queued.ToString(),
                        ["action"] = "MarkedCanceled"
                    }
                );
                await WriteLifecycleAuditAsync(
                    canceledInfo,
                    "JobCanceled",
                    $"{canceledInfo.JobType} job canceled before execution.",
                    ct
                );
                break;
            case CancelAction.AlreadyTerminal:
                AppFileLogger.LogEvent(
                    eventName: "JobCancelRequested",
                    level: "INFO",
                    message: "Cancel request ignored because job is already terminal.",
                    fields: new Dictionary<string, object?>
                    {
                        ["state"] = canceledInfo.Status.ToString(),
                        ["action"] = "AlreadyTerminal"
                    }
                );
                break;
            case CancelAction.IgnoredNotRunning:
                AppFileLogger.LogEvent(
                    eventName: "JobCancelRequested",
                    level: "WARN",
                    message: "Cancel request ignored because job is not running.",
                    fields: new Dictionary<string, object?>
                    {
                        ["state"] = canceledInfo.Status.ToString(),
                        ["action"] = "Ignored"
                    }
                );
                break;
            case CancelAction.MarkedCancelRequested:
                PublishJobUpdate(canceledInfo);
                if (_runningJobs.TryGetValue(jobId, out var runningJobCts))
                {
                    runningJobCts.Cancel();
                    _pendingRunningCancels.TryRemove(jobId, out _);
                    AppFileLogger.LogEvent(
                        eventName: "JobCancelRequested",
                        level: "INFO",
                        message: "Cancel request signaled running job token.",
                        fields: new Dictionary<string, object?>
                        {
                            ["state"] = JobStatus.Running.ToString(),
                            ["action"] = "CtsCanceled"
                        }
                    );
                }
                else
                {
                    _pendingRunningCancels[jobId] = 0;
                    AppFileLogger.LogEvent(
                        eventName: "JobCancelRequested",
                        level: "INFO",
                        message: "Cancel request queued until running token is available.",
                        fields: new Dictionary<string, object?>
                        {
                            ["state"] = JobStatus.Running.ToString(),
                            ["action"] = "PendingUntilCtsAvailable"
                        }
                    );
                }

                break;
            default:
                break;
        }
    }

    public async Task<IReadOnlyList<JobInfo>> GetRecentAsync(Guid? caseId, int take, CancellationToken ct)
    {
        return await _jobQueryService.GetRecentJobsAsync(caseId, take, ct);
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
        var runningInfo = await ExecuteJobWriteWithRetryAsync(
            operationName: "Start",
            jobId: jobId,
            correlationId: null,
            async writeCt =>
            {
                await using var db = await _dbContextFactory.CreateDbContextAsync(writeCt);
                var jobRecord = await db.Jobs.FirstOrDefaultAsync(j => j.JobId == jobId, writeCt);
                if (jobRecord is null || jobRecord.Status != JobStatus.Queued.ToString())
                {
                    return null;
                }

                var now = _clock.UtcNow.ToUniversalTime();
                jobRecord.Status = JobStatus.Running.ToString();
                jobRecord.StartedAtUtc ??= now;
                jobRecord.StatusMessage = $"Running {jobRecord.JobType}...";
                await db.SaveChangesAsync(writeCt);
                return MapJobInfo(jobRecord);
            },
            stoppingToken
        );

        if (runningInfo is null)
        {
            return;
        }

        PublishJobUpdate(runningInfo);
        MarkProgressPersisted(jobId, runningInfo.Progress, runningInfo.StatusMessage);
        LogJobEvent(runningInfo, "JobStarted", "INFO", "Job execution started.");
        await WriteLifecycleAuditAsync(
            runningInfo,
            "JobStarted",
            $"{runningInfo.JobType} job started.",
            stoppingToken
        );

        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        if (!_runningJobs.TryAdd(jobId, linkedCts))
        {
            return;
        }

        if (_pendingRunningCancels.TryRemove(jobId, out _))
        {
            linkedCts.Cancel();
        }

        var terminalStatus = JobStatus.Failed;
        var terminalStatusMessage = "Failed: Terminal state repair after unexpected runner flow.";
        string? terminalErrorMessage = "Terminal state repair after unexpected runner flow.";
        JobRecord? jobToExecute = null;

        try
        {
            jobToExecute = await GetJobRecordAsync(jobId, linkedCts.Token);
            if (jobToExecute is null)
            {
                return;
            }

            var executionStartedAt = DateTimeOffset.UtcNow;
            switch (jobToExecute.JobType)
            {
                case EvidenceImportJobType:
                    await ExecuteEvidenceImportAsync(jobToExecute, linkedCts.Token);
                    terminalStatus = JobStatus.Succeeded;
                    terminalStatusMessage = "Succeeded: Evidence import completed.";
                    terminalErrorMessage = null;
                    break;
                case EvidenceVerifyJobType:
                    await ExecuteEvidenceVerifyAsync(jobToExecute, linkedCts.Token);
                    terminalStatus = JobStatus.Succeeded;
                    terminalStatusMessage = "Succeeded: Evidence verify completed.";
                    terminalErrorMessage = null;
                    break;
                case MessagesIngestJobType:
                {
                    var ingestResult = await ExecuteMessagesIngestAsync(jobToExecute, linkedCts.Token);
                    terminalStatus = JobStatus.Succeeded;
                    terminalStatusMessage = $"Succeeded: {ComposeMessagesIngestSuccessSummary(ingestResult)}";
                    terminalErrorMessage = null;
                    await WriteMessagesIngestSummaryAsync(jobToExecute, ingestResult.MessagesExtracted, linkedCts.Token);
                    break;
                }
                case TargetPresenceIndexRebuildJobType:
                    await ExecuteTargetPresenceIndexRebuildAsync(jobToExecute, linkedCts.Token);
                    terminalStatus = JobStatus.Succeeded;
                    terminalStatusMessage = "Succeeded: Target presence index rebuilt.";
                    terminalErrorMessage = null;
                    break;
                case TestLongRunningJobType:
                    await ExecuteTestLongRunningAsync(jobToExecute, linkedCts.Token);
                    terminalStatus = JobStatus.Succeeded;
                    terminalStatusMessage = "Succeeded: Test long-running job completed.";
                    terminalErrorMessage = null;
                    break;
                default:
                    throw new NotSupportedException($"Unsupported job type \"{jobToExecute.JobType}\".");
            }

            LogJobEvent(
                jobToExecute,
                eventName: "JobCompleted",
                level: "INFO",
                message: "Job execution completed.",
                fields: new Dictionary<string, object?>
                {
                    ["elapsedMs"] = (long)(DateTimeOffset.UtcNow - executionStartedAt).TotalMilliseconds
                }
            );
        }
        catch (OperationCanceledException) when (linkedCts.IsCancellationRequested)
        {
            terminalStatus = JobStatus.Canceled;
            terminalStatusMessage = "Canceled";
            terminalErrorMessage = null;
            if (jobToExecute is not null)
            {
                LogJobEvent(jobToExecute, "JobCanceled", "INFO", "Job execution canceled.");
            }
        }
        catch (Exception ex)
        {
            terminalStatus = JobStatus.Failed;
            terminalStatusMessage = $"Failed: {SummarizeExceptionMessage(ex)}";
            terminalErrorMessage = ex.ToString();
            if (jobToExecute is not null)
            {
                LogJobEvent(jobToExecute, "JobFailed", "ERROR", "Job execution failed.", ex);
            }
            else
            {
                AppFileLogger.LogException($"Job execution failed before job payload load. jobId={jobId:D}", ex);
            }
        }
        finally
        {
            linkedCts.Cancel();
            var terminalInfo = await FinalizeTerminalStateAsync(
                jobId,
                terminalStatus,
                terminalStatusMessage,
                terminalErrorMessage,
                CancellationToken.None
            );

            if (terminalInfo is not null)
            {
                var (actionType, summary) = terminalStatus switch
                {
                    JobStatus.Succeeded => ("JobSucceeded", $"{terminalInfo.JobType} job succeeded."),
                    JobStatus.Canceled => ("JobCanceled", $"{terminalInfo.JobType} job canceled."),
                    JobStatus.Failed => ("JobFailed", $"{terminalInfo.JobType} job failed."),
                    JobStatus.Abandoned => ("JobAbandoned", $"{terminalInfo.JobType} job abandoned."),
                    _ => ("JobFailed", $"{terminalInfo.JobType} job failed.")
                };

                await WriteLifecycleAuditAsync(
                    terminalInfo,
                    actionType,
                    summary,
                    CancellationToken.None
                );
            }

            _runningJobs.TryRemove(jobId, out _);
            _pendingRunningCancels.TryRemove(jobId, out _);
            _progressDropWarnings.TryRemove(jobId, out _);
            _persistedProgressStates.TryRemove(jobId, out _);
            _latestJobInfos.TryRemove(jobId, out _);
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
                SafeReportProgressAsync(
                    jobRecord.JobId,
                    (fileIndex + progress) / totalFiles,
                    $"Importing {fileIndex + 1}/{totalFiles}: {fileName}",
                    ct
                ).Forget("ReportEvidenceImportProgress", caseId: jobRecord.CaseId, evidenceId: jobRecord.EvidenceItemId);
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
            SafeReportProgressAsync(
                jobRecord.JobId,
                progress,
                $"Verifying {evidenceItem.OriginalFileName}...",
                ct
            ).Forget("ReportEvidenceVerifyProgress", caseId: jobRecord.CaseId, evidenceId: jobRecord.EvidenceItemId);
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

        LogJobEvent(
            jobRecord,
            eventName: "MessagesIngestStarted",
            level: "INFO",
            message: "Messages ingest started.",
            fields: new Dictionary<string, object?>
            {
                ["fileExtension"] = evidence.FileExtension
            }
        );

        var ingestStopwatch = System.Diagnostics.Stopwatch.StartNew();
        var lastLoggedPercent = -1;
        var lastLoggedAt = DateTimeOffset.MinValue;

        var detailedProgress = new Progress<MessageIngestProgress>(update =>
        {
            var clampedProgress = Math.Clamp(update.FractionComplete, 0, 1);
            var statusMessage = BuildIngestStatusMessage(update);
            SafeReportProgressAsync(
                jobRecord.JobId,
                clampedProgress,
                statusMessage,
                ct
            ).Forget("ReportMessagesIngestProgress", caseId: jobRecord.CaseId, evidenceId: jobRecord.EvidenceItemId);

            var percent = (int)Math.Round(clampedProgress * 100, MidpointRounding.AwayFromZero);
            var now = DateTimeOffset.UtcNow;
            if (percent >= lastLoggedPercent + 5 || (now - lastLoggedAt).TotalSeconds >= 2)
            {
                lastLoggedPercent = percent;
                lastLoggedAt = now;
                LogJobEvent(
                    jobRecord,
                    eventName: "MessagesIngestProgress",
                    level: "INFO",
                    message: statusMessage,
                    fields: new Dictionary<string, object?>
                    {
                        ["progressPercent"] = percent
                    }
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

        if (_targetMessagePresenceIndexService is not null)
        {
            await ReportProgressAsync(
                jobRecord.JobId,
                0.92,
                "Refreshing target presence index...",
                ct
            );

            await _targetMessagePresenceIndexService.RefreshForEvidenceAsync(
                payload.CaseId,
                payload.EvidenceItemId,
                ct
            );
        }

        await ReportProgressAsync(
            jobRecord.JobId,
            1,
            ComposeMessagesIngestSuccessSummary(ingestResult),
            ct
        );

        LogJobEvent(
            jobRecord,
            eventName: "MessagesIngestCompleted",
            level: "INFO",
            message: ComposeMessagesIngestSuccessSummary(ingestResult),
            fields: new Dictionary<string, object?>
            {
                ["messagesExtracted"] = ingestResult.MessagesExtracted,
                ["threadsCreated"] = ingestResult.ThreadsCreated,
                ["elapsedMs"] = ingestStopwatch.ElapsedMilliseconds
            }
        );

        return ingestResult;
    }

    private async Task ExecuteTargetPresenceIndexRebuildAsync(JobRecord jobRecord, CancellationToken ct)
    {
        if (_targetMessagePresenceIndexService is null)
        {
            throw new InvalidOperationException("Target message presence index service is not configured.");
        }

        var payload = JsonSerializer.Deserialize<TargetPresenceIndexRebuildPayload>(
            jobRecord.JsonPayload,
            PayloadSerializerOptions
        );

        if (payload is null || payload.SchemaVersion != 1 || payload.CaseId == Guid.Empty)
        {
            throw new InvalidOperationException("Invalid TargetPresenceIndexRebuild payload.");
        }

        await ReportProgressAsync(
            jobRecord.JobId,
            0.15,
            "Rebuilding target presence index...",
            ct
        );

        await _targetMessagePresenceIndexService.RebuildCaseAsync(payload.CaseId, ct);

        await ReportProgressAsync(
            jobRecord.JobId,
            1,
            "Target presence index rebuilt.",
            ct
        );
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
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Ignore transient callback updates once the job has been canceled.
        }
        catch (Exception ex)
        {
            WarnProgressDroppedOnce(jobId, progress, statusMessage, ex);
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
        PublishInMemoryProgressUpdate(jobId, clampedProgress, statusMessage);
        if (!ShouldPersistProgress(jobId, clampedProgress, statusMessage))
        {
            return;
        }

        var correlationId = _latestJobInfos.TryGetValue(jobId, out var latestInfo)
            ? latestInfo.CorrelationId
            : null;
        var progressInfo = await ExecuteJobWriteWithRetryAsync(
            operationName: "ReportProgress",
            jobId: jobId,
            correlationId: correlationId,
            async writeCt =>
            {
                await using var db = await _dbContextFactory.CreateDbContextAsync(writeCt);
                var jobRecord = await db.Jobs.FirstOrDefaultAsync(j => j.JobId == jobId, writeCt);
                if (jobRecord is null || jobRecord.Status != JobStatus.Running.ToString())
                {
                    return null;
                }

                if (jobRecord.CompletedAtUtc.HasValue || IsTerminalStatus(jobRecord.Status))
                {
                    return null;
                }

                if (clampedProgress + 0.0005 < jobRecord.Progress)
                {
                    return null;
                }

                var monotonicProgress = Math.Max(jobRecord.Progress, clampedProgress);
                if (Math.Abs(jobRecord.Progress - monotonicProgress) < 0.0005
                    && string.Equals(jobRecord.StatusMessage, statusMessage, StringComparison.Ordinal))
                {
                    return null;
                }

                if (Math.Abs(jobRecord.Progress - monotonicProgress) < 0.0005
                    && ShouldIgnoreSameProgressMessage(jobRecord.StatusMessage, statusMessage))
                {
                    return null;
                }

                jobRecord.Progress = monotonicProgress;
                jobRecord.StatusMessage = statusMessage;
                await db.SaveChangesAsync(writeCt);
                return MapJobInfo(jobRecord);
            },
            ct
        );

        if (progressInfo is null)
        {
            return;
        }

        MarkProgressPersisted(jobId, progressInfo.Progress, progressInfo.StatusMessage);
        PublishJobUpdate(progressInfo);
    }

    private async Task<JobInfo?> FinalizeTerminalStateAsync(
        Guid jobId,
        JobStatus status,
        string statusMessage,
        string? errorMessage,
        CancellationToken ct
    )
    {
        var correlationId = _latestJobInfos.TryGetValue(jobId, out var latestInfo)
            ? latestInfo.CorrelationId
            : null;
        var jobInfo = await ExecuteJobWriteWithRetryAsync(
            operationName: "FinalizeTerminalState",
            jobId: jobId,
            correlationId: correlationId,
            async writeCt =>
            {
                await using var db = await _dbContextFactory.CreateDbContextAsync(writeCt);
                var jobRecord = await db.Jobs.FirstOrDefaultAsync(j => j.JobId == jobId, writeCt);
                if (jobRecord is null)
                {
                    return null;
                }

                jobRecord.Status = status.ToString();
                jobRecord.CompletedAtUtc = _clock.UtcNow.ToUniversalTime();
                jobRecord.StatusMessage = statusMessage;
                jobRecord.ErrorMessage = errorMessage;
                jobRecord.Progress = 1;
                await db.SaveChangesAsync(writeCt);
                return MapJobInfo(jobRecord);
            },
            ct
        );

        if (jobInfo is null)
        {
            return null;
        }

        MarkProgressPersisted(jobId, jobInfo.Progress, jobInfo.StatusMessage);
        PublishJobUpdate(jobInfo);
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
            or TargetPresenceIndexRebuildJobType
            or TestLongRunningJobType;
    }

    private static bool IsTerminalStatus(string status)
    {
        return status is nameof(JobStatus.Succeeded)
            or nameof(JobStatus.Failed)
            or nameof(JobStatus.Canceled)
            or nameof(JobStatus.Abandoned);
    }

    private static bool ShouldDeduplicateEvidenceVerify(JobEnqueueRequest request)
    {
        return string.Equals(request.JobType, EvidenceVerifyJobType, StringComparison.Ordinal)
            && request.CaseId.HasValue
            && request.EvidenceItemId.HasValue;
    }

    private static bool ShouldIgnoreSameProgressMessage(string currentMessage, string incomingMessage)
    {
        if (string.Equals(currentMessage, incomingMessage, StringComparison.Ordinal))
        {
            return false;
        }

        var currentTerminalLike = IsTerminalLikeProgressMessage(currentMessage);
        var incomingTerminalLike = IsTerminalLikeProgressMessage(incomingMessage);
        return currentTerminalLike && !incomingTerminalLike;
    }

    private static bool IsTerminalLikeProgressMessage(string message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return false;
        }

        return message.StartsWith("Extracted ", StringComparison.Ordinal)
            || message.StartsWith("No message sheets found;", StringComparison.Ordinal)
            || message.StartsWith("UFDR ", StringComparison.Ordinal)
            || message.StartsWith("Succeeded:", StringComparison.Ordinal)
            || message.StartsWith("Failed:", StringComparison.Ordinal)
            || string.Equals(message, "Canceled", StringComparison.Ordinal);
    }

    private static bool IsDebugBuild()
    {
#if DEBUG
        return true;
#else
        return false;
#endif
    }

    private static async Task<JobRecord?> FindActiveEvidenceVerifyDuplicateAsync(
        WorkspaceDbContext db,
        Guid caseId,
        Guid evidenceItemId,
        CancellationToken ct
    )
    {
        var activeStatuses = new[]
        {
            JobStatus.Queued.ToString(),
            JobStatus.Running.ToString()
        };

        return await db.Jobs
            .AsNoTracking()
            .Where(job => job.JobType == EvidenceVerifyJobType)
            .Where(job => job.CaseId == caseId && job.EvidenceItemId == evidenceItemId)
            .Where(job => activeStatuses.Contains(job.Status))
            .FirstOrDefaultAsync(ct);
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

    private static string SummarizeExceptionMessage(Exception ex)
    {
        var message = ex.GetBaseException().Message;
        if (string.IsNullOrWhiteSpace(message))
        {
            return "Unhandled error.";
        }

        return message.Trim();
    }

    private async Task<T> ExecuteJobWriteWithRetryAsync<T>(
        string operationName,
        Guid? jobId,
        string? correlationId,
        Func<CancellationToken, Task<T>> operation,
        CancellationToken ct
    )
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(operationName);
        ArgumentNullException.ThrowIfNull(operation);

        IReadOnlyDictionary<string, object?>? fields = null;
        if (jobId.HasValue)
        {
            fields = new Dictionary<string, object?>
            {
                ["jobId"] = jobId.Value.ToString("D")
            };
        }

        return await _workspaceWriteGate.ExecuteWriteWithResultAsync(
            operationName: operationName,
            async writeCt =>
            {
                await _databaseInitializer.EnsureInitializedAsync(writeCt);
                return await operation(writeCt);
            },
            ct,
            correlationId: correlationId,
            fields: fields
        );
    }

    private void PublishJobUpdate(JobInfo jobInfo)
    {
        _latestJobInfos[jobInfo.JobId] = jobInfo;
        _jobUpdates.Publish(jobInfo);
    }

    private void PublishInMemoryProgressUpdate(
        Guid jobId,
        double clampedProgress,
        string statusMessage
    )
    {
        if (!_latestJobInfos.TryGetValue(jobId, out var current))
        {
            return;
        }

        if (current.Status != JobStatus.Running)
        {
            return;
        }

        if (clampedProgress + 0.0005 < current.Progress)
        {
            return;
        }

        var monotonicProgress = Math.Max(current.Progress, clampedProgress);
        if (Math.Abs(current.Progress - monotonicProgress) < 0.0005
            && string.Equals(current.StatusMessage, statusMessage, StringComparison.Ordinal))
        {
            return;
        }

        if (Math.Abs(current.Progress - monotonicProgress) < 0.0005
            && ShouldIgnoreSameProgressMessage(current.StatusMessage, statusMessage))
        {
            return;
        }

        PublishJobUpdate(current with
        {
            Progress = monotonicProgress,
            StatusMessage = statusMessage
        });
    }

    private bool ShouldPersistProgress(Guid jobId, double clampedProgress, string statusMessage)
    {
        if (clampedProgress >= 0.9995 || IsTerminalLikeProgressMessage(statusMessage))
        {
            return true;
        }

        if (!_persistedProgressStates.TryGetValue(jobId, out var persistedState))
        {
            return true;
        }

        if (clampedProgress + 0.0005 < persistedState.Progress)
        {
            return false;
        }

        if (clampedProgress - persistedState.Progress >= ProgressPersistMinimumDelta)
        {
            return true;
        }

        var elapsed = DateTimeOffset.UtcNow - persistedState.TimestampUtc;
        if (elapsed >= ProgressPersistMinimumInterval)
        {
            return true;
        }

        return !string.Equals(
            persistedState.StatusMessage,
            statusMessage,
            StringComparison.Ordinal
        ) && elapsed >= TimeSpan.FromMilliseconds(150);
    }

    private void MarkProgressPersisted(Guid jobId, double progress, string statusMessage)
    {
        _persistedProgressStates[jobId] = new PersistedProgressState(
            DateTimeOffset.UtcNow,
            Math.Clamp(progress, 0, 1),
            statusMessage
        );
    }

    private void WarnProgressDroppedOnce(
        Guid jobId,
        double progress,
        string statusMessage,
        Exception ex
    )
    {
        if (!_progressDropWarnings.TryAdd(jobId, 0))
        {
            return;
        }

        var clampedProgress = Math.Clamp(progress, 0, 1);
        var isLockContention = SqliteWriteRetryPolicy.IsBusyOrLocked(ex);
        AppFileLogger.LogEvent(
            eventName: "JobProgressUpdateDropped",
            level: "WARN",
            message: isLockContention
                ? "Job progress update dropped after SQLite lock retries were exhausted."
                : "Job progress update dropped due to non-disruptive callback failure.",
            ex: ex,
            fields: new Dictionary<string, object?>
            {
                ["jobId"] = jobId.ToString("D"),
                ["progress"] = clampedProgress,
                ["statusMessage"] = statusMessage
            }
        );
    }

    private static void LogJobEvent(
        JobRecord jobRecord,
        string eventName,
        string level,
        string message,
        Exception? ex = null,
        IReadOnlyDictionary<string, object?>? fields = null
    )
    {
        using var jobScope = AppFileLogger.BeginJobScope(
            jobRecord.JobId,
            jobRecord.JobType,
            jobRecord.CorrelationId,
            jobRecord.CaseId,
            jobRecord.EvidenceItemId
        );
        AppFileLogger.LogEvent(eventName, level, message, ex: ex, fields: fields);
    }

    private static void LogJobEvent(
        JobInfo jobInfo,
        string eventName,
        string level,
        string message,
        Exception? ex = null,
        IReadOnlyDictionary<string, object?>? fields = null
    )
    {
        using var jobScope = AppFileLogger.BeginJobScope(
            jobInfo.JobId,
            jobInfo.JobType,
            jobInfo.CorrelationId,
            jobInfo.CaseId,
            jobInfo.EvidenceItemId
        );
        AppFileLogger.LogEvent(eventName, level, message, ex: ex, fields: fields);
    }

    private sealed record EnqueueWriteOutcome(JobRecord? QueuedJob, JobRecord? DeduplicatedJob)
    {
        public static EnqueueWriteOutcome Queued(JobRecord queuedJob)
        {
            return new EnqueueWriteOutcome(queuedJob, DeduplicatedJob: null);
        }

        public static EnqueueWriteOutcome Deduplicated(JobRecord deduplicatedJob)
        {
            return new EnqueueWriteOutcome(
                QueuedJob: null,
                DeduplicatedJob: deduplicatedJob
            );
        }
    }

    private enum CancelAction
    {
        None = 0,
        MarkedCanceled = 1,
        AlreadyTerminal = 2,
        IgnoredNotRunning = 3,
        MarkedCancelRequested = 4
    }

    private sealed record CancelWriteOutcome(bool Found, CancelAction Action, JobInfo? JobInfo)
    {
        public static CancelWriteOutcome NotFound()
        {
            return new CancelWriteOutcome(false, CancelAction.None, JobInfo: null);
        }

        public static CancelWriteOutcome Completed(CancelAction action, JobInfo jobInfo)
        {
            return new CancelWriteOutcome(true, action, jobInfo);
        }
    }

    private sealed record PersistedProgressState(
        DateTimeOffset TimestampUtc,
        double Progress,
        string StatusMessage
    );

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

    private sealed class TargetPresenceIndexRebuildPayload
    {
        public int SchemaVersion { get; set; }

        public Guid CaseId { get; set; }
    }

    private sealed class TestLongRunningPayload
    {
        public int SchemaVersion { get; set; }

        public int DelayMilliseconds { get; set; }
    }
}
