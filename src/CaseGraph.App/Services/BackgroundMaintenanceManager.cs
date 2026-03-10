using CaseGraph.Core.Diagnostics;
using CaseGraph.Infrastructure.Diagnostics;

namespace CaseGraph.App.Services;

public sealed class BackgroundMaintenanceManager : IBackgroundMaintenanceManager
{
    private readonly object _gate = new();
    private readonly Dictionary<string, MaintenanceEntry> _entries = new(StringComparer.Ordinal);
    private readonly TimeProvider _timeProvider;

    public BackgroundMaintenanceManager(TimeProvider? timeProvider = null)
    {
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public event EventHandler<MaintenanceTaskSnapshot>? SnapshotChanged;

    public MaintenanceTaskSnapshot? GetSnapshot(string taskKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(taskKey);

        lock (_gate)
        {
            return _entries.TryGetValue(taskKey, out var entry)
                ? entry.Snapshot
                : null;
        }
    }

    public MaintenanceRequestResult QueueOrJoin(
        MaintenanceTaskRequest request,
        Func<IProgress<MaintenanceProgressUpdate>, CancellationToken, Task> work
    )
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(work);

        MaintenanceTaskSnapshot snapshot;
        Task executionTask;
        string correlationId;
        var wasQueued = false;
        var wasDeduplicated = false;

        lock (_gate)
        {
            if (_entries.TryGetValue(request.TaskKey, out var existing)
                && existing.Snapshot.State is MaintenanceTaskState.Pending or MaintenanceTaskState.Running)
            {
                existing.RequestCount++;
                existing.Snapshot = existing.Snapshot with
                {
                    RequestCount = existing.RequestCount,
                    LastUpdatedAtUtc = _timeProvider.GetUtcNow()
                };
                snapshot = existing.Snapshot;
                executionTask = existing.ExecutionTask;
                correlationId = existing.CorrelationId;
                wasDeduplicated = true;
            }
            else
            {
                var created = new MaintenanceEntry(request, AppFileLogger.NewCorrelationId())
                {
                    Cancellation = request.SupportsCancellation ? new CancellationTokenSource() : null,
                    Completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously),
                    RequestCount = 1
                };
                created.ExecutionTask = created.Completion.Task;
                created.Snapshot = CreateSnapshot(
                    request,
                    MaintenanceTaskState.Pending,
                    request.PendingStatusText ?? $"{request.DisplayName} queued.",
                    detailText: request.PendingStatusText,
                    requestCount: created.RequestCount
                );

                _entries[request.TaskKey] = created;
                snapshot = created.Snapshot;
                executionTask = created.ExecutionTask;
                correlationId = created.CorrelationId;
                wasQueued = true;
            }
        }

        if (wasDeduplicated)
        {
            PublishSnapshot(snapshot);
            LogLifecycleEvent(
                "MaintenanceRequestDeduplicated",
                "Maintenance request attached to an existing active task.",
                snapshot,
                correlationId
            );
            return new MaintenanceRequestResult(false, true, executionTask, snapshot);
        }

        PublishSnapshot(snapshot);
        LogLifecycleEvent(
            "MaintenanceRequestQueued",
            "Maintenance request queued.",
            snapshot,
            correlationId
        );

        _ = Task.Run(async () =>
        {
            MaintenanceEntry entry;
            lock (_gate)
            {
                entry = _entries[request.TaskKey];
            }

            try
            {
                await RunEntryAsync(entry, work).ConfigureAwait(false);
                entry.Completion.TrySetResult();
            }
            catch (Exception ex)
            {
                entry.Completion.TrySetException(ex);
            }
        });

        return new MaintenanceRequestResult(wasQueued, false, executionTask, snapshot);
    }

    public bool TryCancel(string taskKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(taskKey);

        lock (_gate)
        {
            if (!_entries.TryGetValue(taskKey, out var entry)
                || entry.Cancellation is null
                || entry.Snapshot.State is not (MaintenanceTaskState.Pending or MaintenanceTaskState.Running))
            {
                return false;
            }

            entry.Cancellation.Cancel();
            return true;
        }
    }

    private async Task RunEntryAsync(
        MaintenanceEntry entry,
        Func<IProgress<MaintenanceProgressUpdate>, CancellationToken, Task> work
    )
    {
        var runningSnapshot = UpdateSnapshot(
            entry.Request.TaskKey,
            snapshot => snapshot with
            {
                State = MaintenanceTaskState.Running,
                StatusText = entry.Request.RunningStatusText ?? $"{entry.Request.DisplayName} running.",
                StartedAtUtc = snapshot.StartedAtUtc ?? _timeProvider.GetUtcNow(),
                LastUpdatedAtUtc = _timeProvider.GetUtcNow()
            }
        );
        PublishSnapshot(runningSnapshot);
        LogLifecycleEvent(
            "MaintenanceStarted",
            "Maintenance task started.",
            runningSnapshot,
            entry.CorrelationId
        );

        var progress = new Progress<MaintenanceProgressUpdate>(update =>
        {
            var updatedSnapshot = UpdateSnapshot(
                entry.Request.TaskKey,
                snapshot => snapshot with
                {
                    StatusText = update.StatusText,
                    DetailText = update.DetailText,
                    LastUpdatedAtUtc = _timeProvider.GetUtcNow()
                }
            );
            PublishSnapshot(updatedSnapshot);
        });

        try
        {
            await work(progress, entry.Cancellation?.Token ?? CancellationToken.None).ConfigureAwait(false);

            var completedSnapshot = UpdateSnapshot(
                entry.Request.TaskKey,
                snapshot => snapshot with
                {
                    State = MaintenanceTaskState.Completed,
                    StatusText = snapshot.StatusText ?? $"{entry.Request.DisplayName} completed.",
                    DetailText = snapshot.DetailText ?? "Background maintenance completed successfully.",
                    ErrorMessage = null,
                    CompletedAtUtc = _timeProvider.GetUtcNow(),
                    LastUpdatedAtUtc = _timeProvider.GetUtcNow()
                }
            );
            PublishSnapshot(completedSnapshot);
            LogLifecycleEvent(
                "MaintenanceCompleted",
                "Maintenance task completed.",
                completedSnapshot,
                entry.CorrelationId
            );
        }
        catch (Exception ex)
        {
            var failedSnapshot = UpdateSnapshot(
                entry.Request.TaskKey,
                snapshot => snapshot with
                {
                    State = MaintenanceTaskState.Failed,
                    ErrorMessage = ex.Message,
                    DetailText = snapshot.DetailText ?? "Background maintenance failed.",
                    CompletedAtUtc = _timeProvider.GetUtcNow(),
                    LastUpdatedAtUtc = _timeProvider.GetUtcNow()
                }
            );
            PublishSnapshot(failedSnapshot);
            AppFileLogger.LogEvent(
                eventName: "MaintenanceFailed",
                level: "ERROR",
                message: "Maintenance task failed.",
                ex: ex,
                fields: BuildFields(failedSnapshot, entry.CorrelationId)
            );
            throw;
        }
        finally
        {
            entry.Cancellation?.Dispose();
            entry.Cancellation = null;
        }
    }

    private MaintenanceTaskSnapshot UpdateSnapshot(
        string taskKey,
        Func<MaintenanceTaskSnapshot, MaintenanceTaskSnapshot> apply
    )
    {
        lock (_gate)
        {
            var entry = _entries[taskKey];
            entry.Snapshot = apply(entry.Snapshot);
            return entry.Snapshot;
        }
    }

    private MaintenanceTaskSnapshot CreateSnapshot(
        MaintenanceTaskRequest request,
        MaintenanceTaskState state,
        string? statusText,
        string? detailText,
        int requestCount
    )
    {
        var now = _timeProvider.GetUtcNow();
        return new MaintenanceTaskSnapshot(
            request.TaskKey,
            request.DisplayName,
            request.Category,
            state,
            request.CaseId,
            request.Feature,
            StatusText: statusText,
            DetailText: detailText,
            RequestedAtUtc: now,
            LastUpdatedAtUtc: now,
            RequestCount: requestCount
        );
    }

    private void PublishSnapshot(MaintenanceTaskSnapshot snapshot)
    {
        SnapshotChanged?.Invoke(this, snapshot);
    }

    private static void LogLifecycleEvent(
        string eventName,
        string message,
        MaintenanceTaskSnapshot snapshot,
        string correlationId
    )
    {
        AppFileLogger.LogEvent(
            eventName: eventName,
            level: "INFO",
            message: message,
            fields: BuildFields(snapshot, correlationId)
        );
    }

    private static Dictionary<string, object?> BuildFields(
        MaintenanceTaskSnapshot snapshot,
        string correlationId
    )
    {
        var fields = new Dictionary<string, object?>
        {
            ["taskKey"] = snapshot.TaskKey,
            ["taskName"] = snapshot.DisplayName,
            ["taskCategory"] = snapshot.Category.ToString(),
            ["taskState"] = snapshot.State.ToString(),
            ["correlationId"] = correlationId,
            ["requestCount"] = snapshot.RequestCount
        };

        if (snapshot.CaseId.HasValue)
        {
            fields["caseId"] = snapshot.CaseId.Value.ToString("D");
        }

        if (snapshot.Feature.HasValue)
        {
            fields["feature"] = snapshot.Feature.Value.ToString();
        }

        if (!string.IsNullOrWhiteSpace(snapshot.StatusText))
        {
            fields["statusText"] = snapshot.StatusText;
        }

        if (!string.IsNullOrWhiteSpace(snapshot.ErrorMessage))
        {
            fields["errorMessage"] = snapshot.ErrorMessage;
        }

        return fields;
    }

    private sealed class MaintenanceEntry
    {
        public MaintenanceEntry(MaintenanceTaskRequest request, string correlationId)
        {
            Request = request;
            CorrelationId = correlationId;
        }

        public MaintenanceTaskRequest Request { get; }

        public string CorrelationId { get; }

        public MaintenanceTaskSnapshot Snapshot { get; set; } = default!;

        public Task ExecutionTask { get; set; } = Task.CompletedTask;

        public TaskCompletionSource Completion { get; set; } = default!;

        public CancellationTokenSource? Cancellation { get; set; }

        public int RequestCount { get; set; }
    }
}
