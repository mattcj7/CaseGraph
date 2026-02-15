using CaseGraph.Core.Abstractions;
using CaseGraph.Core.Models;
using CaseGraph.Infrastructure.Persistence;
using CaseGraph.Infrastructure.Persistence.Entities;
using CaseGraph.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace CaseGraph.Infrastructure.Tests;

public sealed class TargetRegistryServiceTests
{
    [Fact]
    public async Task AddIdentifierAsync_DefaultConflictResolution_ThrowsConflict()
    {
        await using var fixture = await WorkspaceFixture.CreateAsync();
        var registry = fixture.Services.GetRequiredService<ITargetRegistryService>();

        var caseInfo = await fixture.CreateCaseAsync("Target Conflict Case");
        var alpha = await registry.CreateTargetAsync(
            new CreateTargetRequest(caseInfo.CaseId, "Alpha", null, null),
            CancellationToken.None
        );
        var bravo = await registry.CreateTargetAsync(
            new CreateTargetRequest(caseInfo.CaseId, "Bravo", null, null),
            CancellationToken.None
        );

        await registry.AddIdentifierAsync(
            new AddTargetIdentifierRequest(
                caseInfo.CaseId,
                alpha.TargetId,
                TargetIdentifierType.Phone,
                "(555) 123-0001",
                null,
                IsPrimary: true
            ),
            CancellationToken.None
        );

        var ex = await Assert.ThrowsAsync<IdentifierConflictException>(() => registry.AddIdentifierAsync(
            new AddTargetIdentifierRequest(
                caseInfo.CaseId,
                bravo.TargetId,
                TargetIdentifierType.Phone,
                "+1 (555) 123-0001",
                null,
                IsPrimary: false
            ),
            CancellationToken.None
        ));

        Assert.Equal(alpha.TargetId, ex.Conflict.ExistingTargetId);
        Assert.Equal("+15551230001", ex.Conflict.ValueNormalized);
    }

    [Fact]
    public async Task AddIdentifierAsync_MoveConflictResolution_MovesIdentifierToRequestedTarget()
    {
        await using var fixture = await WorkspaceFixture.CreateAsync();
        var registry = fixture.Services.GetRequiredService<ITargetRegistryService>();

        var caseInfo = await fixture.CreateCaseAsync("Target Move Case");
        var alpha = await registry.CreateTargetAsync(
            new CreateTargetRequest(caseInfo.CaseId, "Alpha", null, null),
            CancellationToken.None
        );
        var bravo = await registry.CreateTargetAsync(
            new CreateTargetRequest(caseInfo.CaseId, "Bravo", null, null),
            CancellationToken.None
        );

        var initial = await registry.AddIdentifierAsync(
            new AddTargetIdentifierRequest(
                caseInfo.CaseId,
                alpha.TargetId,
                TargetIdentifierType.Phone,
                "(555) 123-0001",
                null,
                IsPrimary: true
            ),
            CancellationToken.None
        );

        var moved = await registry.AddIdentifierAsync(
            new AddTargetIdentifierRequest(
                caseInfo.CaseId,
                bravo.TargetId,
                TargetIdentifierType.Phone,
                "5551230001",
                null,
                IsPrimary: true,
                ConflictResolution: IdentifierConflictResolution.MoveIdentifierToRequestedTarget
            ),
            CancellationToken.None
        );

        Assert.Equal(bravo.TargetId, moved.EffectiveTargetId);
        Assert.Equal(initial.Identifier.IdentifierId, moved.Identifier.IdentifierId);
        Assert.True(moved.MovedIdentifier);
        Assert.False(moved.UsedExistingTarget);

        await using var db = await fixture.CreateDbContextAsync();
        var links = await db.TargetIdentifierLinks
            .AsNoTracking()
            .Where(link => link.CaseId == caseInfo.CaseId && link.IdentifierId == moved.Identifier.IdentifierId)
            .ToListAsync();
        Assert.Single(links);
        Assert.Equal(bravo.TargetId, links[0].TargetId);

        var actionTypes = await db.AuditEvents
            .AsNoTracking()
            .Where(audit => audit.CaseId == caseInfo.CaseId)
            .Select(audit => audit.ActionType)
            .ToListAsync();
        Assert.Contains("IdentifierUnlinkedFromTarget", actionTypes);
        Assert.Contains("IdentifierLinkedToTarget", actionTypes);
    }

    [Fact]
    public async Task AddIdentifierAsync_UseExistingConflictResolution_KeepsExistingTarget()
    {
        await using var fixture = await WorkspaceFixture.CreateAsync();
        var registry = fixture.Services.GetRequiredService<ITargetRegistryService>();

        var caseInfo = await fixture.CreateCaseAsync("Target UseExisting Case");
        var alpha = await registry.CreateTargetAsync(
            new CreateTargetRequest(caseInfo.CaseId, "Alpha", null, null),
            CancellationToken.None
        );
        var bravo = await registry.CreateTargetAsync(
            new CreateTargetRequest(caseInfo.CaseId, "Bravo", null, null),
            CancellationToken.None
        );

        var initial = await registry.AddIdentifierAsync(
            new AddTargetIdentifierRequest(
                caseInfo.CaseId,
                alpha.TargetId,
                TargetIdentifierType.Phone,
                "(555) 123-0001",
                null,
                IsPrimary: true
            ),
            CancellationToken.None
        );

        var resolved = await registry.AddIdentifierAsync(
            new AddTargetIdentifierRequest(
                caseInfo.CaseId,
                bravo.TargetId,
                TargetIdentifierType.Phone,
                "+15551230001",
                null,
                IsPrimary: false,
                ConflictResolution: IdentifierConflictResolution.UseExistingTarget
            ),
            CancellationToken.None
        );

        Assert.Equal(alpha.TargetId, resolved.EffectiveTargetId);
        Assert.Equal(initial.Identifier.IdentifierId, resolved.Identifier.IdentifierId);
        Assert.True(resolved.UsedExistingTarget);
        Assert.False(resolved.MovedIdentifier);

        await using var db = await fixture.CreateDbContextAsync();
        var links = await db.TargetIdentifierLinks
            .AsNoTracking()
            .Where(link => link.CaseId == caseInfo.CaseId && link.IdentifierId == resolved.Identifier.IdentifierId)
            .ToListAsync();
        Assert.Single(links);
        Assert.Equal(alpha.TargetId, links[0].TargetId);
    }

    [Fact]
    public async Task LinkMessageParticipantAsync_WritesDerivedProvenanceAndParticipantAudit()
    {
        await using var fixture = await WorkspaceFixture.CreateAsync();
        var registry = fixture.Services.GetRequiredService<ITargetRegistryService>();

        var caseInfo = await fixture.CreateCaseAsync("Participant Link Case");
        var target = await registry.CreateTargetAsync(
            new CreateTargetRequest(caseInfo.CaseId, "Known Person", null, null),
            CancellationToken.None
        );

        var seeded = await fixture.SeedMessageEventAsync(caseInfo.CaseId, "+15551230001", "+15554445555");
        var linkResult = await registry.LinkMessageParticipantAsync(
            new LinkMessageParticipantRequest(
                caseInfo.CaseId,
                seeded.MessageEventId,
                MessageParticipantRole.Sender,
                "+1 (555) 123-0001",
                target.TargetId,
                null
            ),
            CancellationToken.None
        );

        await using var db = await fixture.CreateDbContextAsync();
        var participantLink = await db.MessageParticipantLinks
            .AsNoTracking()
            .FirstAsync(link => link.ParticipantLinkId == linkResult.ParticipantLinkId);

        Assert.Equal("Derived", participantLink.SourceType);
        Assert.Equal(seeded.EvidenceItemId, participantLink.SourceEvidenceItemId);
        Assert.Equal("targets-linker@1", participantLink.IngestModuleVersion);
        Assert.Contains(";role=Sender", participantLink.SourceLocator, StringComparison.Ordinal);
        Assert.Equal(target.TargetId, participantLink.TargetId);

        var identifier = await db.Identifiers
            .AsNoTracking()
            .FirstAsync(item => item.IdentifierId == linkResult.IdentifierId);
        Assert.Equal("Derived", identifier.SourceType);
        Assert.Equal(seeded.EvidenceItemId, identifier.SourceEvidenceItemId);
        Assert.Equal("targets-linker@1", identifier.IngestModuleVersion);

        var actionTypes = await db.AuditEvents
            .AsNoTracking()
            .Where(audit => audit.CaseId == caseInfo.CaseId)
            .Select(audit => audit.ActionType)
            .ToListAsync();
        Assert.Contains("ParticipantLinked", actionTypes);
    }

    [Fact]
    public async Task TargetCreateUpdateAndIdentifierEdit_WriteAuditTrail()
    {
        await using var fixture = await WorkspaceFixture.CreateAsync();
        var registry = fixture.Services.GetRequiredService<ITargetRegistryService>();

        var caseInfo = await fixture.CreateCaseAsync("Audit Trail Case");
        var target = await registry.CreateTargetAsync(
            new CreateTargetRequest(caseInfo.CaseId, "Initial Name", "Alias One", "Notes A"),
            CancellationToken.None
        );

        await registry.UpdateTargetAsync(
            new UpdateTargetRequest(caseInfo.CaseId, target.TargetId, "Updated Name", "Alias Two", "Notes B"),
            CancellationToken.None
        );

        var createdIdentifier = await registry.AddIdentifierAsync(
            new AddTargetIdentifierRequest(
                caseInfo.CaseId,
                target.TargetId,
                TargetIdentifierType.Username,
                "AliasTwo",
                "first pass",
                IsPrimary: true
            ),
            CancellationToken.None
        );

        await registry.UpdateIdentifierAsync(
            new UpdateTargetIdentifierRequest(
                caseInfo.CaseId,
                target.TargetId,
                createdIdentifier.Identifier.IdentifierId,
                TargetIdentifierType.Username,
                "AliasTwo",
                "updated notes",
                IsPrimary: true
            ),
            CancellationToken.None
        );

        await using var db = await fixture.CreateDbContextAsync();
        var actionTypes = await db.AuditEvents
            .AsNoTracking()
            .Where(audit => audit.CaseId == caseInfo.CaseId)
            .Select(audit => audit.ActionType)
            .ToListAsync();

        Assert.Contains("TargetCreated", actionTypes);
        Assert.Contains("TargetUpdated", actionTypes);
        Assert.Contains("IdentifierCreated", actionTypes);
        Assert.Contains("IdentifierUpdated", actionTypes);
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
                new FixedClock(new DateTimeOffset(2026, 2, 15, 12, 0, 0, TimeSpan.Zero))
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
            services.AddSingleton<IAuditLogService, AuditLogService>();
            services.AddSingleton<ICaseWorkspaceService, CaseWorkspaceService>();
            services.AddSingleton<ITargetRegistryService, TargetRegistryService>();

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

        public async Task<(Guid MessageEventId, Guid EvidenceItemId)> SeedMessageEventAsync(
            Guid caseId,
            string sender,
            string recipients
        )
        {
            var evidenceItemId = Guid.NewGuid();
            var threadId = Guid.NewGuid();
            var messageEventId = Guid.NewGuid();

            await using var db = await CreateDbContextAsync();
            db.EvidenceItems.Add(new EvidenceItemRecord
            {
                EvidenceItemId = evidenceItemId,
                CaseId = caseId,
                DisplayName = "Synthetic",
                OriginalPath = "synthetic",
                OriginalFileName = "synthetic.txt",
                AddedAtUtc = DateTimeOffset.UtcNow,
                SizeBytes = 1,
                Sha256Hex = "ab",
                FileExtension = ".txt",
                SourceType = "OTHER",
                ManifestRelativePath = "manifest.json",
                StoredRelativePath = "stored.dat"
            });

            db.MessageThreads.Add(new MessageThreadRecord
            {
                ThreadId = threadId,
                CaseId = caseId,
                EvidenceItemId = evidenceItemId,
                Platform = "SMS",
                ThreadKey = "thread-participant-link",
                CreatedAtUtc = DateTimeOffset.UtcNow,
                SourceLocator = "test:thread",
                IngestModuleVersion = "test"
            });

            db.MessageEvents.Add(new MessageEventRecord
            {
                MessageEventId = messageEventId,
                ThreadId = threadId,
                CaseId = caseId,
                EvidenceItemId = evidenceItemId,
                Platform = "SMS",
                TimestampUtc = DateTimeOffset.UtcNow,
                Direction = "Incoming",
                Sender = sender,
                Recipients = recipients,
                Body = "synthetic body",
                IsDeleted = false,
                SourceLocator = "xlsx:test#Messages:R2",
                IngestModuleVersion = "test"
            });

            await db.SaveChangesAsync();
            return (messageEventId, evidenceItemId);
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
}

