namespace CaseGraph.Core.Models;

public sealed record GlobalPersonIdentifierInfo(
    Guid PersonIdentifierId,
    Guid GlobalEntityId,
    TargetIdentifierType Type,
    string ValueDisplay,
    string ValueNormalized,
    bool IsPrimary,
    string? Notes,
    DateTimeOffset CreatedAtUtc
);
