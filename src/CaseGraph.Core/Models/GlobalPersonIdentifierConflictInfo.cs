namespace CaseGraph.Core.Models;

public sealed record GlobalPersonIdentifierConflictInfo(
    Guid PersonIdentifierId,
    TargetIdentifierType Type,
    string ValueDisplay,
    string ValueNormalized,
    Guid ExistingGlobalEntityId,
    string ExistingGlobalDisplayName
);
