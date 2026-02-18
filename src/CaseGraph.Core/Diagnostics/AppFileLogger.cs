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
            var logDirectory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "CaseGraphOffline",
                "logs"
            );
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
}
