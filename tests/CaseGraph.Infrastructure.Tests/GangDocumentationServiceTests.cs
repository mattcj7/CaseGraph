using CaseGraph.Core.Abstractions;
using CaseGraph.Core.Models;
using CaseGraph.Infrastructure.GangDocumentation;
using CaseGraph.Infrastructure.Organizations;
using CaseGraph.Infrastructure.Persistence;
using CaseGraph.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace CaseGraph.Infrastructure.Tests;

public sealed class GangDocumentationServiceTests
{
    [Fact]
    public async Task CreateDocumentationAsync_PersistsAndReloadsRecord()
    {
        await using var fixture = await GangDocumentationTestWorkspaceFixture.CreateAsync();
        var service = fixture.Services.GetRequiredService<IGangDocumentationService>();
        var organizationService = fixture.Services.GetRequiredService<IOrganizationService>();

        var createdTarget = await fixture.CreateTargetAsync("Marcus Lane", createGlobalPerson: true);
        var organization = await organizationService.CreateOrganizationAsync(
            new CreateOrganizationRequest("Rollin 60s", "gang", "active", null, "Primary organization"),
            CancellationToken.None
        );

        var created = await service.CreateDocumentationAsync(
            new CreateGangDocumentationRequest(
                createdTarget.CaseId,
                createdTarget.TargetId,
                organization.OrganizationId,
                null,
                "member",
                "Documented during street check.",
                "Formal documentation notes"
            ),
            CancellationToken.None
        );

        var reloaded = await service.GetDocumentationForTargetAsync(
            createdTarget.CaseId,
            createdTarget.TargetId,
            CancellationToken.None
        );

        var record = Assert.Single(reloaded);
        Assert.Equal(created.DocumentationId, record.DocumentationId);
        Assert.Equal("Rollin 60s", record.OrganizationName);
        Assert.Equal("member", record.AffiliationRole);
        Assert.Equal(GangDocumentationCatalog.WorkflowStatusDraft, record.Review.WorkflowStatus);
        Assert.Null(record.Review.ReviewerName);
        Assert.Equal("Documented during street check.", record.Summary);
        Assert.Equal(createdTarget.GlobalEntityId, record.GlobalEntityId);

        await using var db = await fixture.CreateDbContextAsync();
        var persisted = await db.GangDocumentationRecords.AsNoTracking().FirstOrDefaultAsync(
            item => item.DocumentationId == created.DocumentationId
        );
        Assert.NotNull(persisted);
        Assert.Equal(createdTarget.TargetId, persisted!.TargetId);
        Assert.Equal(createdTarget.GlobalEntityId, persisted.GlobalEntityId);
    }

    [Fact]
    public async Task SaveCriterionAsync_PersistsAndReloadsCriteria()
    {
        await using var fixture = await GangDocumentationTestWorkspaceFixture.CreateAsync();
        var service = fixture.Services.GetRequiredService<IGangDocumentationService>();
        var organizationService = fixture.Services.GetRequiredService<IOrganizationService>();

        var createdTarget = await fixture.CreateTargetAsync("Devon Price", createGlobalPerson: true);
        var organization = await organizationService.CreateOrganizationAsync(
            new CreateOrganizationRequest("Neighborhood Crips", "gang", "active", null, null),
            CancellationToken.None
        );
        var documentation = await service.CreateDocumentationAsync(
            new CreateGangDocumentationRequest(
                createdTarget.CaseId,
                createdTarget.TargetId,
                organization.OrganizationId,
                null,
                "associate",
                "Pending formal review.",
                null
            ),
            CancellationToken.None
        );

        var createdCriterion = await service.SaveCriterionAsync(
            new SaveGangDocumentationCriterionRequest(
                createdTarget.CaseId,
                documentation.DocumentationId,
                null,
                "social media evidence",
                true,
                "Instagram photos with documented members.",
                new DateTimeOffset(2026, 3, 10, 0, 0, 0, TimeSpan.Zero),
                "Case note 44"
            ),
            CancellationToken.None
        );
        await service.SaveCriterionAsync(
            new SaveGangDocumentationCriterionRequest(
                createdTarget.CaseId,
                documentation.DocumentationId,
                createdCriterion.CriterionId,
                "social media evidence",
                true,
                "Instagram photos and comments with documented members.",
                new DateTimeOffset(2026, 3, 10, 0, 0, 0, TimeSpan.Zero),
                "Case note 44 updated"
            ),
            CancellationToken.None
        );

        var record = Assert.Single(await service.GetDocumentationForTargetAsync(
            createdTarget.CaseId,
            createdTarget.TargetId,
            CancellationToken.None
        ));
        var criterion = Assert.Single(record.Criteria);
        Assert.Equal(createdCriterion.CriterionId, criterion.CriterionId);
        Assert.Equal("social media evidence", criterion.CriterionType);
        Assert.Equal("Instagram photos and comments with documented members.", criterion.BasisSummary);
        Assert.Equal("Case note 44 updated", criterion.SourceNote);

        await using var db = await fixture.CreateDbContextAsync();
        var persisted = await db.GangDocumentationCriteria.AsNoTracking().FirstOrDefaultAsync(
            item => item.CriterionId == createdCriterion.CriterionId
        );
        Assert.NotNull(persisted);
        Assert.Equal("social media evidence", persisted!.CriterionType);
        Assert.Equal("Instagram photos and comments with documented members.", persisted.BasisSummary);
    }

    [Fact]
    public async Task CreateDocumentationAsync_PersistsOrganizationAndSubgroupLinkage()
    {
        await using var fixture = await GangDocumentationTestWorkspaceFixture.CreateAsync();
        var service = fixture.Services.GetRequiredService<IGangDocumentationService>();
        var organizationService = fixture.Services.GetRequiredService<IOrganizationService>();

        var createdTarget = await fixture.CreateTargetAsync("Alicia Ward", createGlobalPerson: true);
        var organization = await organizationService.CreateOrganizationAsync(
            new CreateOrganizationRequest("Grape Street", "gang", "active", null, null),
            CancellationToken.None
        );
        var subgroup = await organizationService.CreateOrganizationAsync(
            new CreateOrganizationRequest("Grape Street East", "set", "active", organization.OrganizationId, null),
            CancellationToken.None
        );

        var created = await service.CreateDocumentationAsync(
            new CreateGangDocumentationRequest(
                createdTarget.CaseId,
                createdTarget.TargetId,
                organization.OrganizationId,
                subgroup.OrganizationId,
                "member",
                "Confirmed set linkage.",
                null
            ),
            CancellationToken.None
        );

        Assert.Equal(organization.OrganizationId, created.OrganizationId);
        Assert.Equal("Grape Street", created.OrganizationName);
        Assert.Equal(subgroup.OrganizationId, created.SubgroupOrganizationId);
        Assert.Equal("Grape Street East", created.SubgroupOrganizationName);

        await using var db = await fixture.CreateDbContextAsync();
        var persisted = await db.GangDocumentationRecords.AsNoTracking().FirstOrDefaultAsync(
            item => item.DocumentationId == created.DocumentationId
        );
        Assert.NotNull(persisted);
        Assert.Equal(organization.OrganizationId, persisted!.OrganizationId);
        Assert.Equal(subgroup.OrganizationId, persisted.SubgroupOrganizationId);
    }

    [Fact]
    public async Task UpdateDocumentationAsync_ReloadsLinkedOrganizationAndSubgroup()
    {
        await using var fixture = await GangDocumentationTestWorkspaceFixture.CreateAsync();
        var service = fixture.Services.GetRequiredService<IGangDocumentationService>();
        var organizationService = fixture.Services.GetRequiredService<IOrganizationService>();

        var createdTarget = await fixture.CreateTargetAsync("Monica Diaz", createGlobalPerson: true);
        var originalOrganization = await organizationService.CreateOrganizationAsync(
            new CreateOrganizationRequest("Original Group", "gang", "active", null, null),
            CancellationToken.None
        );
        var updatedOrganization = await organizationService.CreateOrganizationAsync(
            new CreateOrganizationRequest("Bounty Hunters", "gang", "active", null, null),
            CancellationToken.None
        );
        var updatedSubgroup = await organizationService.CreateOrganizationAsync(
            new CreateOrganizationRequest("Bounty Hunters South", "set", "active", updatedOrganization.OrganizationId, null),
            CancellationToken.None
        );

        var documentation = await service.CreateDocumentationAsync(
            new CreateGangDocumentationRequest(
                createdTarget.CaseId,
                createdTarget.TargetId,
                originalOrganization.OrganizationId,
                null,
                "associate",
                "Initial documentation.",
                null
            ),
            CancellationToken.None
        );

        await service.UpdateDocumentationAsync(
            new UpdateGangDocumentationRequest(
                createdTarget.CaseId,
                documentation.DocumentationId,
                updatedOrganization.OrganizationId,
                updatedSubgroup.OrganizationId,
                "member",
                "Updated organization linkage.",
                null
            ),
            CancellationToken.None
        );

        var reloaded = Assert.Single(await service.GetDocumentationForTargetAsync(
            createdTarget.CaseId,
            createdTarget.TargetId,
            CancellationToken.None
        ));

        Assert.Equal(updatedOrganization.OrganizationId, reloaded.OrganizationId);
        Assert.Equal("Bounty Hunters", reloaded.OrganizationName);
        Assert.Equal(updatedSubgroup.OrganizationId, reloaded.SubgroupOrganizationId);
        Assert.Equal("Bounty Hunters South", reloaded.SubgroupOrganizationName);
    }
}

internal sealed class GangDocumentationTestWorkspaceFixture : IAsyncDisposable
{
    private readonly ServiceProvider _provider;
    private readonly GangDocumentationTestWorkspacePathProvider _pathProvider;

    private GangDocumentationTestWorkspaceFixture(ServiceProvider provider, GangDocumentationTestWorkspacePathProvider pathProvider)
    {
        _provider = provider;
        _pathProvider = pathProvider;
    }

    public IServiceProvider Services => _provider;

    public static async Task<GangDocumentationTestWorkspaceFixture> CreateAsync()
    {
        var workspaceRoot = Path.Combine(
            Path.GetTempPath(),
            "CaseGraph.Infrastructure.Tests",
            Guid.NewGuid().ToString("N")
        );
        Directory.CreateDirectory(workspaceRoot);

        var pathProvider = new GangDocumentationTestWorkspacePathProvider(workspaceRoot);
        var services = new ServiceCollection();
        services.AddSingleton<IClock>(
            new GangDocumentationFixedClock(new DateTimeOffset(2026, 3, 14, 12, 0, 0, TimeSpan.Zero))
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
        services.AddSingleton<IGangDocumentationService, GangDocumentationService>();
        services.AddSingleton<IGangDocumentationPacketExportService, GangDocumentationPacketExportService>();

        var provider = services.BuildServiceProvider();
        var initializer = provider.GetRequiredService<IWorkspaceDatabaseInitializer>();
        await initializer.EnsureInitializedAsync(CancellationToken.None);

        return new GangDocumentationTestWorkspaceFixture(provider, pathProvider);
    }

    public Task<WorkspaceDbContext> CreateDbContextAsync()
    {
        var factory = _provider.GetRequiredService<IDbContextFactory<WorkspaceDbContext>>();
        return factory.CreateDbContextAsync(CancellationToken.None);
    }

    public async Task<GangDocumentationCreatedTarget> CreateTargetAsync(string displayName, bool createGlobalPerson)
    {
        var workspace = _provider.GetRequiredService<ICaseWorkspaceService>();
        var targetRegistry = _provider.GetRequiredService<ITargetRegistryService>();
        var caseInfo = await workspace.CreateCaseAsync($"{displayName} Case", CancellationToken.None);
        var target = await targetRegistry.CreateTargetAsync(
            new CreateTargetRequest(caseInfo.CaseId, displayName, null, null, CreateGlobalPerson: createGlobalPerson),
            CancellationToken.None
        );
        var details = await targetRegistry.GetTargetDetailsAsync(caseInfo.CaseId, target.TargetId, CancellationToken.None);
        Assert.NotNull(details);

        return new GangDocumentationCreatedTarget(
            caseInfo.CaseId,
            target.TargetId,
            details!.GlobalPerson?.GlobalEntityId
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

internal sealed class GangDocumentationTestWorkspacePathProvider : IWorkspacePathProvider
{
    public GangDocumentationTestWorkspacePathProvider(string workspaceRoot)
    {
        WorkspaceRoot = workspaceRoot;
        WorkspaceDbPath = Path.Combine(workspaceRoot, "workspace.db");
        CasesRoot = Path.Combine(workspaceRoot, "cases");
    }

    public string WorkspaceRoot { get; }

    public string WorkspaceDbPath { get; }

    public string CasesRoot { get; }
}

internal sealed class GangDocumentationFixedClock : IClock
{
    public GangDocumentationFixedClock(DateTimeOffset utcNow)
    {
        UtcNow = utcNow;
    }

    public DateTimeOffset UtcNow { get; }
}

internal sealed record GangDocumentationCreatedTarget(Guid CaseId, Guid TargetId, Guid? GlobalEntityId);
