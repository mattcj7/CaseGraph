namespace CaseGraph.Infrastructure.Persistence.Entities;

public sealed class JobOrderKeyRecord
{
    public Guid JobId { get; set; }

    public Guid? CaseId { get; set; }

    public Guid? EvidenceItemId { get; set; }

    public string JobType { get; set; } = string.Empty;

    public string CreatedAtUtc { get; set; } = string.Empty;

    public string? StartedAtUtc { get; set; }

    public string? CompletedAtUtc { get; set; }
}
