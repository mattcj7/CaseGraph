namespace CaseGraph.Infrastructure.GangDocumentation;

public interface IGangDocumentationService
{
    Task<IReadOnlyList<GangDocumentationRecord>> GetDocumentationForTargetAsync(
        Guid caseId,
        Guid targetId,
        CancellationToken ct
    );

    Task<GangDocumentationRecord> CreateDocumentationAsync(
        CreateGangDocumentationRequest request,
        CancellationToken ct
    );

    Task<GangDocumentationRecord> UpdateDocumentationAsync(
        UpdateGangDocumentationRequest request,
        CancellationToken ct
    );

    Task<GangDocumentationRecord> TransitionWorkflowAsync(
        TransitionGangDocumentationWorkflowRequest request,
        CancellationToken ct
    );

    Task<GangDocumentationCriterion> SaveCriterionAsync(
        SaveGangDocumentationCriterionRequest request,
        CancellationToken ct
    );

    Task RemoveCriterionAsync(
        Guid caseId,
        Guid documentationId,
        Guid criterionId,
        CancellationToken ct
    );
}

public static class GangDocumentationCatalog
{
    public const string WorkflowStatusDraft = "draft";
    public const string WorkflowStatusPendingSupervisorReview = "pending supervisor review";
    public const string WorkflowStatusApproved = "approved";
    public const string WorkflowStatusReturnedForChanges = "returned for changes";
    public const string WorkflowStatusInactive = "inactive";
    public const string WorkflowStatusPurgeReview = "purge review";
    public const string WorkflowStatusPurged = "purged";

    public const string WorkflowActionSubmitForReview = "submit for review";
    public const string WorkflowActionApprove = "approve";
    public const string WorkflowActionReturnForChanges = "return for changes";
    public const string WorkflowActionMarkInactive = "mark inactive";
    public const string WorkflowActionMarkPurgeReview = "mark purge review";
    public const string WorkflowActionPurge = "purge";

    public static IReadOnlyList<string> WorkflowStatuses { get; } =
    [
        WorkflowStatusDraft,
        WorkflowStatusPendingSupervisorReview,
        WorkflowStatusApproved,
        WorkflowStatusReturnedForChanges,
        WorkflowStatusInactive,
        WorkflowStatusPurgeReview,
        WorkflowStatusPurged
    ];

    public static IReadOnlyList<string> WorkflowActions { get; } =
    [
        WorkflowActionSubmitForReview,
        WorkflowActionApprove,
        WorkflowActionReturnForChanges,
        WorkflowActionMarkInactive,
        WorkflowActionMarkPurgeReview,
        WorkflowActionPurge
    ];

    public static IReadOnlySet<string> EditableWorkflowStatuses { get; } =
        new HashSet<string>(StringComparer.Ordinal)
        {
            WorkflowStatusDraft,
            WorkflowStatusReturnedForChanges
        };

    public static IReadOnlyList<string> AffiliationRoles { get; } =
    [
        "member",
        "associate",
        "former",
        "suspected",
        "affiliate"
    ];

    public static IReadOnlyList<string> CriterionTypes { get; } =
    [
        "self-admission",
        "tattoos / clothing / symbols",
        "social media evidence",
        "photos with documented members",
        "message content / phone evidence",
        "officer observation",
        "jail / classification intel",
        "confidential source",
        "other documented basis"
    ];

    public static bool IsEditableWorkflowStatus(string? workflowStatus)
    {
        return !string.IsNullOrWhiteSpace(workflowStatus)
            && EditableWorkflowStatuses.Contains(workflowStatus);
    }

    public static string GetWorkflowStatusDisplayName(string workflowStatus)
    {
        return workflowStatus switch
        {
            WorkflowStatusDraft => "Draft",
            WorkflowStatusPendingSupervisorReview => "Pending Supervisor Review",
            WorkflowStatusApproved => "Approved",
            WorkflowStatusReturnedForChanges => "Returned for Changes",
            WorkflowStatusInactive => "Inactive",
            WorkflowStatusPurgeReview => "Purge Review",
            WorkflowStatusPurged => "Purged",
            _ => workflowStatus
        };
    }

    public static string GetWorkflowActionDisplayName(string workflowAction)
    {
        return workflowAction switch
        {
            WorkflowActionSubmitForReview => "Submit for Review",
            WorkflowActionApprove => "Approve",
            WorkflowActionReturnForChanges => "Return for Changes",
            WorkflowActionMarkInactive => "Mark Inactive",
            WorkflowActionMarkPurgeReview => "Mark Purge Review",
            WorkflowActionPurge => "Purge",
            _ => workflowAction
        };
    }

    public static string GetApprovalStatus(string workflowStatus)
    {
        return workflowStatus switch
        {
            WorkflowStatusApproved => "approved",
            WorkflowStatusPendingSupervisorReview => "pending approval",
            WorkflowStatusPurgeReview => "pending approval",
            WorkflowStatusReturnedForChanges => "returned for changes",
            WorkflowStatusPurged => "purged",
            _ => "not submitted"
        };
    }
}

public sealed record CreateGangDocumentationRequest(
    Guid CaseId,
    Guid TargetId,
    Guid OrganizationId,
    Guid? SubgroupOrganizationId,
    string AffiliationRole,
    string Summary,
    string? Notes
);

public sealed record UpdateGangDocumentationRequest(
    Guid CaseId,
    Guid DocumentationId,
    Guid OrganizationId,
    Guid? SubgroupOrganizationId,
    string AffiliationRole,
    string Summary,
    string? Notes
);

public sealed record TransitionGangDocumentationWorkflowRequest(
    Guid CaseId,
    Guid DocumentationId,
    string WorkflowAction,
    string? ReviewerName,
    string? DecisionNote
);

public sealed record SaveGangDocumentationCriterionRequest(
    Guid CaseId,
    Guid DocumentationId,
    Guid? CriterionId,
    string CriterionType,
    bool IsMet,
    string BasisSummary,
    DateTimeOffset? ObservedDateUtc,
    string? SourceNote
);
