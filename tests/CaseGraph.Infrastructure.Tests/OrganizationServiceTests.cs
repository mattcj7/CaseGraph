using CaseGraph.Core.Abstractions;
using CaseGraph.Core.Models;
using CaseGraph.Infrastructure.Organizations;
using CaseGraph.Infrastructure.Persistence;
using CaseGraph.Infrastructure.Persistence.Entities;
using CaseGraph.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace CaseGraph.Infrastructure.Tests;

public sealed class OrganizationServiceTests
{
    [Fact]
    public async Task CreateOrganizationAsync_PersistsOrganizationRecord()
    {
        await using var fixture = await WorkspaceFixture.CreateAsync();
        var service = fixture.Services.GetRequiredService<IOrganizationService>();

        var created = await service.CreateOrganizationAsync(
            new CreateOrganizationRequest("Rollin 60s", "gang", "active", null, "Primary set"),
            CancellationToken.None
        );

        await using var db = await fixture.CreateDbContextAsync();
        var record = await db.Organizations.AsNoTracking().FirstOrDefaultAsync(
            item => item.OrganizationId == created.OrganizationId
        );

        Assert.NotNull(record);
        Assert.Equal("Rollin 60s", record!.Name);
        Assert.Equal("gang", record.Type);
        Assert.Equal("active", record.Status);
        Assert.Equal("Primary set", record.Summary);
    }

    [Fact]
    public async Task AddAliasAsync_PersistsOrganizationAlias()
    {
        await using var fixture = await WorkspaceFixture.CreateAsync();
        var service = fixture.Services.GetRequiredService<IOrganizationService>();
        var created = await service.CreateOrganizationAsync(
            new CreateOrganizationRequest("Neighborhood Crips", "gang", "active", null, null),
            CancellationToken.None
        );

        var alias = await service.AddAliasAsync(
            new AddOrganizationAliasRequest(created.OrganizationId, "NHC", null),
            CancellationToken.None
        );

        var details = await service.GetOrganizationDetailsAsync(created.OrganizationId, CancellationToken.None);
        Assert.NotNull(details);
        Assert.Contains(details!.Aliases, item => item.AliasId == alias.AliasId && item.Alias == "NHC");

        await using var db = await fixture.CreateDbContextAsync();
        var aliasRecord = await db.OrganizationAliases.AsNoTracking().FirstOrDefaultAsync(
            item => item.AliasId == alias.AliasId
        );
        Assert.NotNull(aliasRecord);
        Assert.Equal("nhc", aliasRecord!.AliasNormalized);
    }

    [Fact]
    public async Task AddMembershipAsync_PersistsOrganizationMembershipFields()
    {
        await using var fixture = await WorkspaceFixture.CreateAsync();
        var service = fixture.Services.GetRequiredService<IOrganizationService>();
        var organization = await service.CreateOrganizationAsync(
            new CreateOrganizationRequest("Eight Trey", "gang", "active", null, null),
            CancellationToken.None
        );
        var globalPerson = await fixture.CreateGlobalPersonAsync("DeAndre Stone", "Membership Case");

        var membership = await service.AddMembershipAsync(
            new AddOrganizationMembershipRequest(
                organization.OrganizationId,
                globalPerson.GlobalEntityId,
                "shot caller",
                "member",
                88,
                "Field interview + tattoos",
                new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero),
                new DateTimeOffset(2025, 12, 31, 0, 0, 0, TimeSpan.Zero),
                new DateTimeOffset(2025, 9, 1, 0, 0, 0, TimeSpan.Zero),
                "Detective Hale",
                "Reviewed during gang intel meeting"
            ),
            CancellationToken.None
        );

        var details = await service.GetOrganizationDetailsAsync(organization.OrganizationId, CancellationToken.None);
        var loaded = Assert.Single(details!.Memberships);
        Assert.Equal(membership.MembershipId, loaded.MembershipId);
        Assert.Equal(globalPerson.GlobalEntityId, loaded.GlobalEntityId);
        Assert.Equal("DeAndre Stone", loaded.GlobalDisplayName);
        Assert.Equal("shot caller", loaded.Role);
        Assert.Equal("member", loaded.Status);
        Assert.Equal(88, loaded.Confidence);
        Assert.Equal("Field interview + tattoos", loaded.BasisSummary);
        Assert.Equal("Detective Hale", loaded.Reviewer);
        Assert.Equal("Reviewed during gang intel meeting", loaded.ReviewNotes);
        Assert.Equal(new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero), loaded.StartDateUtc);
        Assert.Equal(new DateTimeOffset(2025, 12, 31, 0, 0, 0, TimeSpan.Zero), loaded.EndDateUtc);
        Assert.Equal(new DateTimeOffset(2025, 9, 1, 0, 0, 0, TimeSpan.Zero), loaded.LastConfirmedDateUtc);
    }

    [Fact]
    public async Task ParentChildOrganizations_PersistAndBrowseCorrectly()
    {
        await using var fixture = await WorkspaceFixture.CreateAsync();
        var service = fixture.Services.GetRequiredService<IOrganizationService>();

        var parent = await service.CreateOrganizationAsync(
            new CreateOrganizationRequest("Grape Street", "gang", "active", null, null),
            CancellationToken.None
        );
        var child = await service.CreateOrganizationAsync(
            new CreateOrganizationRequest("Grape Street East", "set", "active", parent.OrganizationId, "East side set"),
            CancellationToken.None
        );

        var organizations = await service.GetOrganizationsAsync(search: null, CancellationToken.None);
        var parentSummary = Assert.Single(organizations.Where(item => item.OrganizationId == parent.OrganizationId));
        var childSummary = Assert.Single(organizations.Where(item => item.OrganizationId == child.OrganizationId));
        Assert.Equal(1, parentSummary.ChildCount);
        Assert.Equal(parent.OrganizationId, childSummary.ParentOrganizationId);
        Assert.Equal("Grape Street", childSummary.ParentOrganizationName);

        var details = await service.GetOrganizationDetailsAsync(parent.OrganizationId, CancellationToken.None);
        Assert.NotNull(details);
        var childDetail = Assert.Single(details!.Children);
        Assert.Equal(child.OrganizationId, childDetail.OrganizationId);
        Assert.Equal("Grape Street East", childDetail.Name);
    }

    [Fact]
    public async Task Memberships_ReuseGlobalPeopleAcrossCases()
    {
        await using var fixture = await WorkspaceFixture.CreateAsync();
        var service = fixture.Services.GetRequiredService<IOrganizationService>();
        var targetRegistry = fixture.Services.GetRequiredService<ITargetRegistryService>();

        var caseA = await fixture.CreateCaseAsync("Org Reuse A");
        var caseB = await fixture.CreateCaseAsync("Org Reuse B");
        var targetA = await targetRegistry.CreateTargetAsync(
            new CreateTargetRequest(caseA.CaseId, "Marcus Lane", null, null, CreateGlobalPerson: true),
            CancellationToken.None
        );
        var detailsA = await targetRegistry.GetTargetDetailsAsync(caseA.CaseId, targetA.TargetId, CancellationToken.None);
        Assert.NotNull(detailsA);
        Assert.NotNull(detailsA!.GlobalPerson);
        var globalEntityId = detailsA.GlobalPerson!.GlobalEntityId;

        var targetB = await targetRegistry.CreateTargetAsync(
            new CreateTargetRequest(caseB.CaseId, "Marcus Lane B", null, null, GlobalEntityId: globalEntityId),
            CancellationToken.None
        );
        var detailsB = await targetRegistry.GetTargetDetailsAsync(caseB.CaseId, targetB.TargetId, CancellationToken.None);
        Assert.NotNull(detailsB);
        Assert.Equal(globalEntityId, detailsB!.GlobalPerson!.GlobalEntityId);

        var organization = await service.CreateOrganizationAsync(
            new CreateOrganizationRequest("Hoover Criminals", "gang", "active", null, null),
            CancellationToken.None
        );
        await service.AddMembershipAsync(
            new AddOrganizationMembershipRequest(
                organization.OrganizationId,
                globalEntityId,
                "member",
                "member",
                75,
                "Cross-case linkage",
                null,
                null,
                null,
                null,
                null
            ),
            CancellationToken.None
        );

        var organizationDetails = await service.GetOrganizationDetailsAsync(
            organization.OrganizationId,
            CancellationToken.None
        );
        var membership = Assert.Single(organizationDetails!.Memberships);
        Assert.Equal(globalEntityId, membership.GlobalEntityId);
        Assert.Equal("Marcus Lane", membership.GlobalDisplayName);
    }

    [Fact]
    public async Task GetOrganizationDetailsAsync_MembershipsIncludeGangDocumentationStatus()
    {
        await using var fixture = await WorkspaceFixture.CreateAsync();
        var service = fixture.Services.GetRequiredService<IOrganizationService>();
        var organization = await service.CreateOrganizationAsync(
            new CreateOrganizationRequest("Bounty Hunters", "gang", "active", null, null),
            CancellationToken.None
        );
        var subgroup = await service.CreateOrganizationAsync(
            new CreateOrganizationRequest("Bounty Hunters South", "set", "active", organization.OrganizationId, null),
            CancellationToken.None
        );
        var globalPerson = await fixture.CreateGlobalPersonAsync("Rico Lane", "Roster Documentation Case");

        await service.AddMembershipAsync(
            new AddOrganizationMembershipRequest(
                organization.OrganizationId,
                globalPerson.GlobalEntityId,
                "member",
                "member",
                85,
                null,
                null,
                null,
                null,
                null,
                null
            ),
            CancellationToken.None
        );

        await using (var db = await fixture.CreateDbContextAsync())
        {
            var target = await db.Targets
                .AsNoTracking()
                .FirstAsync(item => item.GlobalEntityId == globalPerson.GlobalEntityId);
            db.GangDocumentationRecords.Add(new GangDocumentationRecordEntity
            {
                DocumentationId = Guid.NewGuid(),
                CaseId = target.CaseId,
                TargetId = target.TargetId,
                GlobalEntityId = globalPerson.GlobalEntityId,
                OrganizationId = organization.OrganizationId,
                SubgroupOrganizationId = subgroup.OrganizationId,
                AffiliationRole = "member",
                DocumentationStatus = "pending review",
                ApprovalStatus = "pending approval",
                Summary = "Roster documentation",
                CreatedAtUtc = DateTimeOffset.UtcNow,
                UpdatedAtUtc = new DateTimeOffset(2026, 3, 15, 12, 0, 0, TimeSpan.Zero)
            });
            await db.SaveChangesAsync();
        }

        var details = await service.GetOrganizationDetailsAsync(organization.OrganizationId, CancellationToken.None);

        var membership = Assert.Single(details!.Memberships);
        Assert.True(membership.HasGangDocumentation);
        Assert.Equal("Pending Review", membership.DocumentationStatusDisplay);
        Assert.Equal("Bounty Hunters / Bounty Hunters South", membership.DocumentationLinkageSummary);
    }

    [Fact]
    public async Task GetOrganizationDetailsAsync_MembershipsWithoutGangDocumentationShowNoDocumentation()
    {
        await using var fixture = await WorkspaceFixture.CreateAsync();
        var service = fixture.Services.GetRequiredService<IOrganizationService>();
        var organization = await service.CreateOrganizationAsync(
            new CreateOrganizationRequest("Rollin 60s", "gang", "active", null, null),
            CancellationToken.None
        );
        var globalPerson = await fixture.CreateGlobalPersonAsync("Marcus Gray", "Roster No Documentation Case");

        await service.AddMembershipAsync(
            new AddOrganizationMembershipRequest(
                organization.OrganizationId,
                globalPerson.GlobalEntityId,
                "associate",
                "associate",
                70,
                null,
                null,
                null,
                null,
                null,
                null
            ),
            CancellationToken.None
        );

        var details = await service.GetOrganizationDetailsAsync(organization.OrganizationId, CancellationToken.None);

        var membership = Assert.Single(details!.Memberships);
        Assert.False(membership.HasGangDocumentation);
        Assert.Equal("No Documentation", membership.DocumentationStatusDisplay);
        Assert.Null(membership.DocumentationLinkageSummary);
    }

    private sealed class WorkspaceFixture : IAsyncDisposable
    {
        private readonly ServiceProvider _provider;
        private readonly TestWorkspacePathProvider _pathProvider;

        private WorkspaceFixture(ServiceProvider provider, TestWorkspacePathProvider pathProvider)
        {
            _provider = provider;
            _pathProvider = pathProvider;
        }

        public IServiceProvider Services => _provider;

        public static async Task<WorkspaceFixture> CreateAsync()
        {
            var workspaceRoot = Path.Combine(
                Path.GetTempPath(),
                "CaseGraph.Infrastructure.Tests",
                Guid.NewGuid().ToString("N")
            );
            Directory.CreateDirectory(workspaceRoot);

            var pathProvider = new TestWorkspacePathProvider(workspaceRoot);
            var services = new ServiceCollection();
            services.AddSingleton<IClock>(
                new FixedClock(new DateTimeOffset(2026, 3, 12, 12, 0, 0, TimeSpan.Zero))
            );
            services.AddSingleton<IWorkspacePathProvider>(pathProvider);
            services.AddDbContextFactory<WorkspaceDbContext>(options =>
            {
                Directory.CreateDirectory(pathProvider.WorkspaceRoot);
                options.UseSqlite($"Data Source={pathProvider.WorkspaceDbPath}");
            });
            services.AddSingleton<WorkspaceDbRebuilder>();
            services.AddSingleton<WorkspaceDbInitializer>();
            services.AddSingleton<IWorkspaceDbInitializer>(
                provider => provider.GetRequiredService<WorkspaceDbInitializer>()
            );
            services.AddSingleton<IWorkspaceDatabaseInitializer>(
                provider => provider.GetRequiredService<WorkspaceDbInitializer>()
            );
            services.AddSingleton<IWorkspaceWriteGate, WorkspaceWriteGate>();
            services.AddSingleton<IAuditLogService, AuditLogService>();
            services.AddSingleton<ICaseWorkspaceService, CaseWorkspaceService>();
            services.AddSingleton<IMessageSearchService, MessageSearchService>();
            services.AddSingleton<ITargetMessagePresenceIndexService, TargetMessagePresenceIndexService>();
            services.AddSingleton<ITargetRegistryService, TargetRegistryService>();
            services.AddSingleton<IOrganizationService, OrganizationService>();

            var provider = services.BuildServiceProvider();
            var initializer = provider.GetRequiredService<IWorkspaceDatabaseInitializer>();
            await initializer.EnsureInitializedAsync(CancellationToken.None);

            return new WorkspaceFixture(provider, pathProvider);
        }

        public Task<WorkspaceDbContext> CreateDbContextAsync()
        {
            var factory = _provider.GetRequiredService<IDbContextFactory<WorkspaceDbContext>>();
            return factory.CreateDbContextAsync(CancellationToken.None);
        }

        public Task<CaseInfo> CreateCaseAsync(string name)
        {
            var workspace = _provider.GetRequiredService<ICaseWorkspaceService>();
            return workspace.CreateCaseAsync(name, CancellationToken.None);
        }

        public async Task<CreatedGlobalPerson> CreateGlobalPersonAsync(string displayName, string caseName)
        {
            var registry = _provider.GetRequiredService<ITargetRegistryService>();
            var caseInfo = await CreateCaseAsync(caseName);
            var target = await registry.CreateTargetAsync(
                new CreateTargetRequest(caseInfo.CaseId, displayName, null, null, CreateGlobalPerson: true),
                CancellationToken.None
            );
            var details = await registry.GetTargetDetailsAsync(caseInfo.CaseId, target.TargetId, CancellationToken.None);
            Assert.NotNull(details);
            Assert.NotNull(details!.GlobalPerson);

            return new CreatedGlobalPerson(
                details.GlobalPerson!.GlobalEntityId,
                details.GlobalPerson.DisplayName
            );
        }

        public async ValueTask DisposeAsync()
        {
            await _provider.DisposeAsync();

            if (!Directory.Exists(_pathProvider.WorkspaceRoot))
            {
                return;
            }

            for (var attempt = 1; attempt <= 5; attempt++)
            {
                try
                {
                    Directory.Delete(_pathProvider.WorkspaceRoot, recursive: true);
                    return;
                }
                catch (IOException)
                {
                    if (attempt == 5)
                    {
                        return;
                    }

                    await Task.Delay(50);
                }
                catch (UnauthorizedAccessException)
                {
                    if (attempt == 5)
                    {
                        return;
                    }

                    await Task.Delay(50);
                }
            }
        }
    }

    private sealed class TestWorkspacePathProvider : IWorkspacePathProvider
    {
        public TestWorkspacePathProvider(string workspaceRoot)
        {
            WorkspaceRoot = workspaceRoot;
            WorkspaceDbPath = Path.Combine(workspaceRoot, "workspace.db");
            CasesRoot = Path.Combine(workspaceRoot, "cases");
        }

        public string WorkspaceRoot { get; }

        public string WorkspaceDbPath { get; }

        public string CasesRoot { get; }
    }

    private sealed class FixedClock : IClock
    {
        public FixedClock(DateTimeOffset utcNow)
        {
            UtcNow = utcNow;
        }

        public DateTimeOffset UtcNow { get; }
    }

    private sealed record CreatedGlobalPerson(Guid GlobalEntityId, string DisplayName);
}
