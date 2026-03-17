using CaseGraph.Infrastructure.Organizations;

namespace CaseGraph.Infrastructure.Persistence.Entities;

public sealed class GangDocumentationRecordEntity
{
    public Guid DocumentationId { get; set; }

    public Guid CaseId { get; set; }

    public Guid TargetId { get; set; }

    public Guid? GlobalEntityId { get; set; }

    public Guid OrganizationId { get; set; }

    public Guid? SubgroupOrganizationId { get; set; }

    public string AffiliationRole { get; set; } = string.Empty;

    public string DocumentationStatus { get; set; } = string.Empty;

    public string ApprovalStatus { get; set; } = string.Empty;

    public string? Reviewer { get; set; }

    public DateTimeOffset? ReviewDueDateUtc { get; set; }

    public string Summary { get; set; } = string.Empty;

    public string? Notes { get; set; }

    public DateTimeOffset CreatedAtUtc { get; set; }

    public DateTimeOffset UpdatedAtUtc { get; set; }

    public TargetRecord? Target { get; set; }

    public PersonEntityRecord? GlobalPerson { get; set; }

    public OrganizationRecord? Organization { get; set; }

    public OrganizationRecord? SubgroupOrganization { get; set; }

    public GangDocumentationReviewRecord? Review { get; set; }

    public ICollection<GangDocumentationCriterionRecord> Criteria { get; set; } =
        new List<GangDocumentationCriterionRecord>();

    public ICollection<GangDocumentationStatusHistoryRecord> StatusHistory { get; set; } =
        new List<GangDocumentationStatusHistoryRecord>();
}
