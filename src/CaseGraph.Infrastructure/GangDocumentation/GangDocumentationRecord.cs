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
    string DocumentationStatus,
    string ApprovalStatus,
    string? Reviewer,
    DateTimeOffset? ReviewDueDateUtc,
    string Summary,
    string? Notes,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc,
    IReadOnlyList<GangDocumentationCriterion> Criteria,
    IReadOnlyList<GangDocumentationStatusHistoryEntry> StatusHistory
);

public sealed record GangDocumentationStatusHistoryEntry(
    Guid HistoryEntryId,
    Guid DocumentationId,
    string ActionType,
    string Summary,
    string? ChangedBy,
    DateTimeOffset ChangedAtUtc
);
