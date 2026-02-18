namespace CaseGraph.Infrastructure.Persistence.Entities;

public sealed class CaseOrderKeyRecord
{
    public Guid CaseId { get; set; }

    public string CreatedAtUtc { get; set; } = string.Empty;

    public string? LastOpenedAtUtc { get; set; }
}
