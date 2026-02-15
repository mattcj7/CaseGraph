namespace CaseGraph.Core.Models;

public sealed record TargetIdentifierMutationResult(
    TargetIdentifierInfo Identifier,
    Guid EffectiveTargetId,
    bool CreatedIdentifier,
    bool MovedIdentifier,
    bool UsedExistingTarget
);
