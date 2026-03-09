using CaseGraph.Core.Diagnostics;

namespace CaseGraph.Infrastructure.Diagnostics;

public sealed record PerformanceOperationContext(
    string OperationKind,
    string OperationName,
    string? FeatureName = null,
    Guid? CaseId = null,
    Guid? EvidenceItemId = null,
    string? CorrelationId = null,
    IReadOnlyDictionary<string, object?>? Fields = null
);

public static class PerformanceOperationKinds
{
    public const string Startup = "Startup";
    public const string CaseOpen = "CaseOpen";
    public const string FeatureReadiness = "FeatureReadiness";
    public const string FeatureOpen = "FeatureOpen";
    public const string FeatureQuery = "FeatureQuery";
    public const string ImportMaintenance = "ImportMaintenance";
}

public static class PerformanceOutcomes
{
    public const string Succeeded = "Succeeded";
    public const string Canceled = "Canceled";
    public const string Faulted = "Faulted";
}

public sealed class PerformanceScope : IDisposable
{
    private readonly TimeProvider _timeProvider;
    private readonly PerformanceOperationContext _context;
    private readonly PerformanceBudgetResolution _budget;
    private readonly long _startedTimestamp;
    private readonly IDisposable _logScope;
    private bool _completed;

    internal PerformanceScope(
        TimeProvider timeProvider,
        PerformanceOperationContext context,
        PerformanceBudgetResolution budget
    )
    {
        _timeProvider = timeProvider;
        _context = context with
        {
            CorrelationId = ResolveCorrelationId(context.CorrelationId)
        };
        _budget = budget;
        _startedTimestamp = _timeProvider.GetTimestamp();
        _logScope = AppFileLogger.BeginScope(BuildScopeFields(_context));

        AppFileLogger.LogEvent(
            eventName: "PerformanceOperationStarted",
            level: "INFO",
            message: "Performance operation started.",
            fields: BuildFields(elapsedMs: null, outcome: null, slowPath: null, extraFields: null)
        );
    }

    public string CorrelationId => _context.CorrelationId!;

    public PerformanceOperationContext Context => _context;

    public void Complete(
        string outcome = PerformanceOutcomes.Succeeded,
        IReadOnlyDictionary<string, object?>? extraFields = null
    )
    {
        if (_completed)
        {
            return;
        }

        _completed = true;
        var elapsedMs = _timeProvider.GetElapsedTime(_startedTimestamp).TotalMilliseconds;
        var slowPath = elapsedMs > _budget.ThresholdMs;
        var fields = BuildFields(elapsedMs, outcome, slowPath, extraFields);

        AppFileLogger.LogEvent(
            eventName: "PerformanceOperationCompleted",
            level: "INFO",
            message: "Performance operation completed.",
            fields: fields
        );

        if (slowPath)
        {
            AppFileLogger.LogEvent(
                eventName: "PerformanceSlowPathWarning",
                level: "WARN",
                message: "Performance budget exceeded.",
                fields: fields
            );
        }

        _logScope.Dispose();
    }

    public void Dispose()
    {
        Complete();
    }

    private Dictionary<string, object?> BuildFields(
        double? elapsedMs,
        string? outcome,
        bool? slowPath,
        IReadOnlyDictionary<string, object?>? extraFields
    )
    {
        var fields = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["operationKind"] = _context.OperationKind,
            ["operationName"] = _context.OperationName,
            ["budgetKey"] = _budget.BudgetKey,
            ["thresholdMs"] = _budget.ThresholdMs,
            ["correlationId"] = _context.CorrelationId
        };

        if (!string.IsNullOrWhiteSpace(_context.FeatureName))
        {
            fields["feature"] = _context.FeatureName;
        }

        if (_context.CaseId.HasValue)
        {
            fields["caseId"] = _context.CaseId.Value.ToString("D");
        }

        if (_context.EvidenceItemId.HasValue)
        {
            fields["evidenceItemId"] = _context.EvidenceItemId.Value.ToString("D");
        }

        if (elapsedMs.HasValue)
        {
            fields["elapsedMs"] = Math.Round(elapsedMs.Value, 3);
        }

        if (!string.IsNullOrWhiteSpace(outcome))
        {
            fields["outcome"] = outcome;
        }

        if (slowPath.HasValue)
        {
            fields["slowPath"] = slowPath.Value;
        }

        if (_context.Fields is not null)
        {
            foreach (var entry in _context.Fields)
            {
                fields[entry.Key] = entry.Value;
            }
        }

        if (extraFields is not null)
        {
            foreach (var entry in extraFields)
            {
                fields[entry.Key] = entry.Value;
            }
        }

        return fields;
    }

    private static Dictionary<string, object?> BuildScopeFields(PerformanceOperationContext context)
    {
        var fields = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["correlationId"] = context.CorrelationId
        };

        if (context.CaseId.HasValue)
        {
            fields["caseId"] = context.CaseId.Value.ToString("D");
        }

        if (context.EvidenceItemId.HasValue)
        {
            fields["evidenceId"] = context.EvidenceItemId.Value.ToString("D");
        }

        if (!string.IsNullOrWhiteSpace(context.FeatureName))
        {
            fields["feature"] = context.FeatureName;
        }

        return fields;
    }

    private static string ResolveCorrelationId(string? correlationId)
    {
        if (!string.IsNullOrWhiteSpace(correlationId))
        {
            return correlationId.Trim();
        }

        return AppFileLogger.GetScopeValue("correlationId") ?? AppFileLogger.NewCorrelationId();
    }
}
