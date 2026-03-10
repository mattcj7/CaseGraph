using CaseGraph.Core.Diagnostics;
using CaseGraph.Infrastructure.Diagnostics;
using CaseGraph.Infrastructure.Services;

namespace CaseGraph.App.Services;

public sealed class FeatureReadinessService : IFeatureReadinessService
{
    private readonly WorkspaceDbInitializer _workspaceDbInitializer;
    private readonly IBackgroundMaintenanceManager _backgroundMaintenanceManager;
    private readonly IPerformanceInstrumentation _performanceInstrumentation;

    public FeatureReadinessService(
        WorkspaceDbInitializer workspaceDbInitializer,
        IBackgroundMaintenanceManager backgroundMaintenanceManager,
        IPerformanceInstrumentation? performanceInstrumentation = null
    )
    {
        _workspaceDbInitializer = workspaceDbInitializer;
        _backgroundMaintenanceManager = backgroundMaintenanceManager;
        _performanceInstrumentation = performanceInstrumentation
            ?? new PerformanceInstrumentation(new PerformanceBudgetOptions(), TimeProvider.System);
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
        return await _performanceInstrumentation.TrackAsync(
            new PerformanceOperationContext(
                PerformanceOperationKinds.FeatureReadiness,
                "EnsureReady",
                FeatureName: feature.ToString(),
                CaseId: caseId,
                CorrelationId: correlationId,
                Fields: new Dictionary<string, object?>
                {
                    ["requiresMessageSearchIndex"] = requiresMessageSearchIndex
                }
            ),
            async innerCt =>
            {
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
                    workPerformed: null,
                    status: null
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
                            workPerformed: false,
                            status: null
                        );
                        return new ReadinessResult(false, noWorkSummary);
                    }

                    var readinessStatus = await _workspaceDbInitializer.GetMessageSearchReadinessStatusAsync(
                        innerCt
                    );
                    if (readinessStatus.IsCurrent)
                    {
                        const string currentSummary = "Message search readiness already current.";
                        Report(
                            progress,
                            new ReadinessProgress(
                                ReadinessPhase.FeatureOpen,
                                $"{ToDisplayName(feature)} ready.",
                                currentSummary,
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
                            workPerformed: false,
                            status: readinessStatus.State.ToString()
                        );
                        return new ReadinessResult(false, currentSummary);
                    }

                    Report(
                        progress,
                        new ReadinessProgress(
                            ReadinessPhase.FeatureOpen,
                            $"Preparing {ToDisplayName(feature)}...",
                            readinessStatus.State == MessageSearchReadinessState.MaintenanceInProgress
                                ? "Search index maintenance is already running."
                                : "Search index maintenance is required before keyword search can run.",
                            Progress: 0.4,
                            CaseId: caseId,
                            Feature: feature
                        )
                    );
                    var maintenanceRequest = _backgroundMaintenanceManager.QueueOrJoin(
                        new MaintenanceTaskRequest(
                            MaintenanceTaskKeys.MessageSearchIndex(caseId ?? Guid.Empty),
                            "Message search index maintenance",
                            MaintenanceTaskCategory.MessageSearchIndex,
                            caseId,
                            feature,
                            SupportsCancellation: false,
                            PendingStatusText: "Message search index maintenance queued.",
                            RunningStatusText: "Message search index maintenance running."
                        ),
                        async (maintenanceProgress, maintenanceCt) =>
                        {
                            maintenanceProgress.Report(
                                new MaintenanceProgressUpdate(
                                    "Message search index maintenance running.",
                                    "Reconciling message-search FTS state in the background."
                                )
                            );
                            var scheduledWork = await _workspaceDbInitializer
                                .EnsureMessageSearchMaintenanceScheduledAsync(maintenanceCt);
                            await scheduledWork.WaitAsync(maintenanceCt);
                            maintenanceProgress.Report(
                                new MaintenanceProgressUpdate(
                                    "Message search index maintenance completed.",
                                    "Message-search readiness is current."
                                )
                            );
                        }
                    );
                    var summary = maintenanceRequest.WasDeduplicated
                        || readinessStatus.State == MessageSearchReadinessState.MaintenanceInProgress
                        ? $"{ToDisplayName(feature)} is still preparing the message index. Your request will continue when ready."
                        : $"Preparing {ToDisplayName(feature)}. Message index maintenance is running before the request starts.";
                    LogEvent(
                        "FeatureReadinessDeferred",
                        $"{ToDisplayName(feature)} readiness deferred while message search maintenance runs.",
                        feature,
                        correlationId,
                        caseId,
                        workPerformed: true,
                        status: readinessStatus.State.ToString()
                    );
                    return new ReadinessResult(
                        WorkPerformed: true,
                        Summary: summary,
                        IsReady: false,
                        PendingWork: maintenanceRequest.ExecutionTask
                    );
                }
                catch (Exception ex)
                {
                    AppFileLogger.LogEvent(
                        eventName: "FeatureReadinessFailed",
                        level: "ERROR",
                        message: $"{ToDisplayName(feature)} readiness failed.",
                        ex: ex,
                        fields: BuildFields(
                            feature,
                            correlationId,
                            caseId,
                            workPerformed: null,
                            status: null
                        )
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
        ReadinessFeature feature,
        string correlationId,
        Guid? caseId,
        bool? workPerformed,
        string? status
    )
    {
        AppFileLogger.LogEvent(
            eventName: eventName,
            level: "INFO",
            message: message,
            fields: BuildFields(feature, correlationId, caseId, workPerformed, status)
        );
    }

    private static Dictionary<string, object?> BuildFields(
        ReadinessFeature feature,
        string correlationId,
        Guid? caseId,
        bool? workPerformed,
        string? status
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

        if (!string.IsNullOrWhiteSpace(status))
        {
            fields["readinessStatus"] = status;
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
