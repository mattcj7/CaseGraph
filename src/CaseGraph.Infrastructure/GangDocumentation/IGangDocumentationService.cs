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
    public static IReadOnlyList<string> DocumentationStatuses { get; } =
    [
        "draft",
        "pending review",
        "approved",
        "inactive",
        "purge review"
    ];

    public static IReadOnlyList<string> ApprovalStatuses { get; } =
    [
        "not submitted",
        "pending approval",
        "approved",
        "rejected"
    ];

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
}

public sealed record CreateGangDocumentationRequest(
    Guid CaseId,
    Guid TargetId,
    Guid OrganizationId,
    Guid? SubgroupOrganizationId,
    string AffiliationRole,
    string DocumentationStatus,
    string ApprovalStatus,
    string? Reviewer,
    DateTimeOffset? ReviewDueDateUtc,
    string Summary,
    string? Notes
);

public sealed record UpdateGangDocumentationRequest(
    Guid CaseId,
    Guid DocumentationId,
    Guid OrganizationId,
    Guid? SubgroupOrganizationId,
    string AffiliationRole,
    string DocumentationStatus,
    string ApprovalStatus,
    string? Reviewer,
    DateTimeOffset? ReviewDueDateUtc,
    string Summary,
    string? Notes
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
