namespace CaseGraph.Infrastructure.Diagnostics;

public sealed class PerformanceInstrumentation : IPerformanceInstrumentation
{
    private readonly PerformanceBudgetOptions _budgetOptions;
    private readonly TimeProvider _timeProvider;

    public PerformanceInstrumentation(
        PerformanceBudgetOptions budgetOptions,
        TimeProvider? timeProvider = null
    )
    {
        _budgetOptions = budgetOptions;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public PerformanceScope BeginScope(PerformanceOperationContext context)
    {
        var budget = _budgetOptions.Resolve(
            context.OperationKind,
            context.OperationName,
            context.FeatureName
        );
        return new PerformanceScope(_timeProvider, context, budget);
    }

    public async Task TrackAsync(
        PerformanceOperationContext context,
        Func<CancellationToken, Task> operation,
        CancellationToken ct
    )
    {
        using var scope = BeginScope(context);
        try
        {
            await operation(ct);
            scope.Complete(PerformanceOutcomes.Succeeded);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            scope.Complete(PerformanceOutcomes.Canceled);
            throw;
        }
        catch
        {
            scope.Complete(PerformanceOutcomes.Faulted);
            throw;
        }
    }

    public async Task<T> TrackAsync<T>(
        PerformanceOperationContext context,
        Func<CancellationToken, Task<T>> operation,
        CancellationToken ct
    )
    {
        using var scope = BeginScope(context);
        try
        {
            var result = await operation(ct);
            scope.Complete(PerformanceOutcomes.Succeeded);
            return result;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            scope.Complete(PerformanceOutcomes.Canceled);
            throw;
        }
        catch
        {
            scope.Complete(PerformanceOutcomes.Faulted);
            throw;
        }
    }
}
