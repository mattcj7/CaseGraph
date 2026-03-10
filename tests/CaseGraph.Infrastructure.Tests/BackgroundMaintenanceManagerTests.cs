using CaseGraph.App.Services;

namespace CaseGraph.Infrastructure.Tests;

public sealed class BackgroundMaintenanceManagerTests
{
    [Fact]
    public async Task QueueOrJoin_TracksPendingRunningAndCompletedLifecycle()
    {
        var manager = new BackgroundMaintenanceManager();
        var taskKey = MaintenanceTaskKeys.MessageSearchIndex(Guid.NewGuid());
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var allowCompletion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var snapshots = new List<MaintenanceTaskSnapshot>();

        manager.SnapshotChanged += (_, snapshot) => snapshots.Add(snapshot);

        var result = manager.QueueOrJoin(
            new MaintenanceTaskRequest(
                taskKey,
                "Message search index maintenance",
                MaintenanceTaskCategory.MessageSearchIndex
            ),
            async (progress, ct) =>
            {
                progress.Report(
                    new MaintenanceProgressUpdate(
                        "Running.",
                        "Reconciling the message index."
                    )
                );
                started.SetResult();
                await allowCompletion.Task.WaitAsync(ct);
                progress.Report(
                    new MaintenanceProgressUpdate(
                        "Completed.",
                        "Message index maintenance completed."
                    )
                );
            }
        );

        Assert.True(result.WasQueued);
        Assert.False(result.WasDeduplicated);
        Assert.Equal(MaintenanceTaskState.Pending, result.Snapshot.State);

        await started.Task;
        Assert.Equal(MaintenanceTaskState.Running, manager.GetSnapshot(taskKey)?.State);

        allowCompletion.SetResult();
        await result.ExecutionTask;

        var completed = manager.GetSnapshot(taskKey);
        Assert.NotNull(completed);
        Assert.Equal(MaintenanceTaskState.Completed, completed!.State);
        Assert.Contains(snapshots, snapshot => snapshot.State == MaintenanceTaskState.Pending);
        Assert.Contains(snapshots, snapshot => snapshot.State == MaintenanceTaskState.Running);
        Assert.Contains(snapshots, snapshot => snapshot.State == MaintenanceTaskState.Completed);
    }

    [Fact]
    public async Task QueueOrJoin_DeduplicatesOverlappingRequests()
    {
        var manager = new BackgroundMaintenanceManager();
        var taskKey = MaintenanceTaskKeys.MessageSearchIndex(Guid.NewGuid());
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var allowCompletion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var invocationCount = 0;

        var first = manager.QueueOrJoin(
            new MaintenanceTaskRequest(
                taskKey,
                "Message search index maintenance",
                MaintenanceTaskCategory.MessageSearchIndex
            ),
            async (_, ct) =>
            {
                Interlocked.Increment(ref invocationCount);
                started.SetResult();
                await allowCompletion.Task.WaitAsync(ct);
            }
        );

        await started.Task;

        var second = manager.QueueOrJoin(
            new MaintenanceTaskRequest(
                taskKey,
                "Message search index maintenance",
                MaintenanceTaskCategory.MessageSearchIndex
            ),
            (_, _) => Task.CompletedTask
        );

        Assert.False(second.WasQueued);
        Assert.True(second.WasDeduplicated);
        Assert.Same(first.ExecutionTask, second.ExecutionTask);
        Assert.Equal(1, invocationCount);

        allowCompletion.SetResult();
        await Task.WhenAll(first.ExecutionTask, second.ExecutionTask);
    }

    [Fact]
    public async Task QueueOrJoin_FailedTaskTransitionsToFailedState()
    {
        var manager = new BackgroundMaintenanceManager();
        var taskKey = MaintenanceTaskKeys.MessageSearchIndex(Guid.NewGuid());

        var result = manager.QueueOrJoin(
            new MaintenanceTaskRequest(
                taskKey,
                "Message search index maintenance",
                MaintenanceTaskCategory.MessageSearchIndex
            ),
            (_, _) => throw new InvalidOperationException("boom")
        );

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => result.ExecutionTask);
        Assert.Equal("boom", ex.Message);

        var failed = manager.GetSnapshot(taskKey);
        Assert.NotNull(failed);
        Assert.Equal(MaintenanceTaskState.Failed, failed!.State);
        Assert.Equal("boom", failed.ErrorMessage);
    }

    [Theory]
    [InlineData(MaintenanceTaskState.Pending, false, ReadinessBannerTone.Info, "Timeline maintenance queued")]
    [InlineData(MaintenanceTaskState.Running, true, ReadinessBannerTone.Info, "Timeline maintenance in progress")]
    [InlineData(MaintenanceTaskState.Completed, false, ReadinessBannerTone.Success, "Timeline ready")]
    [InlineData(MaintenanceTaskState.Failed, false, ReadinessBannerTone.Error, "Timeline maintenance failed")]
    public void ReadinessBannerStateFactory_MapsMaintenanceStates(
        MaintenanceTaskState state,
        bool blocksCurrentAction,
        ReadinessBannerTone expectedTone,
        string expectedTitle
    )
    {
        var banner = ReadinessBannerStateFactory.FromMaintenance(
            ReadinessFeature.Timeline,
            new MaintenanceTaskSnapshot(
                "task",
                "Task",
                MaintenanceTaskCategory.MessageSearchIndex,
                state,
                Guid.NewGuid(),
                ReadinessFeature.Timeline,
                StatusText: "Status",
                DetailText: "Detail",
                ErrorMessage: "Error"
            ),
            blocksCurrentAction
        );

        Assert.True(banner.IsVisible);
        Assert.Equal(expectedTone, banner.Tone);
        Assert.Equal(expectedTitle, banner.Title);
    }
}
