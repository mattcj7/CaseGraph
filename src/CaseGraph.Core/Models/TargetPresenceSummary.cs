namespace CaseGraph.Core.Models;

public sealed record TargetPresenceSummary(
    Guid TargetId,
    int TotalCount,
    DateTimeOffset? LastSeenUtc,
    IReadOnlyList<TargetPresenceIdentifierSummary> ByIdentifier
);

public sealed record TargetPresenceIdentifierSummary(
    Guid IdentifierId,
    TargetIdentifierType Type,
    string ValueDisplay,
    int Count,
    DateTimeOffset? LastSeenUtc
);
