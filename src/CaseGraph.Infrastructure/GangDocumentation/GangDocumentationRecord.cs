namespace CaseGraph.Infrastructure.GangDocumentation;

public sealed record GangDocumentationRecord(
    Guid DocumentationId,
    Guid CaseId,
    Guid TargetId,
    Guid? GlobalEntityId,
    Guid OrganizationId,
    string OrganizationName,
    Guid? SubgroupOrganizationId,
    string? SubgroupOrganizationName,
    string AffiliationRole,
    string Summary,
    string? Notes,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc,
    GangDocumentationReview Review,
    IReadOnlyList<GangDocumentationCriterion> Criteria,
    IReadOnlyList<GangDocumentationStatusHistoryEntry> StatusHistory
)
{
    public string DocumentationStatus => Review.WorkflowStatus;

    public string ApprovalStatus => Review.ApprovalStatus;

    public string? Reviewer => Review.ReviewerName;

    public DateTimeOffset? ReviewDueDateUtc => Review.ReviewDueDateUtc;
}
