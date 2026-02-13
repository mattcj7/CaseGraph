namespace CaseGraph.Core.Models;

public sealed record MessageSearchHit(
    Guid MessageEventId,
    Guid CaseId,
    Guid EvidenceItemId,
    string Platform,
    DateTimeOffset? TimestampUtc,
    string? Sender,
    string? Snippet,
    string SourceLocator
)
{
    public string? Body { get; init; }

    public string? Recipients { get; init; }

    public string? ThreadKey { get; init; }

    public string? EvidenceDisplayName { get; init; }

    public string? StoredRelativePath { get; init; }
}
