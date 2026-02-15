namespace CaseGraph.Core.Models;

public sealed record IdentifierConflictInfo(
    Guid CaseId,
    Guid IdentifierId,
    TargetIdentifierType Type,
    string ValueRaw,
    string ValueNormalized,
    Guid ExistingTargetId,
    string ExistingTargetDisplayName
);
