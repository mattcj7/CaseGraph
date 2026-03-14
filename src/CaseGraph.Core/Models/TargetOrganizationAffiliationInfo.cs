namespace CaseGraph.Core.Models;

public sealed record TargetOrganizationAffiliationInfo(
    Guid MembershipId,
    Guid OrganizationId,
    string OrganizationName,
    string OrganizationType,
    string OrganizationStatus,
    string? OrganizationSummary,
    string? Role,
    string MembershipStatus,
    int Confidence,
    DateTimeOffset? StartDateUtc,
    DateTimeOffset? EndDateUtc,
    DateTimeOffset? LastConfirmedDateUtc
);
