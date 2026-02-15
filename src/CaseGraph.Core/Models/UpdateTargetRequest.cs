namespace CaseGraph.Core.Models;

public sealed record UpdateTargetRequest(
    Guid CaseId,
    Guid TargetId,
    string DisplayName,
    string? PrimaryAlias,
    string? Notes,
    string SourceLocator = "manual:targets/update"
);
