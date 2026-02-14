namespace CaseGraph.Infrastructure.Services;

public sealed record MessageIngestProgress(
    double FractionComplete,
    string Phase,
    int? Processed,
    int? Total
);
