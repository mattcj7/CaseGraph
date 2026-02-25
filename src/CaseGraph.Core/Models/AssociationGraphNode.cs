namespace CaseGraph.Core.Models;

public sealed record AssociationGraphNode(
    string NodeId,
    AssociationGraphNodeKind Kind,
    string Label,
    Guid? TargetId,
    Guid? IdentifierId,
    Guid? GlobalEntityId,
    TargetIdentifierType? IdentifierType,
    string? IdentifierValueDisplay,
    string? IdentifierValueNormalized,
    int MessageEventCount,
    int ConnectionCount,
    int LinkedIdentifierCount,
    int ContributingTargetCount,
    DateTimeOffset? LastSeenUtc,
    IReadOnlyList<Guid> ContributingTargetIds,
    IReadOnlyList<Guid> ContributingIdentifierIds
);
