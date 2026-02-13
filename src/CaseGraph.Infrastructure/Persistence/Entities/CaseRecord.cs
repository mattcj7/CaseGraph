namespace CaseGraph.Infrastructure.Persistence.Entities;

public sealed class CaseRecord
{
    public Guid CaseId { get; set; }

    public string Name { get; set; } = string.Empty;

    public DateTimeOffset CreatedAtUtc { get; set; }

    public DateTimeOffset? LastOpenedAtUtc { get; set; }

    public List<EvidenceItemRecord> EvidenceItems { get; set; } = new();
}
