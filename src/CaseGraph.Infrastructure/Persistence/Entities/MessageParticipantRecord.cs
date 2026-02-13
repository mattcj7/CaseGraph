namespace CaseGraph.Infrastructure.Persistence.Entities;

public sealed class MessageParticipantRecord
{
    public Guid ParticipantId { get; set; }

    public Guid ThreadId { get; set; }

    public string Value { get; set; } = string.Empty;

    public string Kind { get; set; } = string.Empty;

    public string SourceLocator { get; set; } = string.Empty;

    public string IngestModuleVersion { get; set; } = string.Empty;

    public MessageThreadRecord? Thread { get; set; }
}
