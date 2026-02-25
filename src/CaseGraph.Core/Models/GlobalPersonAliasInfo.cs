namespace CaseGraph.Core.Models;

public sealed record GlobalPersonAliasInfo(
    Guid AliasId,
    Guid GlobalEntityId,
    string Alias,
    string AliasNormalized,
    string? Notes
);
