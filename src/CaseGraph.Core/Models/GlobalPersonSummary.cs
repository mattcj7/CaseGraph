namespace CaseGraph.Core.Models;

public sealed record GlobalPersonSummary(
    Guid GlobalEntityId,
    string DisplayName,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc
);
