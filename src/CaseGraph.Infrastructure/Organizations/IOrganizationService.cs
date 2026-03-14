namespace CaseGraph.Infrastructure.Organizations;

public interface IOrganizationService
{
    Task<IReadOnlyList<OrganizationSummaryDto>> GetOrganizationsAsync(string? search, CancellationToken ct);

    Task<OrganizationDetailsDto?> GetOrganizationDetailsAsync(Guid organizationId, CancellationToken ct);

    Task<OrganizationSummaryDto> CreateOrganizationAsync(CreateOrganizationRequest request, CancellationToken ct);

    Task<OrganizationSummaryDto> UpdateOrganizationAsync(UpdateOrganizationRequest request, CancellationToken ct);

    Task<OrganizationAliasDto> AddAliasAsync(AddOrganizationAliasRequest request, CancellationToken ct);

    Task RemoveAliasAsync(Guid aliasId, CancellationToken ct);

    Task<OrganizationMembershipDto> AddMembershipAsync(
        AddOrganizationMembershipRequest request,
        CancellationToken ct
    );

    Task<OrganizationMembershipDto> UpdateMembershipAsync(
        UpdateOrganizationMembershipRequest request,
        CancellationToken ct
    );

    Task RemoveMembershipAsync(Guid membershipId, CancellationToken ct);
}

public static class OrganizationRegistryCatalog
{
    public static IReadOnlyList<string> OrganizationTypes { get; } =
    [
        "gang",
        "set",
        "clique",
        "subgroup",
        "crew",
        "hybrid / informal group"
    ];

    public static IReadOnlyList<string> OrganizationStatuses { get; } =
    [
        "active",
        "inactive"
    ];

    public static IReadOnlyList<string> MembershipStatuses { get; } =
    [
        "member",
        "associate",
        "former",
        "suspected",
        "affiliate"
    ];
}

public sealed record OrganizationSummaryDto(
    Guid OrganizationId,
    string Name,
    string Type,
    string Status,
    string? Summary,
    Guid? ParentOrganizationId,
    string? ParentOrganizationName,
    int AliasCount,
    int MembershipCount,
    int ChildCount,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc
);

public sealed record OrganizationDetailsDto(
    OrganizationSummaryDto Organization,
    IReadOnlyList<OrganizationAliasDto> Aliases,
    IReadOnlyList<OrganizationMembershipDto> Memberships,
    IReadOnlyList<OrganizationSummaryDto> Children
);

public sealed record OrganizationAliasDto(
    Guid AliasId,
    Guid OrganizationId,
    string Alias,
    string? Notes,
    DateTimeOffset CreatedAtUtc
);

public sealed record OrganizationMembershipDto(
    Guid MembershipId,
    Guid OrganizationId,
    Guid GlobalEntityId,
    string GlobalDisplayName,
    string? Role,
    string Status,
    int Confidence,
    string? BasisSummary,
    DateTimeOffset? StartDateUtc,
    DateTimeOffset? EndDateUtc,
    DateTimeOffset? LastConfirmedDateUtc,
    string? Reviewer,
    string? ReviewNotes,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc
);

public sealed record CreateOrganizationRequest(
    string Name,
    string Type,
    string Status,
    Guid? ParentOrganizationId,
    string? Summary
);

public sealed record UpdateOrganizationRequest(
    Guid OrganizationId,
    string Name,
    string Type,
    string Status,
    Guid? ParentOrganizationId,
    string? Summary
);

public sealed record AddOrganizationAliasRequest(
    Guid OrganizationId,
    string Alias,
    string? Notes
);

public sealed record AddOrganizationMembershipRequest(
    Guid OrganizationId,
    Guid GlobalEntityId,
    string? Role,
    string Status,
    int Confidence,
    string? BasisSummary,
    DateTimeOffset? StartDateUtc,
    DateTimeOffset? EndDateUtc,
    DateTimeOffset? LastConfirmedDateUtc,
    string? Reviewer,
    string? ReviewNotes
);

public sealed record UpdateOrganizationMembershipRequest(
    Guid MembershipId,
    string? Role,
    string Status,
    int Confidence,
    string? BasisSummary,
    DateTimeOffset? StartDateUtc,
    DateTimeOffset? EndDateUtc,
    DateTimeOffset? LastConfirmedDateUtc,
    string? Reviewer,
    string? ReviewNotes
);
