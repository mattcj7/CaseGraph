namespace CaseGraph.Core.Models;

public sealed class AuditEvent
{
    public Guid AuditEventId { get; set; }

    public DateTimeOffset TimestampUtc { get; set; }

    public string Operator { get; set; } = string.Empty;

    public string ActionType { get; set; } = string.Empty;

    public Guid? CaseId { get; set; }

    public Guid? EvidenceItemId { get; set; }

    public string Summary { get; set; } = string.Empty;

    public string? JsonPayload { get; set; }
}
