namespace CaseGraph.Infrastructure.GangDocumentation;

public sealed record GangDocumentationReview(
    Guid ReviewId,
    Guid DocumentationId,
    string WorkflowStatus,
    string? ReviewerName,
    string? ReviewerIdentity,
    DateTimeOffset? SubmittedForReviewAtUtc,
    DateTimeOffset? ReviewedAtUtc,
    DateTimeOffset? ApprovedAtUtc,
    DateTimeOffset? ReviewDueDateUtc,
    string? DecisionNote
)
{
    public string ApprovalStatus => GangDocumentationCatalog.GetApprovalStatus(WorkflowStatus);

    public string WorkflowStatusDisplay => GangDocumentationCatalog.GetWorkflowStatusDisplayName(WorkflowStatus);
}
