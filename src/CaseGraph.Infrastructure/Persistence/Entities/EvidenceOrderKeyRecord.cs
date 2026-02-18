namespace CaseGraph.Infrastructure.Persistence.Entities;

public sealed class EvidenceOrderKeyRecord
{
    public Guid EvidenceItemId { get; set; }

    public Guid CaseId { get; set; }

    public string AddedAtUtc { get; set; } = string.Empty;
}
