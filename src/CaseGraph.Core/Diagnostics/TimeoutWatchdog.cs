namespace CaseGraph.Core.Diagnostics;

public static class TimeoutWatchdog
{
    public static async Task RunAsync(
        Func<CancellationToken, Task> operation,
        TimeSpan timeout,
        string timeoutMessage,
        CancellationToken ct,
        Action<Task>? onTimeout = null
    )
    {
        ArgumentNullException.ThrowIfNull(operation);
        ArgumentException.ThrowIfNullOrWhiteSpace(timeoutMessage);
        if (timeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(timeout), "Timeout must be positive.");
        }

        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var operationTask = operation(linkedCts.Token);
        var timeoutTask = Task.Delay(timeout, CancellationToken.None);
        var completed = await Task.WhenAny(operationTask, timeoutTask).ConfigureAwait(false);

        if (completed == operationTask)
        {
            await operationTask.ConfigureAwait(false);
            return;
        }

        linkedCts.Cancel();
        onTimeout?.Invoke(operationTask);
        throw new TimeoutException(timeoutMessage);
    }
}
