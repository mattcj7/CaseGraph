using CaseGraph.App.ViewModels;
using CaseGraph.Core.Abstractions;
using CaseGraph.Core.Models;
using CaseGraph.Infrastructure.Organizations;

namespace CaseGraph.Infrastructure.Tests;

public sealed class EntityProfileViewModelTests
{
    [Fact]
    public async Task PersonProfile_LoadAsync_PopulatesSections()
    {
        var targetId = Guid.NewGuid();
        var organizationId = Guid.NewGuid();
        var viewModel = new PersonProfileViewModel(
            new FakeTargetRegistryService(
                new TargetDetails(
                    new TargetSummary(
                        targetId,
                        Guid.NewGuid(),
                        "Marcus Lane",
                        "Lil Marc",
                        "Rollin 60s",
                        "Primary notes",
                        new DateTimeOffset(2026, 3, 1, 0, 0, 0, TimeSpan.Zero),
                        new DateTimeOffset(2026, 3, 5, 0, 0, 0, TimeSpan.Zero),
                        Guid.Parse("11111111-1111-1111-1111-111111111111"),
                        "Marcus Lane"
                    ),
                    [new TargetAliasInfo(Guid.NewGuid(), targetId, Guid.NewGuid(), "Lil Marc", "lil marc")],
                    [
                        new TargetOrganizationAffiliationInfo(
                            Guid.NewGuid(),
                            organizationId,
                            "Rollin 60s",
                            "gang",
                            "active",
                            null,
                            "member",
                            "member",
                            90,
                            null,
                            null,
                            new DateTimeOffset(2026, 2, 20, 0, 0, 0, TimeSpan.Zero)
                        )
                    ],
                    [],
                    0,
                    new TargetGlobalPersonInfo(
                        Guid.Parse("11111111-1111-1111-1111-111111111111"),
                        "Marcus Lane",
                        [new GlobalPersonAliasInfo(Guid.NewGuid(), Guid.NewGuid(), "Marc", "marc", null)],
                        [new GlobalPersonIdentifierInfo(Guid.NewGuid(), Guid.NewGuid(), TargetIdentifierType.Phone, "(555) 111-2222", "+15551112222", true, null, DateTimeOffset.UtcNow)],
                        [new GlobalPersonCaseReference(Guid.NewGuid(), "Case B", Guid.NewGuid(), "Marcus Lane B")]
                    )
                )
            )
        );

        await viewModel.LoadAsync(Guid.NewGuid(), targetId, CancellationToken.None);

        Assert.True(viewModel.IsRecordAvailable);
        Assert.Equal("Marcus Lane", viewModel.HeaderTitle);
        Assert.Equal("Lil Marc", Assert.Single(viewModel.Aliases));
        Assert.Equal("Rollin 60s", Assert.Single(viewModel.Affiliations).OrganizationName);
        Assert.True(viewModel.HasLinkedGlobalPerson);
        Assert.Single(viewModel.GlobalIdentifiers);
        Assert.Single(viewModel.GlobalOtherCases);
    }

    [Fact]
    public async Task PersonProfile_WhenRecordMissing_ShowsEmptyState()
    {
        var viewModel = new PersonProfileViewModel(new FakeTargetRegistryService(null));

        await viewModel.LoadAsync(Guid.NewGuid(), Guid.NewGuid(), CancellationToken.None);

        Assert.False(viewModel.IsRecordAvailable);
        Assert.Equal("No organization memberships recorded", viewModel.AffiliationsEmptyState);
        Assert.Equal("No linked global person recorded", viewModel.GlobalPersonEmptyState);
    }

    [Fact]
    public async Task OrganizationProfile_LoadAsync_PopulatesMemberships_AndChildOrganizations()
    {
        var organizationId = Guid.NewGuid();
        var childId = Guid.NewGuid();
        var viewModel = new OrganizationProfileViewModel(
            new FakeOrganizationService(
                new OrganizationDetailsDto(
                    new OrganizationSummaryDto(
                        organizationId,
                        "Rollin 60s",
                        "gang",
                        "active",
                        "Main summary",
                        null,
                        null,
                        1,
                        1,
                        1,
                        new DateTimeOffset(2026, 3, 1, 0, 0, 0, TimeSpan.Zero),
                        new DateTimeOffset(2026, 3, 3, 0, 0, 0, TimeSpan.Zero)
                    ),
                    [new OrganizationAliasDto(Guid.NewGuid(), organizationId, "R60", null, DateTimeOffset.UtcNow)],
                    [new OrganizationMembershipDto(Guid.NewGuid(), organizationId, Guid.NewGuid(), "Marcus Lane", "member", "member", 80, null, null, null, null, null, null, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow)],
                    [new OrganizationSummaryDto(childId, "Rollin 60s West", "set", "active", null, organizationId, "Rollin 60s", 0, 0, 0, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow)]
                )
            )
        );

        await viewModel.LoadAsync(organizationId, CancellationToken.None);

        Assert.True(viewModel.IsRecordAvailable);
        Assert.Equal("Rollin 60s", viewModel.HeaderTitle);
        Assert.Single(viewModel.Aliases);
        Assert.Single(viewModel.Memberships);
        Assert.Single(viewModel.Children);
    }

    [Fact]
    public async Task OrganizationProfile_OpenCommands_InvokeCallbacks()
    {
        var organizationId = Guid.NewGuid();
        var globalEntityId = Guid.NewGuid();
        var viewModel = new OrganizationProfileViewModel(
            new FakeOrganizationService(
                new OrganizationDetailsDto(
                    new OrganizationSummaryDto(
                        organizationId,
                        "Rollin 60s",
                        "gang",
                        "active",
                        null,
                        null,
                        null,
                        0,
                        1,
                        1,
                        DateTimeOffset.UtcNow,
                        DateTimeOffset.UtcNow
                    ),
                    [],
                    [new OrganizationMembershipDto(Guid.NewGuid(), organizationId, globalEntityId, "Marcus Lane", null, "member", 70, null, null, null, null, null, null, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow)],
                    [new OrganizationSummaryDto(Guid.NewGuid(), "Rollin 60s West", "set", "active", null, organizationId, "Rollin 60s", 0, 0, 0, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow)]
                )
            )
        );

        Guid? openedOrganizationId = null;
        Guid? openedGlobalEntityId = null;
        viewModel.OpenOrganizationProfileRequested = id => openedOrganizationId = id;
        viewModel.OpenPersonProfileRequested = (id, _) => openedGlobalEntityId = id;

        await viewModel.LoadAsync(organizationId, CancellationToken.None);
        await viewModel.OpenMembershipPersonCommand.ExecuteAsync(viewModel.Memberships[0]);
        await viewModel.OpenChildOrganizationCommand.ExecuteAsync(viewModel.Children[0]);

        Assert.Equal(globalEntityId, openedGlobalEntityId);
        Assert.Equal(viewModel.Children[0].OrganizationId, openedOrganizationId);
    }

    private sealed class FakeTargetRegistryService : ITargetRegistryService
    {
        private readonly TargetDetails? _details;

        public FakeTargetRegistryService(TargetDetails? details)
        {
            _details = details;
        }

        public Task<IReadOnlyList<TargetSummary>> GetTargetsAsync(Guid caseId, string? search, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<TargetSummary>>([]);

        public Task<TargetDetails?> GetTargetDetailsAsync(Guid caseId, Guid targetId, CancellationToken ct)
            => Task.FromResult(_details);

        public Task<IReadOnlyList<GlobalPersonSummary>> SearchGlobalPersonsAsync(string? search, int take, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<GlobalPersonSummary>>([]);

        public Task<TargetSummary> CreateTargetAsync(CreateTargetRequest request, CancellationToken ct)
            => throw new NotSupportedException();

        public Task<TargetSummary> UpdateTargetAsync(UpdateTargetRequest request, CancellationToken ct)
            => throw new NotSupportedException();

        public Task<TargetGlobalPersonInfo> CreateAndLinkGlobalPersonAsync(CreateGlobalPersonForTargetRequest request, CancellationToken ct)
            => throw new NotSupportedException();

        public Task<TargetGlobalPersonInfo> LinkTargetToGlobalPersonAsync(LinkTargetToGlobalPersonRequest request, CancellationToken ct)
            => throw new NotSupportedException();

        public Task UnlinkTargetFromGlobalPersonAsync(Guid caseId, Guid targetId, CancellationToken ct)
            => throw new NotSupportedException();

        public Task<TargetAliasInfo> AddAliasAsync(AddTargetAliasRequest request, CancellationToken ct)
            => throw new NotSupportedException();

        public Task RemoveAliasAsync(Guid caseId, Guid aliasId, CancellationToken ct)
            => throw new NotSupportedException();

        public Task<TargetIdentifierMutationResult> AddIdentifierAsync(AddTargetIdentifierRequest request, CancellationToken ct)
            => throw new NotSupportedException();

        public Task<TargetIdentifierMutationResult> UpdateIdentifierAsync(UpdateTargetIdentifierRequest request, CancellationToken ct)
            => throw new NotSupportedException();

        public Task RemoveIdentifierAsync(RemoveTargetIdentifierRequest request, CancellationToken ct)
            => throw new NotSupportedException();

        public Task<MessageParticipantLinkResult> LinkMessageParticipantAsync(LinkMessageParticipantRequest request, CancellationToken ct)
            => throw new NotSupportedException();
    }

    private sealed class FakeOrganizationService : IOrganizationService
    {
        private readonly OrganizationDetailsDto? _details;

        public FakeOrganizationService(OrganizationDetailsDto? details)
        {
            _details = details;
        }

        public Task<IReadOnlyList<OrganizationSummaryDto>> GetOrganizationsAsync(string? search, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<OrganizationSummaryDto>>([]);

        public Task<OrganizationDetailsDto?> GetOrganizationDetailsAsync(Guid organizationId, CancellationToken ct)
            => Task.FromResult(_details);

        public Task<OrganizationSummaryDto> CreateOrganizationAsync(CreateOrganizationRequest request, CancellationToken ct)
            => throw new NotSupportedException();

        public Task<OrganizationSummaryDto> UpdateOrganizationAsync(UpdateOrganizationRequest request, CancellationToken ct)
            => throw new NotSupportedException();

        public Task<OrganizationAliasDto> AddAliasAsync(AddOrganizationAliasRequest request, CancellationToken ct)
            => throw new NotSupportedException();

        public Task RemoveAliasAsync(Guid aliasId, CancellationToken ct)
            => throw new NotSupportedException();

        public Task<OrganizationMembershipDto> AddMembershipAsync(AddOrganizationMembershipRequest request, CancellationToken ct)
            => throw new NotSupportedException();

        public Task<OrganizationMembershipDto> UpdateMembershipAsync(UpdateOrganizationMembershipRequest request, CancellationToken ct)
            => throw new NotSupportedException();

        public Task RemoveMembershipAsync(Guid membershipId, CancellationToken ct)
            => throw new NotSupportedException();
    }
}
