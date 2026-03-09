namespace CaseGraph.Infrastructure.Diagnostics;

public interface IPerformanceInstrumentation
{
    PerformanceScope BeginScope(PerformanceOperationContext context);

    Task TrackAsync(
        PerformanceOperationContext context,
        Func<CancellationToken, Task> operation,
        CancellationToken ct
    );

    Task<T> TrackAsync<T>(
        PerformanceOperationContext context,
        Func<CancellationToken, Task<T>> operation,
        CancellationToken ct
    );
}
