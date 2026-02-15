namespace CaseGraph.Infrastructure.Persistence.Entities;

public sealed class TargetRecord
{
    public Guid TargetId { get; set; }

    public Guid CaseId { get; set; }

    public string DisplayName { get; set; } = string.Empty;

    public string? PrimaryAlias { get; set; }

    public string? Notes { get; set; }

    public DateTimeOffset CreatedAtUtc { get; set; }

    public DateTimeOffset UpdatedAtUtc { get; set; }

    public string SourceType { get; set; } = string.Empty;

    public Guid? SourceEvidenceItemId { get; set; }

    public string SourceLocator { get; set; } = string.Empty;

    public string IngestModuleVersion { get; set; } = string.Empty;

    public CaseRecord? Case { get; set; }

    public ICollection<TargetAliasRecord> Aliases { get; set; } = new List<TargetAliasRecord>();

    public ICollection<TargetIdentifierLinkRecord> IdentifierLinks { get; set; } = new List<TargetIdentifierLinkRecord>();
}
