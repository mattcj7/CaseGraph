namespace CaseGraph.Core.Abstractions;

public interface IWorkspaceWriteGate
{
    Task RunAsync(Func<CancellationToken, Task> operation, CancellationToken ct);

    Task<T> RunAsync<T>(Func<CancellationToken, Task<T>> operation, CancellationToken ct);

    Task ExecuteWriteAsync(
        string operationName,
        Func<CancellationToken, Task> operation,
        CancellationToken ct,
        string? correlationId = null,
        IReadOnlyDictionary<string, object?>? fields = null
    );

    Task<T> ExecuteWriteWithResultAsync<T>(
        string operationName,
        Func<CancellationToken, Task<T>> operation,
        CancellationToken ct,
        string? correlationId = null,
        IReadOnlyDictionary<string, object?>? fields = null
    );
}
