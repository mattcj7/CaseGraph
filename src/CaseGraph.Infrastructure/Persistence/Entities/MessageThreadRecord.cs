namespace CaseGraph.Infrastructure.Persistence.Entities;

public sealed class MessageThreadRecord
{
    public Guid ThreadId { get; set; }

    public Guid CaseId { get; set; }

    public Guid EvidenceItemId { get; set; }

    public string Platform { get; set; } = string.Empty;

    public string ThreadKey { get; set; } = string.Empty;

    public string? Title { get; set; }

    public DateTimeOffset CreatedAtUtc { get; set; }

    public string SourceLocator { get; set; } = string.Empty;

    public string IngestModuleVersion { get; set; } = string.Empty;

    public List<MessageEventRecord> MessageEvents { get; set; } = new();

    public List<MessageParticipantRecord> Participants { get; set; } = new();
}
