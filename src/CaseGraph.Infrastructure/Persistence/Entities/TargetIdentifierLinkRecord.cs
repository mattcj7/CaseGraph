namespace CaseGraph.Infrastructure.Persistence.Entities;

public sealed class TargetIdentifierLinkRecord
{
    public Guid LinkId { get; set; }

    public Guid CaseId { get; set; }

    public Guid TargetId { get; set; }

    public Guid IdentifierId { get; set; }

    public bool IsPrimary { get; set; }

    public DateTimeOffset CreatedAtUtc { get; set; }

    public string SourceType { get; set; } = string.Empty;

    public Guid? SourceEvidenceItemId { get; set; }

    public string SourceLocator { get; set; } = string.Empty;

    public string IngestModuleVersion { get; set; } = string.Empty;

    public TargetRecord? Target { get; set; }

    public IdentifierRecord? Identifier { get; set; }
}
