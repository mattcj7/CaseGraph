using System.Text;

namespace CaseGraph.Core.Diagnostics;

public static class AppFileLogger
{
    private static readonly object Sync = new();

    public static void Log(string message)
    {
        try
        {
            var now = DateTimeOffset.Now;
            var logDirectory = GetLogDirectory();
            Directory.CreateDirectory(logDirectory);

            var logPath = Path.Combine(logDirectory, $"app-{now:yyyyMMdd}.log");
            var line = $"{now:yyyy-MM-dd HH:mm:ss.fff zzz} {message}{Environment.NewLine}";

            lock (Sync)
            {
                File.AppendAllText(logPath, line, Encoding.UTF8);
            }
        }
        catch
        {
            // Logging failures must never break app workflows.
        }
    }

    public static void LogException(string context, Exception ex)
    {
        Log($"{context} {ex.ToString()}");
    }

    public static void LogFatal(string context, Exception ex)
    {
        Log($"[FATAL] {context} {ex.ToString()}");
    }

    public static string GetLogDirectory()
    {
        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "CaseGraphOffline",
            "logs"
        );
    }

    public static string GetCurrentLogPath()
    {
        var now = DateTimeOffset.Now;
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
}
