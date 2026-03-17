namespace CaseGraph.Infrastructure.Persistence.Entities;

public sealed class GangDocumentationReviewRecord
{
    public Guid ReviewId { get; set; }

    public Guid DocumentationId { get; set; }

    public string WorkflowStatus { get; set; } = string.Empty;

    public string? ReviewerName { get; set; }

    public string? ReviewerIdentity { get; set; }

    public DateTimeOffset? SubmittedForReviewAtUtc { get; set; }

    public DateTimeOffset? ReviewedAtUtc { get; set; }

    public DateTimeOffset? ApprovedAtUtc { get; set; }

    public DateTimeOffset? ReviewDueDateUtc { get; set; }

    public string? DecisionNote { get; set; }

    public GangDocumentationRecordEntity? Documentation { get; set; }
}
