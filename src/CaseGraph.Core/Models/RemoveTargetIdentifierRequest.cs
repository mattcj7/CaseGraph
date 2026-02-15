namespace CaseGraph.Core.Models;

public sealed record RemoveTargetIdentifierRequest(
    Guid CaseId,
    Guid TargetId,
    Guid IdentifierId,
    string SourceLocator = "manual:targets/identifier-remove"
);
