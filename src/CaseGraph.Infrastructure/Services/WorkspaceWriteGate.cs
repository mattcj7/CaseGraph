using CaseGraph.Core.Abstractions;

namespace CaseGraph.Infrastructure.Services;

public sealed class WorkspaceWriteGate : IWorkspaceWriteGate
{
    private readonly SemaphoreSlim _semaphore = new(1, 1);

    public Task RunAsync(Func<CancellationToken, Task> operation, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(operation);
        return RunInternalAsync(operation, ct);
    }

    public Task<T> RunAsync<T>(Func<CancellationToken, Task<T>> operation, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(operation);
        return RunInternalAsync(operation, ct);
    }

    private async Task RunInternalAsync(
        Func<CancellationToken, Task> operation,
        CancellationToken ct
    )
    {
        await _semaphore.WaitAsync(ct);
        try
        {
            await operation(ct);
        }
        finally
        {
            _semaphore.Release();
        }
    }

    private async Task<T> RunInternalAsync<T>(
        Func<CancellationToken, Task<T>> operation,
        CancellationToken ct
    )
    {
        await _semaphore.WaitAsync(ct);
        try
        {
            return await operation(ct);
        }
        finally
        {
            _semaphore.Release();
        }
    }
}
