namespace CaseGraph.Core.Models;

public sealed record LinkTargetToGlobalPersonRequest(
    Guid CaseId,
    Guid TargetId,
    Guid GlobalEntityId,
    GlobalPersonIdentifierConflictResolution ConflictResolution = GlobalPersonIdentifierConflictResolution.Cancel,
    string SourceLocator = "manual:targets/global-link"
);
