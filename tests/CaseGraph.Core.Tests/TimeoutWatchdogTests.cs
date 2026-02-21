using CaseGraph.Core.Diagnostics;

namespace CaseGraph.Core.Tests;

public sealed class TimeoutWatchdogTests
{
    [Fact]
    public async Task RunAsync_Completes_WhenOperationFinishesBeforeTimeout()
    {
        var exception = await Record.ExceptionAsync(() =>
            TimeoutWatchdog.RunAsync(
                operation: _ => Task.CompletedTask,
                timeout: TimeSpan.FromMilliseconds(250),
                timeoutMessage: "timeout",
                ct: CancellationToken.None
            )
        );

        Assert.Null(exception);
    }

    [Fact]
    public async Task RunAsync_TimesOut_AndInvokesOnTimeoutCallback()
    {
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task? timedOutTask = null;

        var exception = await Assert.ThrowsAsync<TimeoutException>(() =>
            TimeoutWatchdog.RunAsync(
                operation: _ => tcs.Task,
                timeout: TimeSpan.FromMilliseconds(50),
                timeoutMessage: "timed-out",
                ct: CancellationToken.None,
                onTimeout: task => timedOutTask = task
            )
        );

        Assert.Equal("timed-out", exception.Message);
        Assert.Same(tcs.Task, timedOutTask);
        tcs.SetResult();
        await tcs.Task;
    }
}
