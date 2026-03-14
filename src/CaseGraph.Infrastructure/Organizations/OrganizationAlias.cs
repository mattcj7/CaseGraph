namespace CaseGraph.Infrastructure.Organizations;

public sealed class OrganizationAlias
{
    public Guid AliasId { get; set; }

    public Guid OrganizationId { get; set; }

    public string Alias { get; set; } = string.Empty;

    public string AliasNormalized { get; set; } = string.Empty;

    public string? Notes { get; set; }

    public DateTimeOffset CreatedAtUtc { get; set; }

    public OrganizationRecord? Organization { get; set; }
}
