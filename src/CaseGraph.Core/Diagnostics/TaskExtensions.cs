namespace CaseGraph.Core.Diagnostics;

public static class TaskExtensions
{
    public static void Forget(
        this Task task,
        string actionName,
        string? correlationId = null,
        Guid? caseId = null,
        Guid? evidenceId = null
    )
    {
        ArgumentNullException.ThrowIfNull(task);
        ArgumentException.ThrowIfNullOrWhiteSpace(actionName);

        var resolvedCorrelationId = string.IsNullOrWhiteSpace(correlationId)
            ? AppFileLogger.NewCorrelationId()
            : correlationId.Trim();

        if (task.IsCompleted)
        {
            ObserveCompletedTask(task, actionName, resolvedCorrelationId, caseId, evidenceId);
            return;
        }

        _ = ObserveAsync(task, actionName, resolvedCorrelationId, caseId, evidenceId);
    }

    private static void ObserveCompletedTask(
        Task task,
        string actionName,
        string correlationId,
        Guid? caseId,
        Guid? evidenceId
    )
    {
        if (task.IsCanceled)
        {
            return;
        }

        if (!task.IsFaulted)
        {
            return;
        }

        var exception = task.Exception?.GetBaseException() ?? task.Exception;
        if (exception is null)
        {
            return;
        }

        LogForgetFailure(actionName, correlationId, caseId, evidenceId, exception);
    }

    private static async Task ObserveAsync(
        Task task,
        string actionName,
        string correlationId,
        Guid? caseId,
        Guid? evidenceId
    )
    {
        try
        {
            await task.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Cancellation is expected for many background refresh operations.
        }
        catch (Exception ex)
        {
            LogForgetFailure(actionName, correlationId, caseId, evidenceId, ex);
        }
    }

    private static void LogForgetFailure(
        string actionName,
        string correlationId,
        Guid? caseId,
        Guid? evidenceId,
        Exception ex
    )
    {
        var fields = new Dictionary<string, object?>
        {
            ["actionName"] = actionName,
            ["correlationId"] = correlationId
        };

        if (caseId.HasValue)
        {
            fields["caseId"] = caseId.Value.ToString("D");
        }

        if (evidenceId.HasValue)
        {
            fields["evidenceId"] = evidenceId.Value.ToString("D");
        }

        AppFileLogger.LogEvent(
            eventName: "FireAndForgetTaskFailed",
            level: "ERROR",
            message: $"{actionName} fire-and-forget task failed.",
            ex: ex,
            fields: fields
        );
    }
}
