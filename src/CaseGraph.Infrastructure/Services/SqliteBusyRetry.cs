using CaseGraph.Core.Diagnostics;

namespace CaseGraph.Infrastructure.Services;

public static class SqliteBusyRetry
{
    private static readonly TimeSpan[] BaseRetryDelays =
    [
        TimeSpan.FromMilliseconds(50),
        TimeSpan.FromMilliseconds(100),
        TimeSpan.FromMilliseconds(200),
        TimeSpan.FromMilliseconds(400),
        TimeSpan.FromMilliseconds(800)
    ];

    private static readonly TimeSpan MaxElapsed = TimeSpan.FromSeconds(5);
    private const int MinimumRetryAttempts = 2;

    public static Task ExecuteAsync(
        string operationName,
        string workspaceDbPath,
        Func<CancellationToken, Task> operation,
        CancellationToken ct,
        string? correlationId = null,
        IReadOnlyDictionary<string, object?>? fields = null
    )
    {
        ArgumentNullException.ThrowIfNull(operation);
        return ExecuteAsync<object?>(
            operationName,
            workspaceDbPath,
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

    public static async Task<T> ExecuteAsync<T>(
        string operationName,
        string workspaceDbPath,
        Func<CancellationToken, Task<T>> operation,
        CancellationToken ct,
        string? correlationId = null,
        IReadOnlyDictionary<string, object?>? fields = null
    )
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(operationName);
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceDbPath);
        ArgumentNullException.ThrowIfNull(operation);

        var startedAtUtc = DateTimeOffset.UtcNow;
        var attemptCount = 0;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            attemptCount++;

            try
            {
                return await operation(ct);
            }
            catch (Exception ex) when (SqliteWriteRetryPolicy.IsBusyOrLocked(ex))
            {
                var elapsed = DateTimeOffset.UtcNow - startedAtUtc;
                var retryDelay = ResolveRetryDelay(attemptCount - 1, elapsed);
                if (retryDelay <= TimeSpan.Zero && attemptCount < MinimumRetryAttempts)
                {
                    retryDelay = ApplyJitter(TimeSpan.FromMilliseconds(50));
                }

                if (retryDelay <= TimeSpan.Zero)
                {
                    LogRetryExhausted(
                        operationName,
                        workspaceDbPath,
                        attemptCount,
                        correlationId,
                        fields,
                        ex
                    );
                    throw new WorkspaceDbLockedException(
                        operationName,
                        attemptCount,
                        workspaceDbPath,
                        ex
                    );
                }

                LogRetryAttempt(
                    operationName,
                    workspaceDbPath,
                    attemptCount,
                    retryDelay,
                    correlationId,
                    fields,
                    ex
                );
                await Task.Delay(retryDelay, ct);
            }
        }
    }

    private static TimeSpan ResolveRetryDelay(int retryIndex, TimeSpan elapsed)
    {
        if (elapsed >= MaxElapsed)
        {
            return TimeSpan.Zero;
        }

        var baseDelay = ResolveBaseDelay(retryIndex);
        var jitteredDelay = ApplyJitter(baseDelay);
        var remaining = MaxElapsed - elapsed;
        if (remaining <= TimeSpan.Zero)
        {
            return TimeSpan.Zero;
        }

        return jitteredDelay <= remaining
            ? jitteredDelay
            : TimeSpan.Zero;
    }

    private static TimeSpan ResolveBaseDelay(int retryIndex)
    {
        if (retryIndex < 0)
        {
            return TimeSpan.Zero;
        }

        return retryIndex < BaseRetryDelays.Length
            ? BaseRetryDelays[retryIndex]
            : BaseRetryDelays[^1];
    }

    private static TimeSpan ApplyJitter(TimeSpan delay)
    {
        if (delay <= TimeSpan.Zero)
        {
            return TimeSpan.Zero;
        }

        var jitterFactor = 0.85 + (Random.Shared.NextDouble() * 0.30);
        var jitteredMs = delay.TotalMilliseconds * jitterFactor;
        return TimeSpan.FromMilliseconds(Math.Max(1, jitteredMs));
    }

    private static void LogRetryAttempt(
        string operationName,
        string workspaceDbPath,
        int attempt,
        TimeSpan retryDelay,
        string? correlationId,
        IReadOnlyDictionary<string, object?>? fields,
        Exception ex
    )
    {
        var payload = BuildBaseFields(
            operationName,
            workspaceDbPath,
            attempt,
            correlationId,
            fields
        );
        payload["retryDelayMs"] = (int)retryDelay.TotalMilliseconds;
        payload["maxElapsedMs"] = (int)MaxElapsed.TotalMilliseconds;

        AppFileLogger.LogEvent(
            eventName: "SqliteBusyRetry",
            level: "WARN",
            message: $"SQLite busy/locked retry for {operationName}.",
            ex: ex,
            fields: payload
        );
    }

    private static void LogRetryExhausted(
        string operationName,
        string workspaceDbPath,
        int attempt,
        string? correlationId,
        IReadOnlyDictionary<string, object?>? fields,
        Exception ex
    )
    {
        var payload = BuildBaseFields(
            operationName,
            workspaceDbPath,
            attempt,
            correlationId,
            fields
        );
        payload["maxElapsedMs"] = (int)MaxElapsed.TotalMilliseconds;

        AppFileLogger.LogEvent(
            eventName: "SqliteBusyRetryExhausted",
            level: "ERROR",
            message: $"SQLite busy/locked retries exhausted for {operationName}.",
            ex: ex,
            fields: payload
        );
    }

    private static Dictionary<string, object?> BuildBaseFields(
        string operationName,
        string workspaceDbPath,
        int attempt,
        string? correlationId,
        IReadOnlyDictionary<string, object?>? fields
    )
    {
        var payload = new Dictionary<string, object?>
        {
            ["operation"] = operationName,
            ["attempt"] = attempt,
            ["workspaceDbPath"] = workspaceDbPath
        };

        if (!string.IsNullOrWhiteSpace(correlationId))
        {
            payload["correlationId"] = correlationId;
        }

        if (fields is not null)
        {
            foreach (var pair in fields)
            {
                payload[pair.Key] = pair.Value;
            }
        }

        return payload;
    }
}
