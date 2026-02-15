namespace CaseGraph.Core.Models;

public sealed record TargetSummary(
    Guid TargetId,
    Guid CaseId,
    string DisplayName,
    string? PrimaryAlias,
    string? Notes,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc
);
