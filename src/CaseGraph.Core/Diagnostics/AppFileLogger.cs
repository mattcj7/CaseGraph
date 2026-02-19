using System.Text;
using System.Text.Json;
using System.Threading;

namespace CaseGraph.Core.Diagnostics;

public static class AppFileLogger
{
    private const string LogDirectoryOverrideEnvironmentVariable = "CASEGRAPH_LOG_DIRECTORY";
    private static readonly object Sync = new();
    private static readonly AsyncLocal<ScopeFrame?> CurrentScope = new();

    public static string NewCorrelationId()
    {
        return Guid.NewGuid().ToString("N");
    }

    public static IDisposable BeginActionScope(
        string actionName,
        string? correlationId = null,
        Guid? caseId = null,
        Guid? evidenceId = null
    )
    {
        var fields = new Dictionary<string, object?>
        {
            ["actionName"] = actionName,
            ["correlationId"] = string.IsNullOrWhiteSpace(correlationId)
                ? NewCorrelationId()
                : correlationId
        };

        if (caseId.HasValue)
        {
            fields["caseId"] = caseId.Value.ToString("D");
        }

        if (evidenceId.HasValue)
        {
            fields["evidenceId"] = evidenceId.Value.ToString("D");
        }

        return BeginScope(fields);
    }

    public static IDisposable BeginJobScope(
        Guid jobId,
        string jobType,
        string? correlationId = null,
        Guid? caseId = null,
        Guid? evidenceId = null
    )
    {
        var fields = new Dictionary<string, object?>
        {
            ["jobId"] = jobId.ToString("D"),
            ["jobType"] = jobType,
            ["correlationId"] = string.IsNullOrWhiteSpace(correlationId)
                ? NewCorrelationId()
                : correlationId
        };

        if (caseId.HasValue)
        {
            fields["caseId"] = caseId.Value.ToString("D");
        }

        if (evidenceId.HasValue)
        {
            fields["evidenceId"] = evidenceId.Value.ToString("D");
        }

        return BeginScope(fields);
    }

    public static IDisposable BeginWorkspaceScope(string workspacePath)
    {
        var correlationId = GetScopeValue("correlationId");
        if (string.IsNullOrWhiteSpace(correlationId))
        {
            correlationId = NewCorrelationId();
        }

        return BeginScope(
            new Dictionary<string, object?>
            {
                ["workspacePath"] = workspacePath,
                ["correlationId"] = correlationId
            }
        );
    }

    public static IDisposable BeginScope(IReadOnlyDictionary<string, object?> fields)
    {
        var previous = CurrentScope.Value;
        CurrentScope.Value = new ScopeFrame(previous, fields);
        return new ScopeHandle(previous);
    }

    public static string? GetScopeValue(string key)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            return null;
        }

        var merged = CollectScopeFields();
        return merged.TryGetValue(key, out var value)
            ? value?.ToString()
            : null;
    }

    public static void Log(string message)
    {
        LogEvent("Log", "INFO", message);
    }

    public static void LogException(string context, Exception ex)
    {
        LogEvent("Exception", "ERROR", context, ex);
    }

    public static void LogFatal(string context, Exception ex)
    {
        LogEvent("Fatal", "FATAL", context, ex);
    }

    public static void LogEvent(
        string eventName,
        string level,
        string? message = null,
        Exception? ex = null,
        string? eventId = null,
        IReadOnlyDictionary<string, object?>? fields = null
    )
    {
        try
        {
            var nowUtc = DateTimeOffset.UtcNow;
            var payload = new Dictionary<string, object?>
            {
                ["timestampUtc"] = nowUtc.ToString("O"),
                ["level"] = string.IsNullOrWhiteSpace(level) ? "INFO" : level.ToUpperInvariant(),
                ["eventName"] = string.IsNullOrWhiteSpace(eventName) ? "Log" : eventName,
                ["eventId"] = string.IsNullOrWhiteSpace(eventId) ? eventName : eventId
            };

            foreach (var entry in CollectScopeFields())
            {
                payload[entry.Key] = entry.Value;
            }

            if (fields is not null)
            {
                foreach (var entry in fields)
                {
                    payload[entry.Key] = entry.Value;
                }
            }

            if (!payload.TryGetValue("correlationId", out var correlationValue)
                || string.IsNullOrWhiteSpace(correlationValue?.ToString()))
            {
                payload["correlationId"] = NewCorrelationId();
            }

            if (!string.IsNullOrWhiteSpace(message))
            {
                payload["message"] = message;
            }

            if (ex is not null)
            {
                payload["exception"] = ex.ToString();
            }

            var logDirectory = GetLogDirectory();
            Directory.CreateDirectory(logDirectory);

            var logPath = Path.Combine(logDirectory, $"app-{nowUtc:yyyyMMdd}.log");
            var line = JsonSerializer.Serialize(payload);

            lock (Sync)
            {
                File.AppendAllText(
                    logPath,
                    $"{line}{Environment.NewLine}",
                    Encoding.UTF8
                );
            }
        }
        catch
        {
            // Logging failures must never break app workflows.
        }
    }

    public static string GetLogDirectory()
    {
        var overrideDirectory = Environment.GetEnvironmentVariable(
            LogDirectoryOverrideEnvironmentVariable
        );
        if (!string.IsNullOrWhiteSpace(overrideDirectory))
        {
            return overrideDirectory.Trim();
        }

        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "CaseGraphOffline",
            "logs"
        );
    }

    public static string GetCurrentLogPath()
    {
        var now = DateTimeOffset.UtcNow;
        return Path.Combine(GetLogDirectory(), $"app-{now:yyyyMMdd}.log");
    }

    public static IReadOnlyList<string> ReadLastLogLines(int take)
    {
        if (take <= 0)
        {
            return Array.Empty<string>();
        }

        try
        {
            var logPath = GetCurrentLogPath();
            if (!File.Exists(logPath))
            {
                return Array.Empty<string>();
            }

            var lines = File.ReadAllLines(logPath, Encoding.UTF8);
            if (lines.Length <= take)
            {
                return lines;
            }

            return lines.Skip(lines.Length - take).ToArray();
        }
        catch
        {
            return Array.Empty<string>();
        }
    }

    public static void Flush()
    {
        // Writes are append-per-call, so there is no buffered writer to flush.
    }

    private static Dictionary<string, object?> CollectScopeFields()
    {
        var merged = new Dictionary<string, object?>(StringComparer.Ordinal);
        var frames = new Stack<ScopeFrame>();
        var current = CurrentScope.Value;
        while (current is not null)
        {
            frames.Push(current);
            current = current.Parent;
        }

        while (frames.Count > 0)
        {
            var frame = frames.Pop();
            foreach (var entry in frame.Fields)
            {
                merged[entry.Key] = entry.Value;
            }
        }

        return merged;
    }

    private sealed class ScopeFrame
    {
        public ScopeFrame(ScopeFrame? parent, IReadOnlyDictionary<string, object?> fields)
        {
            Parent = parent;
            Fields = fields;
        }

        public ScopeFrame? Parent { get; }

        public IReadOnlyDictionary<string, object?> Fields { get; }
    }

    private sealed class ScopeHandle : IDisposable
    {
        private readonly ScopeFrame? _previous;
        private bool _disposed;

        public ScopeHandle(ScopeFrame? previous)
        {
            _previous = previous;
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            CurrentScope.Value = _previous;
        }
    }
}
