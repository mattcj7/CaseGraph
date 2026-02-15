namespace CaseGraph.Core.Models;

public sealed record UpdateTargetIdentifierRequest(
    Guid CaseId,
    Guid TargetId,
    Guid IdentifierId,
    TargetIdentifierType Type,
    string ValueRaw,
    string? Notes,
    bool IsPrimary,
    IdentifierConflictResolution ConflictResolution = IdentifierConflictResolution.Cancel,
    string SourceLocator = "manual:targets/identifier-update"
);
