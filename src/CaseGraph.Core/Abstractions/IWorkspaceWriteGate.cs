namespace CaseGraph.Core.Abstractions;

public interface IWorkspaceWriteGate
{
    Task RunAsync(Func<CancellationToken, Task> operation, CancellationToken ct);

    Task<T> RunAsync<T>(Func<CancellationToken, Task<T>> operation, CancellationToken ct);
}
