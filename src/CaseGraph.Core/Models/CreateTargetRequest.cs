namespace CaseGraph.Core.Models;

public sealed record CreateTargetRequest(
    Guid CaseId,
    string DisplayName,
    string? PrimaryAlias,
    string? Notes,
    string SourceLocator = "manual:targets/create",
    Guid? GlobalEntityId = null,
    bool CreateGlobalPerson = false
);
