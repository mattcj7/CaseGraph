namespace CaseGraph.Infrastructure.Services;

public sealed record MessageIngestResult(
    int MessagesExtracted,
    int ThreadsCreated,
    string? SummaryOverride
);
