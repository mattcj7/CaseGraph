namespace CaseGraph.Infrastructure.Persistence.Entities;

public sealed class GangDocumentationCriterionRecord
{
    public Guid CriterionId { get; set; }

    public Guid DocumentationId { get; set; }

    public string CriterionType { get; set; } = string.Empty;

    public bool IsMet { get; set; }

    public string BasisSummary { get; set; } = string.Empty;

    public DateTimeOffset? ObservedDateUtc { get; set; }

    public string? SourceNote { get; set; }

    public int SortOrder { get; set; }

    public DateTimeOffset CreatedAtUtc { get; set; }

    public DateTimeOffset UpdatedAtUtc { get; set; }

    public GangDocumentationRecordEntity? Documentation { get; set; }
}
