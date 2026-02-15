namespace CaseGraph.Core.Models;

public sealed record AddTargetAliasRequest(
    Guid CaseId,
    Guid TargetId,
    string Alias,
    string SourceLocator = "manual:targets/alias-add"
);
