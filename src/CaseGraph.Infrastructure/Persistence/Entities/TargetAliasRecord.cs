namespace CaseGraph.Infrastructure.Persistence.Entities;

public sealed class TargetAliasRecord
{
    public Guid AliasId { get; set; }

    public Guid TargetId { get; set; }

    public Guid CaseId { get; set; }

    public string Alias { get; set; } = string.Empty;

    public string AliasNormalized { get; set; } = string.Empty;

    public string SourceType { get; set; } = string.Empty;

    public Guid? SourceEvidenceItemId { get; set; }

    public string SourceLocator { get; set; } = string.Empty;

    public string IngestModuleVersion { get; set; } = string.Empty;

    public TargetRecord? Target { get; set; }
}
