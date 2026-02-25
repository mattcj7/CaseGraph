namespace CaseGraph.Infrastructure.Persistence.Entities;

public sealed class TargetMessagePresenceRecord
{
    public Guid PresenceId { get; set; }

    public Guid CaseId { get; set; }

    public Guid TargetId { get; set; }

    public Guid MessageEventId { get; set; }

    public Guid MatchedIdentifierId { get; set; }

    public string Role { get; set; } = string.Empty;

    public Guid EvidenceItemId { get; set; }

    public string SourceLocator { get; set; } = string.Empty;

    public DateTimeOffset? MessageTimestampUtc { get; set; }

    public DateTimeOffset FirstSeenUtc { get; set; }

    public DateTimeOffset LastSeenUtc { get; set; }

    public MessageEventRecord? MessageEvent { get; set; }

    public TargetRecord? Target { get; set; }

    public IdentifierRecord? MatchedIdentifier { get; set; }
}
