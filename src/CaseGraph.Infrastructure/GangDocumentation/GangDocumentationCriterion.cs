namespace CaseGraph.Infrastructure.GangDocumentation;

public sealed record GangDocumentationCriterion(
    Guid CriterionId,
    Guid DocumentationId,
    string CriterionType,
    bool IsMet,
    string BasisSummary,
    DateTimeOffset? ObservedDateUtc,
    string? SourceNote,
    int SortOrder,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc
);
