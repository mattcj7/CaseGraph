using CaseGraph.Core.Diagnostics;
using CaseGraph.Infrastructure.Diagnostics;

namespace CaseGraph.App.Services;

public sealed class CaseOpenReadinessService : ICaseOpenReadinessService
{
    private readonly IWorkspaceMigrationService _workspaceMigrationService;
    private readonly IPerformanceInstrumentation _performanceInstrumentation;

    public CaseOpenReadinessService(
        IWorkspaceMigrationService workspaceMigrationService,
        IPerformanceInstrumentation? performanceInstrumentation = null
    )
    {
        _workspaceMigrationService = workspaceMigrationService;
        _performanceInstrumentation = performanceInstrumentation
            ?? new PerformanceInstrumentation(new PerformanceBudgetOptions(), TimeProvider.System);
    }

    public async Task<ReadinessResult> EnsureReadyAsync(
        Guid caseId,
        IProgress<ReadinessProgress>? progress,
        CancellationToken ct
    )
    {
        var correlationId = AppFileLogger.NewCorrelationId();
        return await _performanceInstrumentation.TrackAsync(
            new PerformanceOperationContext(
                PerformanceOperationKinds.CaseOpen,
                "CaseOpenReadiness",
                CaseId: caseId,
                CorrelationId: correlationId
            ),
            async innerCt =>
            {
                Report(
                    progress,
                    new ReadinessProgress(
                        ReadinessPhase.CaseOpen,
                        "Preparing case...",
                        "Checking case readiness tasks.",
                        Progress: 0.2,
                        CaseId: caseId
                    )
                );
                LogEvent(
                    "CaseOpenReadinessStarted",
                    "Case-open readiness started.",
                    correlationId,
                    caseId
                );

                try
                {
                    Report(
                        progress,
                        new ReadinessProgress(
                            ReadinessPhase.CaseOpen,
                            "Preparing case...",
                            "Reconciling interrupted case jobs.",
                            Progress: 0.6,
                            CaseId: caseId
                        )
                    );
                    var workPerformed = await _workspaceMigrationService.RunCaseOpenReadinessAsync(
                        innerCt
                    );
                    var summary = workPerformed
                        ? "Case readiness completed."
                        : "Case readiness already current.";

                    Report(
                        progress,
                        new ReadinessProgress(
                            ReadinessPhase.CaseOpen,
                            "Preparing case...",
                            summary,
                            Progress: 0.9,
                            CaseId: caseId
                        )
                    );
                    LogEvent(
                        "CaseOpenReadinessCompleted",
                        summary,
                        correlationId,
                        caseId,
                        workPerformed: workPerformed
                    );
                    return new ReadinessResult(workPerformed, summary);
                }
                catch (Exception ex)
                {
                    AppFileLogger.LogEvent(
                        eventName: "CaseOpenReadinessFailed",
                        level: "ERROR",
                        message: "Case-open readiness failed.",
                        ex: ex,
                        fields: BuildFields(correlationId, caseId, feature: null, workPerformed: null)
                    );
                    throw;
                }
            },
            ct
        );
    }

    private static void Report(IProgress<ReadinessProgress>? progress, ReadinessProgress update)
    {
        progress?.Report(update);
    }

    private static void LogEvent(
        string eventName,
        string message,
        string correlationId,
        Guid caseId,
        bool? workPerformed = null
    )
    {
        AppFileLogger.LogEvent(
            eventName: eventName,
            level: "INFO",
            message: message,
            fields: BuildFields(correlationId, caseId, feature: null, workPerformed: workPerformed)
        );
    }

    private static Dictionary<string, object?> BuildFields(
        string correlationId,
        Guid caseId,
        ReadinessFeature? feature,
        bool? workPerformed
    )
    {
        var fields = new Dictionary<string, object?>
        {
            ["phase"] = ReadinessPhase.CaseOpen.ToString(),
            ["correlationId"] = correlationId,
            ["caseId"] = caseId.ToString("D")
        };

        if (feature.HasValue)
        {
            fields["feature"] = feature.Value.ToString();
        }

        if (workPerformed.HasValue)
        {
            fields["workPerformed"] = workPerformed.Value;
        }

        return fields;
    }
}
