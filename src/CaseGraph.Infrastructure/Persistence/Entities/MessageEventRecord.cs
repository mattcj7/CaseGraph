namespace CaseGraph.Infrastructure.Persistence.Entities;

public sealed class MessageEventRecord
{
    public Guid MessageEventId { get; set; }

    public Guid ThreadId { get; set; }

    public Guid CaseId { get; set; }

    public Guid EvidenceItemId { get; set; }

    public string Platform { get; set; } = string.Empty;

    public DateTimeOffset? TimestampUtc { get; set; }

    public string Direction { get; set; } = string.Empty;

    public string? Sender { get; set; }

    public string? Recipients { get; set; }

    public string? Body { get; set; }

    public bool IsDeleted { get; set; }

    public string SourceLocator { get; set; } = string.Empty;

    public string IngestModuleVersion { get; set; } = string.Empty;

    public MessageThreadRecord? Thread { get; set; }
}
