using CaseGraph.Core.Abstractions;
using System.Threading;

namespace CaseGraph.Infrastructure.Services;

public sealed class WorkspaceWriteGate : IWorkspaceWriteGate
{
    private static readonly AsyncLocal<int> ReentrancyDepth = new();
    private readonly SemaphoreSlim _semaphore = new(1, 1);
    private readonly IWorkspacePathProvider? _workspacePathProvider;

    public WorkspaceWriteGate()
    {
    }

    public WorkspaceWriteGate(IWorkspacePathProvider workspacePathProvider)
    {
        _workspacePathProvider = workspacePathProvider;
    }

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

    public Task ExecuteWriteAsync(
        string operationName,
        Func<CancellationToken, Task> operation,
        CancellationToken ct,
        string? correlationId = null,
        IReadOnlyDictionary<string, object?>? fields = null
    )
    {
        ArgumentNullException.ThrowIfNull(operation);
        return ExecuteWriteWithResultAsync<object?>(
            operationName,
            async writeCt =>
            {
                await operation(writeCt);
                return null;
            },
            ct,
            correlationId,
            fields
        );
    }

    public Task<T> ExecuteWriteWithResultAsync<T>(
        string operationName,
        Func<CancellationToken, Task<T>> operation,
        CancellationToken ct,
        string? correlationId = null,
        IReadOnlyDictionary<string, object?>? fields = null
    )
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(operationName);
        ArgumentNullException.ThrowIfNull(operation);
        return ExecuteWithRetryAsync(operationName, operation, ct, correlationId, fields);
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

    private async Task<T> ExecuteWithRetryAsync<T>(
        string operationName,
        Func<CancellationToken, Task<T>> operation,
        CancellationToken ct,
        string? correlationId,
        IReadOnlyDictionary<string, object?>? fields
    )
    {
        if (ReentrancyDepth.Value > 0)
        {
            return await SqliteBusyRetry.ExecuteAsync(
                operationName,
                ResolveWorkspaceDbPath(),
                operation,
                ct,
                correlationId,
                fields
            );
        }

        await _semaphore.WaitAsync(ct);
        ReentrancyDepth.Value++;
        try
        {
            return await SqliteBusyRetry.ExecuteAsync(
                operationName,
                ResolveWorkspaceDbPath(),
                operation,
                ct,
                correlationId,
                fields
            );
        }
        finally
        {
            ReentrancyDepth.Value--;
            _semaphore.Release();
        }
    }

    private string ResolveWorkspaceDbPath()
    {
        return string.IsNullOrWhiteSpace(_workspacePathProvider?.WorkspaceDbPath)
            ? "(unknown)"
            : _workspacePathProvider.WorkspaceDbPath;
    }
}
