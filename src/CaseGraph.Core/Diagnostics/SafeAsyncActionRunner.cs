using System.Diagnostics;

namespace CaseGraph.Core.Diagnostics;

public sealed class SafeAsyncActionRunner
{
    public async Task<SafeAsyncActionResult> ExecuteAsync(
        string actionName,
        Func<CancellationToken, Task> executeAsync,
        CancellationToken ct,
        Guid? caseId = null,
        Guid? evidenceId = null,
        string? correlationId = null
    )
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(actionName);
        ArgumentNullException.ThrowIfNull(executeAsync);

        var resolvedCorrelationId = string.IsNullOrWhiteSpace(correlationId)
            ? AppFileLogger.NewCorrelationId()
            : correlationId.Trim();
        using var actionScope = AppFileLogger.BeginActionScope(
            actionName,
            resolvedCorrelationId,
            caseId,
            evidenceId
        );

        var stopwatch = Stopwatch.StartNew();
        AppFileLogger.LogEvent(
            eventName: "UiActionStarted",
            level: "INFO",
            message: $"{actionName} started."
        );

        try
        {
            await executeAsync(ct);
            stopwatch.Stop();

            AppFileLogger.LogEvent(
                eventName: "UiActionSucceeded",
                level: "INFO",
                message: $"{actionName} succeeded.",
                fields: new Dictionary<string, object?>
                {
                    ["durationMs"] = stopwatch.ElapsedMilliseconds
                }
            );

            return SafeAsyncActionResult.FromSucceeded(resolvedCorrelationId, stopwatch.ElapsedMilliseconds);
        }
        catch (OperationCanceledException ex)
        {
            if (ct.IsCancellationRequested || ex.CancellationToken.IsCancellationRequested)
            {
                stopwatch.Stop();
                AppFileLogger.LogEvent(
                    eventName: "UiActionCanceled",
                    level: "INFO",
                    message: $"{actionName} canceled.",
                    fields: new Dictionary<string, object?>
                    {
                        ["durationMs"] = stopwatch.ElapsedMilliseconds
                    }
                );

                return SafeAsyncActionResult.FromCanceled(resolvedCorrelationId, stopwatch.ElapsedMilliseconds);
            }

            stopwatch.Stop();
            AppFileLogger.LogEvent(
                eventName: "UiActionFailed",
                level: "ERROR",
                message: $"{actionName} failed.",
                ex: ex,
                fields: new Dictionary<string, object?>
                {
                    ["durationMs"] = stopwatch.ElapsedMilliseconds
                }
            );

            return SafeAsyncActionResult.FromFailed(resolvedCorrelationId, stopwatch.ElapsedMilliseconds, ex);
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            AppFileLogger.LogEvent(
                eventName: "UiActionFailed",
                level: "ERROR",
                message: $"{actionName} failed.",
                ex: ex,
                fields: new Dictionary<string, object?>
                {
                    ["durationMs"] = stopwatch.ElapsedMilliseconds
                }
            );

            return SafeAsyncActionResult.FromFailed(resolvedCorrelationId, stopwatch.ElapsedMilliseconds, ex);
        }
    }
}

public sealed record SafeAsyncActionResult(
    bool Succeeded,
    bool Canceled,
    string CorrelationId,
    long DurationMs,
    Exception? Exception
)
{
    public static SafeAsyncActionResult FromSucceeded(string correlationId, long durationMs)
    {
        return new SafeAsyncActionResult(
            Succeeded: true,
            Canceled: false,
            CorrelationId: correlationId,
            DurationMs: durationMs,
            Exception: null
        );
    }

    public static SafeAsyncActionResult FromCanceled(string correlationId, long durationMs)
    {
        return new SafeAsyncActionResult(
            Succeeded: false,
            Canceled: true,
            CorrelationId: correlationId,
            DurationMs: durationMs,
            Exception: null
        );
    }

    public static SafeAsyncActionResult FromFailed(
        string correlationId,
        long durationMs,
        Exception exception
    )
    {
        return new SafeAsyncActionResult(
            Succeeded: false,
            Canceled: false,
            CorrelationId: correlationId,
            DurationMs: durationMs,
            Exception: exception
        );
    }
}
