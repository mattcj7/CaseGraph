using CaseGraph.Core.Diagnostics;
using CaseGraph.Infrastructure.Services;

namespace CaseGraph.App.Services;

public sealed class FeatureReadinessService : IFeatureReadinessService
{
    private readonly WorkspaceDbInitializer _workspaceDbInitializer;

    public FeatureReadinessService(WorkspaceDbInitializer workspaceDbInitializer)
    {
        _workspaceDbInitializer = workspaceDbInitializer;
    }

    public async Task<ReadinessResult> EnsureReadyAsync(
        ReadinessFeature feature,
        Guid? caseId,
        bool requiresMessageSearchIndex,
        IProgress<ReadinessProgress>? progress,
        CancellationToken ct
    )
    {
        var correlationId = AppFileLogger.NewCorrelationId();
        Report(
            progress,
            new ReadinessProgress(
                ReadinessPhase.FeatureOpen,
                $"Preparing {ToDisplayName(feature)}...",
                requiresMessageSearchIndex
                    ? "Checking feature readiness."
                    : $"{ToDisplayName(feature)} is checking readiness.",
                Progress: 0.2,
                CaseId: caseId,
                Feature: feature
            )
        );
        LogEvent(
            "FeatureReadinessStarted",
            $"{ToDisplayName(feature)} readiness started.",
            feature,
            correlationId,
            caseId,
            workPerformed: null
        );

        try
        {
            if (!requiresMessageSearchIndex)
            {
                const string noWorkSummary = "No additional readiness work was required.";
                Report(
                    progress,
                    new ReadinessProgress(
                        ReadinessPhase.FeatureOpen,
                        $"{ToDisplayName(feature)} ready.",
                        noWorkSummary,
                        Progress: 1.0,
                        CaseId: caseId,
                        Feature: feature
                    )
                );
                LogEvent(
                    "FeatureReadinessCompleted",
                    $"{ToDisplayName(feature)} readiness completed without additional work.",
                    feature,
                    correlationId,
                    caseId,
                    workPerformed: false
                );
                return new ReadinessResult(false, noWorkSummary);
            }

            Report(
                progress,
                new ReadinessProgress(
                    ReadinessPhase.FeatureOpen,
                    $"Preparing {ToDisplayName(feature)}...",
                    "Ensuring the message search index is current.",
                    Progress: 0.6,
                    CaseId: caseId,
                    Feature: feature
                )
            );
            var workPerformed = await _workspaceDbInitializer.EnsureMessageSearchReadyAsync(ct);
            var summary = workPerformed
                ? "Message search readiness completed."
                : "Message search readiness already current.";

            Report(
                progress,
                new ReadinessProgress(
                    ReadinessPhase.FeatureOpen,
                    $"{ToDisplayName(feature)} ready.",
                    summary,
                    Progress: 1.0,
                    CaseId: caseId,
                    Feature: feature
                )
            );
            LogEvent(
                "FeatureReadinessCompleted",
                $"{ToDisplayName(feature)} readiness completed.",
                feature,
                correlationId,
                caseId,
                workPerformed: workPerformed
            );
            return new ReadinessResult(workPerformed, summary);
        }
        catch (Exception ex)
        {
            AppFileLogger.LogEvent(
                eventName: "FeatureReadinessFailed",
                level: "ERROR",
                message: $"{ToDisplayName(feature)} readiness failed.",
                ex: ex,
                fields: BuildFields(feature, correlationId, caseId, workPerformed: null)
            );
            throw;
        }
    }

    private static void Report(IProgress<ReadinessProgress>? progress, ReadinessProgress update)
    {
        progress?.Report(update);
    }

    private static void LogEvent(
        string eventName,
        string message,
        ReadinessFeature feature,
        string correlationId,
        Guid? caseId,
        bool? workPerformed
    )
    {
        AppFileLogger.LogEvent(
            eventName: eventName,
            level: "INFO",
            message: message,
            fields: BuildFields(feature, correlationId, caseId, workPerformed)
        );
    }

    private static Dictionary<string, object?> BuildFields(
        ReadinessFeature feature,
        string correlationId,
        Guid? caseId,
        bool? workPerformed
    )
    {
        var fields = new Dictionary<string, object?>
        {
            ["phase"] = ReadinessPhase.FeatureOpen.ToString(),
            ["feature"] = feature.ToString(),
            ["correlationId"] = correlationId
        };

        if (caseId.HasValue)
        {
            fields["caseId"] = caseId.Value.ToString("D");
        }

        if (workPerformed.HasValue)
        {
            fields["workPerformed"] = workPerformed.Value;
        }

        return fields;
    }

    private static string ToDisplayName(ReadinessFeature feature)
    {
        return feature switch
        {
            ReadinessFeature.Search => "Search",
            ReadinessFeature.Timeline => "Timeline",
            ReadinessFeature.Reports => "Reports",
            ReadinessFeature.IncidentWindow => "Incident Window",
            _ => feature.ToString()
        };
    }
}
