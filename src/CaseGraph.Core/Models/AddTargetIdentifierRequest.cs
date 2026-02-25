namespace CaseGraph.Core.Models;

public sealed record AddTargetIdentifierRequest(
    Guid CaseId,
    Guid TargetId,
    TargetIdentifierType Type,
    string ValueRaw,
    string? Notes,
    bool IsPrimary,
    IdentifierConflictResolution ConflictResolution = IdentifierConflictResolution.Cancel,
    string SourceLocator = "manual:targets/identifier-add",
    GlobalPersonIdentifierConflictResolution GlobalConflictResolution = GlobalPersonIdentifierConflictResolution.Cancel
);
