namespace CaseGraph.Infrastructure.Persistence.Entities;

public sealed class AuditOrderKeyRecord
{
    public Guid AuditEventId { get; set; }

    public Guid? CaseId { get; set; }

    public string TimestampUtc { get; set; } = string.Empty;
}
