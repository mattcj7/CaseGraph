using CaseGraph.App.ViewModels;
using CaseGraph.Core.Abstractions;
using CaseGraph.Core.Models;
using CaseGraph.Infrastructure.GangDocumentation;
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
            ),
            new GangDocumentationViewModel(
                new FakeGangDocumentationService(
                    [
                        new GangDocumentationRecord(
                            Guid.NewGuid(),
                            Guid.NewGuid(),
                            targetId,
                            Guid.Parse("11111111-1111-1111-1111-111111111111"),
                            organizationId,
                            "Rollin 60s",
                            null,
                            null,
                            "member",
                            "approved",
                            "approved",
                            "Detective Hale",
                            new DateTimeOffset(2026, 3, 20, 0, 0, 0, TimeSpan.Zero),
                            "Validated during review meeting.",
                            "Formal record notes",
                            new DateTimeOffset(2026, 3, 1, 0, 0, 0, TimeSpan.Zero),
                            new DateTimeOffset(2026, 3, 5, 0, 0, 0, TimeSpan.Zero),
                            [
                                new GangDocumentationCriterion(
                                    Guid.NewGuid(),
                                    Guid.NewGuid(),
                                    "self-admission",
                                    true,
                                    "Admitted during interview.",
                                    new DateTimeOffset(2026, 2, 28, 0, 0, 0, TimeSpan.Zero),
                                    "FI card",
                                    1,
                                    new DateTimeOffset(2026, 3, 1, 0, 0, 0, TimeSpan.Zero),
                                    new DateTimeOffset(2026, 3, 1, 0, 0, 0, TimeSpan.Zero)
                                )
                            ],
                            [
                                new GangDocumentationStatusHistoryEntry(
                                    Guid.NewGuid(),
                                    Guid.NewGuid(),
                                    "Created",
                                    "Created in approved state.",
                                    "Detective Hale",
                                    new DateTimeOffset(2026, 3, 1, 0, 0, 0, TimeSpan.Zero)
                                )
                            ]
                        )
                    ]
                ),
                new FakeOrganizationService(null)
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
        Assert.True(viewModel.GangDocumentation.HasRecords);
        Assert.Single(viewModel.GangDocumentation.Records);
    }

    [Fact]
    public async Task PersonProfile_WhenRecordMissing_ShowsEmptyState()
    {
        var viewModel = new PersonProfileViewModel(
            new FakeTargetRegistryService(null),
            new GangDocumentationViewModel(new FakeGangDocumentationService([]), new FakeOrganizationService(null))
        );

        await viewModel.LoadAsync(Guid.NewGuid(), Guid.NewGuid(), CancellationToken.None);

        Assert.False(viewModel.IsRecordAvailable);
        Assert.Equal("No organization memberships recorded", viewModel.AffiliationsEmptyState);
        Assert.Equal("No linked global person recorded", viewModel.GlobalPersonEmptyState);
    }

    [Fact]
    public async Task GangDocumentationViewModel_LoadAsync_WithNoRecords_PreparesCreateWorkflow()
    {
        var organizationId = Guid.NewGuid();
        var viewModel = new GangDocumentationViewModel(
            new FakeGangDocumentationService([]),
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
                        0,
                        0,
                        DateTimeOffset.UtcNow,
                        DateTimeOffset.UtcNow
                    ),
                    [],
                    [],
                    []
                )
            )
        );

        await viewModel.LoadAsync(Guid.NewGuid(), Guid.NewGuid(), CancellationToken.None);

        Assert.False(viewModel.HasRecords);
        Assert.True(viewModel.HasOrganizationOptions);
        Assert.True(viewModel.CanEditDocumentation);
        Assert.False(viewModel.CanManageCriteria);
        Assert.Equal(organizationId, viewModel.SelectedOrganizationId);
        Assert.Equal("Create Documentation", viewModel.SaveRecordButtonText);
    }

    [Fact]
    public async Task GangDocumentationViewModel_SaveRecordAndCriterion_ReloadsWorkflowState()
    {
        var caseId = Guid.NewGuid();
        var targetId = Guid.NewGuid();
        var organizationId = Guid.NewGuid();
        var subgroupId = Guid.NewGuid();
        var organizationService = new FakeOrganizationService(
            new OrganizationDetailsDto(
                new OrganizationSummaryDto(
                    organizationId,
                    "Neighborhood Crips",
                    "gang",
                    "active",
                    null,
                    null,
                    null,
                    0,
                    0,
                    0,
                    DateTimeOffset.UtcNow,
                    DateTimeOffset.UtcNow
                ),
                [],
                [],
                [
                    new OrganizationSummaryDto(
                        subgroupId,
                        "Neighborhood Crips East",
                        "set",
                        "active",
                        null,
                        organizationId,
                        "Neighborhood Crips",
                        0,
                        0,
                        0,
                        DateTimeOffset.UtcNow,
                        DateTimeOffset.UtcNow
                    )
                ]
            )
        );
        var documentationService = new MutableFakeGangDocumentationService(
            caseId,
            targetId,
            organizationId,
            subgroupId
        );
        var viewModel = new GangDocumentationViewModel(documentationService, organizationService);

        await viewModel.LoadAsync(caseId, targetId, CancellationToken.None);
        viewModel.SelectedSubgroupOrganizationId = subgroupId;
        viewModel.Summary = "Formal documentation summary";
        viewModel.Notes = "Formal documentation notes";
        viewModel.Reviewer = "Detective Hale";
        viewModel.DocumentationStatus = "draft";
        viewModel.ApprovalStatus = "pending approval";
        await viewModel.SaveRecordCommand.ExecuteAsync(null);

        Assert.True(viewModel.HasRecords);
        Assert.True(viewModel.HasSelectedDocumentation);
        Assert.True(viewModel.CanManageCriteria);
        Assert.Equal("Gang documentation saved.", viewModel.StatusMessage);

        viewModel.CriterionType = "self-admission";
        viewModel.CriterionBasisSummary = "Admitted during interview.";
        viewModel.CriterionSourceNote = "FI card";
        await viewModel.SaveCriterionCommand.ExecuteAsync(null);

        Assert.True(viewModel.HasCriteria);
        var criterion = Assert.Single(viewModel.Criteria);
        Assert.Equal("self-admission", criterion.CriterionType);
        Assert.Equal("Admitted during interview.", criterion.BasisSummary);
        Assert.Equal("Criterion saved.", viewModel.StatusMessage);

        await viewModel.LoadAsync(caseId, targetId, CancellationToken.None);

        Assert.True(viewModel.HasRecords);
        Assert.True(viewModel.HasCriteria);
        Assert.Equal("Formal documentation summary", viewModel.SelectedRecord!.Summary);
        Assert.Equal("Neighborhood Crips", viewModel.SelectedRecord.OrganizationName);
        Assert.Equal("Neighborhood Crips East", viewModel.SelectedRecord.SubgroupOrganizationName);
        Assert.Equal("Neighborhood Crips", viewModel.CurrentOrganizationDisplay);
        Assert.Equal("Neighborhood Crips East", viewModel.CurrentSubgroupDisplay);
        Assert.Equal("Admitted during interview.", Assert.Single(viewModel.Criteria).BasisSummary);
    }

    [Fact]
    public async Task GangDocumentationViewModel_BlankRequiredFields_ShowValidationStateAfterSubmit()
    {
        var viewModel = new GangDocumentationViewModel(
            new FakeGangDocumentationService([]),
            new FakeOrganizationService(null)
        );

        await viewModel.LoadAsync(Guid.NewGuid(), Guid.NewGuid(), CancellationToken.None);
        viewModel.Summary = string.Empty;

        await viewModel.SaveRecordCommand.ExecuteAsync(null);

        Assert.True(viewModel.ShowOrganizationRequiredIndicator);
        Assert.True(viewModel.ShowSummaryRequiredIndicator);
        Assert.True(viewModel.HasRecordValidationIssues);
        Assert.Equal(
            "Complete the required gang documentation fields marked with *.",
            viewModel.RecordValidationMessage
        );
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
                    [new OrganizationMembershipDto(Guid.NewGuid(), organizationId, Guid.NewGuid(), "Marcus Lane", "member", "member", 80, null, null, null, null, null, null, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, true, "Documented", "Rollin 60s")],
                    [new OrganizationSummaryDto(childId, "Rollin 60s West", "set", "active", null, organizationId, "Rollin 60s", 0, 0, 0, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow)]
                )
            )
        );

        await viewModel.LoadAsync(organizationId, CancellationToken.None);

        Assert.True(viewModel.IsRecordAvailable);
        Assert.Equal("Rollin 60s", viewModel.HeaderTitle);
        Assert.Single(viewModel.Aliases);
        Assert.Single(viewModel.Memberships);
        Assert.Equal("Documented", viewModel.Memberships[0].DocumentationStatus);
        Assert.Equal("Open Documentation", viewModel.Memberships[0].ActionLabel);
        Assert.Single(viewModel.Children);
    }

    [Fact]
    public async Task OrganizationProfile_LoadAsync_MembershipWithoutDocumentation_ShowsNoDocumentation()
    {
        var organizationId = Guid.NewGuid();
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
                        0,
                        DateTimeOffset.UtcNow,
                        DateTimeOffset.UtcNow
                    ),
                    [],
                    [
                        new OrganizationMembershipDto(
                            Guid.NewGuid(),
                            organizationId,
                            Guid.NewGuid(),
                            "Marcus Lane",
                            "associate",
                            "associate",
                            70,
                            null,
                            null,
                            null,
                            null,
                            null,
                            null,
                            DateTimeOffset.UtcNow,
                            DateTimeOffset.UtcNow
                        )
                    ],
                    []
                )
            )
        );

        await viewModel.LoadAsync(organizationId, CancellationToken.None);

        var membership = Assert.Single(viewModel.Memberships);
        Assert.Equal("No Documentation", membership.DocumentationStatus);
        Assert.Equal("Create Documentation", membership.ActionLabel);
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
            => Task.FromResult<IReadOnlyList<OrganizationSummaryDto>>(
                _details is null
                    ? []
                    : [_details.Organization, .. _details.Children]
            );

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

    private sealed class FakeGangDocumentationService : IGangDocumentationService
    {
        private readonly IReadOnlyList<GangDocumentationRecord> _records;

        public FakeGangDocumentationService(IReadOnlyList<GangDocumentationRecord> records)
        {
            _records = records;
        }

        public Task<IReadOnlyList<GangDocumentationRecord>> GetDocumentationForTargetAsync(
            Guid caseId,
            Guid targetId,
            CancellationToken ct
        ) => Task.FromResult(_records);

        public Task<GangDocumentationRecord> CreateDocumentationAsync(
            CreateGangDocumentationRequest request,
            CancellationToken ct
        ) => throw new NotSupportedException();

        public Task<GangDocumentationRecord> UpdateDocumentationAsync(
            UpdateGangDocumentationRequest request,
            CancellationToken ct
        ) => throw new NotSupportedException();

        public Task<GangDocumentationCriterion> SaveCriterionAsync(
            SaveGangDocumentationCriterionRequest request,
            CancellationToken ct
        ) => throw new NotSupportedException();

        public Task RemoveCriterionAsync(
            Guid caseId,
            Guid documentationId,
            Guid criterionId,
            CancellationToken ct
        ) => throw new NotSupportedException();
    }

    private sealed class MutableFakeGangDocumentationService : IGangDocumentationService
    {
        private readonly Guid _caseId;
        private readonly Guid _targetId;
        private readonly Guid _organizationId;
        private readonly Guid? _subgroupId;
        private GangDocumentationRecord? _record;

        public MutableFakeGangDocumentationService(
            Guid caseId,
            Guid targetId,
            Guid organizationId,
            Guid? subgroupId
        )
        {
            _caseId = caseId;
            _targetId = targetId;
            _organizationId = organizationId;
            _subgroupId = subgroupId;
        }

        public Task<IReadOnlyList<GangDocumentationRecord>> GetDocumentationForTargetAsync(
            Guid caseId,
            Guid targetId,
            CancellationToken ct
        )
        {
            if (_record is null || caseId != _caseId || targetId != _targetId)
            {
                return Task.FromResult<IReadOnlyList<GangDocumentationRecord>>([]);
            }

            return Task.FromResult<IReadOnlyList<GangDocumentationRecord>>([_record]);
        }

        public Task<GangDocumentationRecord> CreateDocumentationAsync(
            CreateGangDocumentationRequest request,
            CancellationToken ct
        )
        {
            _record = new GangDocumentationRecord(
                Guid.NewGuid(),
                request.CaseId,
                request.TargetId,
                null,
                request.OrganizationId,
                "Neighborhood Crips",
                request.SubgroupOrganizationId,
                request.SubgroupOrganizationId == _subgroupId ? "Neighborhood Crips East" : null,
                request.AffiliationRole,
                request.DocumentationStatus,
                request.ApprovalStatus,
                request.Reviewer,
                request.ReviewDueDateUtc,
                request.Summary,
                request.Notes,
                DateTimeOffset.UtcNow,
                DateTimeOffset.UtcNow,
                [],
                []
            );
            return Task.FromResult(_record);
        }

        public Task<GangDocumentationRecord> UpdateDocumentationAsync(
            UpdateGangDocumentationRequest request,
            CancellationToken ct
        )
        {
            _record = new GangDocumentationRecord(
                request.DocumentationId,
                request.CaseId,
                _targetId,
                null,
                request.OrganizationId,
                "Neighborhood Crips",
                request.SubgroupOrganizationId,
                request.SubgroupOrganizationId == _subgroupId ? "Neighborhood Crips East" : null,
                request.AffiliationRole,
                request.DocumentationStatus,
                request.ApprovalStatus,
                request.Reviewer,
                request.ReviewDueDateUtc,
                request.Summary,
                request.Notes,
                _record?.CreatedAtUtc ?? DateTimeOffset.UtcNow,
                DateTimeOffset.UtcNow,
                _record?.Criteria ?? [],
                _record?.StatusHistory ?? []
            );
            return Task.FromResult(_record);
        }

        public Task<GangDocumentationCriterion> SaveCriterionAsync(
            SaveGangDocumentationCriterionRequest request,
            CancellationToken ct
        )
        {
            var criterion = new GangDocumentationCriterion(
                request.CriterionId ?? Guid.NewGuid(),
                request.DocumentationId,
                request.CriterionType,
                request.IsMet,
                request.BasisSummary,
                request.ObservedDateUtc,
                request.SourceNote,
                1,
                DateTimeOffset.UtcNow,
                DateTimeOffset.UtcNow
            );

            _record = new GangDocumentationRecord(
                _record?.DocumentationId ?? Guid.NewGuid(),
                request.CaseId,
                _targetId,
                null,
                _organizationId,
                "Neighborhood Crips",
                _record?.SubgroupOrganizationId,
                _record?.SubgroupOrganizationName,
                _record?.AffiliationRole ?? "member",
                _record?.DocumentationStatus ?? "draft",
                _record?.ApprovalStatus ?? "pending approval",
                _record?.Reviewer,
                _record?.ReviewDueDateUtc,
                _record?.Summary ?? string.Empty,
                _record?.Notes,
                _record?.CreatedAtUtc ?? DateTimeOffset.UtcNow,
                DateTimeOffset.UtcNow,
                [criterion],
                _record?.StatusHistory ?? []
            );

            return Task.FromResult(criterion);
        }

        public Task RemoveCriterionAsync(
            Guid caseId,
            Guid documentationId,
            Guid criterionId,
            CancellationToken ct
        )
        {
            if (_record is not null)
            {
                _record = _record with { Criteria = [] };
            }

            return Task.CompletedTask;
        }
    }
}
