namespace CaseGraph.Core.Models;

public sealed record MessageParticipantLinkResult(
    Guid ParticipantLinkId,
    Guid IdentifierId,
    Guid EffectiveTargetId,
    bool CreatedTarget,
    bool CreatedIdentifier,
    bool MovedIdentifier,
    bool UsedExistingTarget
);
