namespace CaseGraph.Core.Models;

public sealed record CreateGlobalPersonForTargetRequest(
    Guid CaseId,
    Guid TargetId,
    string? DisplayName,
    GlobalPersonIdentifierConflictResolution ConflictResolution = GlobalPersonIdentifierConflictResolution.Cancel,
    string SourceLocator = "manual:targets/global-create-link"
);
