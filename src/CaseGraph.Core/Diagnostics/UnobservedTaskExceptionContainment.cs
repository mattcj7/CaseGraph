namespace CaseGraph.Core.Diagnostics;

public static class UnobservedTaskExceptionContainment
{
    public static void Handle(
        UnobservedTaskExceptionEventArgs eventArgs,
        string context,
        Action<AggregateException>? scheduleNotification = null
    )
    {
        ArgumentNullException.ThrowIfNull(eventArgs);
        var correlationId = AppFileLogger.NewCorrelationId();

        try
        {
            AppFileLogger.LogEvent(
                eventName: "UnobservedTaskException",
                level: "ERROR",
                message: context,
                ex: eventArgs.Exception,
                fields: new Dictionary<string, object?>
                {
                    ["correlationId"] = correlationId
                }
            );
        }
        catch
        {
            // Logging must never throw.
        }

        try
        {
            eventArgs.SetObserved();
        }
        catch (Exception setObservedEx)
        {
            AppFileLogger.LogEvent(
                eventName: "UnobservedTaskExceptionSetObservedFailed",
                level: "WARN",
                message: "Failed to mark unobserved exception as observed.",
                ex: setObservedEx,
                fields: new Dictionary<string, object?>
                {
                    ["correlationId"] = correlationId
                }
            );
        }

        if (scheduleNotification is null)
        {
            return;
        }

        try
        {
            scheduleNotification(eventArgs.Exception);
        }
        catch (Exception notifyEx)
        {
            AppFileLogger.LogEvent(
                eventName: "UnobservedTaskExceptionNotificationFailed",
                level: "WARN",
                message: "Failed to schedule unobserved exception UI notification.",
                ex: notifyEx,
                fields: new Dictionary<string, object?>
                {
                    ["correlationId"] = correlationId
                }
            );
        }
    }
}
