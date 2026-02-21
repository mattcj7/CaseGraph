namespace CaseGraph.Core.Diagnostics;

public static class UiExceptionReportContextBuilder
{
    public static Dictionary<string, object?> Build(
        string correlationId,
        IAppSessionState? sessionState
    )
    {
        var context = new Dictionary<string, object?>
        {
            ["correlationId"] = correlationId
        };

        if (sessionState is not null)
        {
            var caseId = sessionState.CurrentCaseId;
            if (caseId.HasValue)
            {
                context["caseId"] = caseId.Value.ToString("D");
            }

            var evidenceId = sessionState.CurrentEvidenceId;
            if (evidenceId.HasValue)
            {
                context["evidenceId"] = evidenceId.Value.ToString("D");
            }
        }

        var scopedAction = AppFileLogger.GetScopeValue("actionName");
        if (!string.IsNullOrWhiteSpace(scopedAction))
        {
            context["actionName"] = scopedAction;
        }

        var scopedJobId = AppFileLogger.GetScopeValue("jobId");
        if (!string.IsNullOrWhiteSpace(scopedJobId))
        {
            context["jobId"] = scopedJobId;
        }

        var scopedJobType = AppFileLogger.GetScopeValue("jobType");
        if (!string.IsNullOrWhiteSpace(scopedJobType))
        {
            context["jobType"] = scopedJobType;
        }

        return context;
    }
}
