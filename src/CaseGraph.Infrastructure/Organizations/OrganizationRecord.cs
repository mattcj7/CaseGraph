using CaseGraph.Infrastructure.Persistence.Entities;

namespace CaseGraph.Infrastructure.Organizations;

public sealed class OrganizationRecord
{
    public Guid OrganizationId { get; set; }

    public string Name { get; set; } = string.Empty;

    public string NameNormalized { get; set; } = string.Empty;

    public string Type { get; set; } = string.Empty;

    public string Status { get; set; } = string.Empty;

    public Guid? ParentOrganizationId { get; set; }

    public string? Summary { get; set; }

    public DateTimeOffset CreatedAtUtc { get; set; }

    public DateTimeOffset UpdatedAtUtc { get; set; }

    public OrganizationRecord? ParentOrganization { get; set; }

    public ICollection<OrganizationRecord> ChildOrganizations { get; set; } = new List<OrganizationRecord>();

    public ICollection<OrganizationAlias> Aliases { get; set; } = new List<OrganizationAlias>();

    public ICollection<OrganizationMembership> Memberships { get; set; } = new List<OrganizationMembership>();
}
