namespace CaseGraph.Core.Models;

public sealed record MessageSearchRequest(
    Guid CaseId,
    string? Query,
    string? PlatformFilter,
    string? SenderFilter,
    string? RecipientFilter,
    Guid? TargetId,
    TargetIdentifierType? IdentifierTypeFilter,
    MessageDirectionFilter DirectionFilter,
    DateTimeOffset? FromUtc,
    DateTimeOffset? ToUtc,
    int Take,
    int Skip
);
