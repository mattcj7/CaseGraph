using Microsoft.Data.Sqlite;

namespace CaseGraph.Infrastructure.Services;

public static class SqliteWriteRetryPolicy
{
    private static readonly TimeSpan[] DefaultRetryDelays =
    [
        TimeSpan.FromMilliseconds(50),
        TimeSpan.FromMilliseconds(100),
        TimeSpan.FromMilliseconds(200),
        TimeSpan.FromMilliseconds(400)
    ];

    public static Task ExecuteAsync(
        Func<CancellationToken, Task> operation,
        CancellationToken ct,
        int maxRetries = 4,
        Func<int, TimeSpan>? retryDelaySelector = null
    )
    {
        ArgumentNullException.ThrowIfNull(operation);
        return ExecuteInternalAsync<object?>(
            async token =>
            {
                await operation(token);
                return null;
            },
            ct,
            maxRetries,
            retryDelaySelector
        );
    }

    public static Task<T> ExecuteAsync<T>(
        Func<CancellationToken, Task<T>> operation,
        CancellationToken ct,
        int maxRetries = 4,
        Func<int, TimeSpan>? retryDelaySelector = null
    )
    {
        ArgumentNullException.ThrowIfNull(operation);
        return ExecuteInternalAsync(
            operation,
            ct,
            maxRetries,
            retryDelaySelector
        );
    }

    public static bool IsBusyOrLocked(Exception ex)
    {
        ArgumentNullException.ThrowIfNull(ex);

        for (Exception? current = ex; current is not null; current = current.InnerException)
        {
            if (current is not SqliteException sqliteEx)
            {
                continue;
            }

            if (IsBusyOrLocked(sqliteEx.SqliteErrorCode)
                || IsBusyOrLocked(sqliteEx.SqliteExtendedErrorCode))
            {
                return true;
            }
        }

        return false;
    }

    private static async Task<T> ExecuteInternalAsync<T>(
        Func<CancellationToken, Task<T>> operation,
        CancellationToken ct,
        int maxRetries,
        Func<int, TimeSpan>? retryDelaySelector
    )
    {
        if (maxRetries < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxRetries));
        }

        for (var attempt = 0; ; attempt++)
        {
            ct.ThrowIfCancellationRequested();

            try
            {
                return await operation(ct);
            }
            catch (Exception ex) when (IsBusyOrLocked(ex) && attempt < maxRetries)
            {
                var retryDelay = retryDelaySelector?.Invoke(attempt) ?? ResolveDefaultDelay(attempt);
                if (retryDelay > TimeSpan.Zero)
                {
                    await Task.Delay(retryDelay, ct);
                }
            }
        }
    }

    private static TimeSpan ResolveDefaultDelay(int attempt)
    {
        if (attempt < 0)
        {
            return TimeSpan.Zero;
        }

        return attempt < DefaultRetryDelays.Length
            ? DefaultRetryDelays[attempt]
            : DefaultRetryDelays[^1];
    }

    private static bool IsBusyOrLocked(int sqliteCode)
    {
        return sqliteCode is 5 or 6; // SQLITE_BUSY, SQLITE_LOCKED
    }
}
