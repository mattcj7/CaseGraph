namespace CaseGraph.Infrastructure.Persistence.Entities;

public sealed class IdentifierRecord
{
    public Guid IdentifierId { get; set; }

    public Guid CaseId { get; set; }

    public string Type { get; set; } = string.Empty;

    public string ValueRaw { get; set; } = string.Empty;

    public string ValueNormalized { get; set; } = string.Empty;

    public string? Notes { get; set; }

    public DateTimeOffset CreatedAtUtc { get; set; }

    public string SourceType { get; set; } = string.Empty;

    public Guid? SourceEvidenceItemId { get; set; }

    public string SourceLocator { get; set; } = string.Empty;

    public string IngestModuleVersion { get; set; } = string.Empty;

    public CaseRecord? Case { get; set; }

    public ICollection<TargetIdentifierLinkRecord> TargetLinks { get; set; } = new List<TargetIdentifierLinkRecord>();
}
