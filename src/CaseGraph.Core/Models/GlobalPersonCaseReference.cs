namespace CaseGraph.Core.Models;

public sealed record GlobalPersonCaseReference(
    Guid CaseId,
    string CaseName,
    Guid TargetId,
    string TargetDisplayName
);
