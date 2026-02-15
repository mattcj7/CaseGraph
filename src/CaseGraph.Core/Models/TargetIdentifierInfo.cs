namespace CaseGraph.Core.Models;

public sealed record TargetIdentifierInfo(
    Guid IdentifierId,
    Guid CaseId,
    TargetIdentifierType Type,
    string ValueRaw,
    string ValueNormalized,
    string? Notes,
    DateTimeOffset CreatedAtUtc,
    bool IsPrimary
);
