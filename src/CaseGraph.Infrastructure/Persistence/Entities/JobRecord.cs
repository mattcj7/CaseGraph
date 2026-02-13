namespace CaseGraph.Infrastructure.Persistence.Entities;

public sealed class JobRecord
{
    public Guid JobId { get; set; }

    public DateTimeOffset CreatedAtUtc { get; set; }

    public DateTimeOffset? StartedAtUtc { get; set; }

    public DateTimeOffset? CompletedAtUtc { get; set; }

    public string Status { get; set; } = string.Empty;

    public string JobType { get; set; } = string.Empty;

    public Guid? CaseId { get; set; }

    public Guid? EvidenceItemId { get; set; }

    public double Progress { get; set; }

    public string StatusMessage { get; set; } = string.Empty;

    public string? ErrorMessage { get; set; }

    public string JsonPayload { get; set; } = string.Empty;

    public string CorrelationId { get; set; } = string.Empty;

    public string Operator { get; set; } = string.Empty;
}
