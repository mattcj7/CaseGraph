namespace CaseGraph.Core.Models;

public sealed record TargetAliasInfo(
    Guid AliasId,
    Guid TargetId,
    Guid CaseId,
    string Alias,
    string AliasNormalized
);
