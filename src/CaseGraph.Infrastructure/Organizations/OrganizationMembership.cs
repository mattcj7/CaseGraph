using CaseGraph.Infrastructure.Persistence.Entities;

namespace CaseGraph.Infrastructure.Organizations;

public sealed class OrganizationMembership
{
    public Guid MembershipId { get; set; }

    public Guid OrganizationId { get; set; }

    public Guid GlobalEntityId { get; set; }

    public string? Role { get; set; }

    public string Status { get; set; } = string.Empty;

    public int Confidence { get; set; }

    public string? BasisSummary { get; set; }

    public DateTimeOffset? StartDateUtc { get; set; }

    public DateTimeOffset? EndDateUtc { get; set; }

    public DateTimeOffset? LastConfirmedDateUtc { get; set; }

    public string? Reviewer { get; set; }

    public string? ReviewNotes { get; set; }

    public DateTimeOffset CreatedAtUtc { get; set; }

    public DateTimeOffset UpdatedAtUtc { get; set; }

    public OrganizationRecord? Organization { get; set; }

    public PersonEntityRecord? GlobalPerson { get; set; }
}
