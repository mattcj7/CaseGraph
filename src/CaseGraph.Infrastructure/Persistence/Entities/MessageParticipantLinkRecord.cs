namespace CaseGraph.Infrastructure.Persistence.Entities;

public sealed class MessageParticipantLinkRecord
{
    public Guid ParticipantLinkId { get; set; }

    public Guid CaseId { get; set; }

    public Guid MessageEventId { get; set; }

    public string Role { get; set; } = string.Empty;

    public string ParticipantRaw { get; set; } = string.Empty;

    public Guid IdentifierId { get; set; }

    public Guid? TargetId { get; set; }

    public DateTimeOffset CreatedAtUtc { get; set; }

    public string SourceType { get; set; } = string.Empty;

    public Guid SourceEvidenceItemId { get; set; }

    public string SourceLocator { get; set; } = string.Empty;

    public string IngestModuleVersion { get; set; } = string.Empty;

    public MessageEventRecord? MessageEvent { get; set; }

    public IdentifierRecord? Identifier { get; set; }

    public TargetRecord? Target { get; set; }
}
