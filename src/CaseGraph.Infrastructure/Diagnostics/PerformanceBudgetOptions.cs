namespace CaseGraph.Infrastructure.Diagnostics;

public sealed class PerformanceBudgetOptions
{
    private readonly Dictionary<string, int> _operationOverrides =
        new(StringComparer.OrdinalIgnoreCase)
        {
            [$"{PerformanceOperationKinds.Startup}:ApplicationStartup"] = 10000,
            [$"{PerformanceOperationKinds.FeatureQuery}:IncidentWindow:Run"] = 4000,
            [$"{PerformanceOperationKinds.ImportMaintenance}:MessageSearch:Maintenance"] = 20000,
            [$"{PerformanceOperationKinds.ImportMaintenance}:MessagesIngest:IngestEvidence"] = 20000,
            [$"{PerformanceOperationKinds.ImportMaintenance}:TargetPresenceIndex:Refresh"] = 15000
        };

    public int StartupThresholdMs { get; set; } = 8000;

    public int CaseOpenThresholdMs { get; set; } = 4000;

    public int FeatureReadinessThresholdMs { get; set; } = 1500;

    public int FeatureOpenThresholdMs { get; set; } = 2500;

    public int FeatureQueryThresholdMs { get; set; } = 2500;

    public int MaintenanceThresholdMs { get; set; } = 15000;

    public IDictionary<string, int> OperationOverrides => _operationOverrides;

    public PerformanceBudgetResolution Resolve(
        string operationKind,
        string operationName,
        string? featureName
    )
    {
        var featureKey = string.IsNullOrWhiteSpace(featureName)
            ? null
            : $"{operationKind}:{featureName}:{operationName}";
        if (featureKey is not null && _operationOverrides.TryGetValue(featureKey, out var featureThreshold))
        {
            return new PerformanceBudgetResolution(featureKey, featureThreshold);
        }

        var operationKey = $"{operationKind}:{operationName}";
        if (_operationOverrides.TryGetValue(operationKey, out var operationThreshold))
        {
            return new PerformanceBudgetResolution(operationKey, operationThreshold);
        }

        return new PerformanceBudgetResolution(
            operationKind,
            operationKind switch
            {
                PerformanceOperationKinds.Startup => StartupThresholdMs,
                PerformanceOperationKinds.CaseOpen => CaseOpenThresholdMs,
                PerformanceOperationKinds.FeatureReadiness => FeatureReadinessThresholdMs,
                PerformanceOperationKinds.FeatureOpen => FeatureOpenThresholdMs,
                PerformanceOperationKinds.FeatureQuery => FeatureQueryThresholdMs,
                PerformanceOperationKinds.ImportMaintenance => MaintenanceThresholdMs,
                _ => FeatureQueryThresholdMs
            }
        );
    }
}

public readonly record struct PerformanceBudgetResolution(string BudgetKey, int ThresholdMs);
