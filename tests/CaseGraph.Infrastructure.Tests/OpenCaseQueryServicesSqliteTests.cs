using CaseGraph.Core.Abstractions;
using CaseGraph.Infrastructure.Persistence;
using CaseGraph.Infrastructure.Persistence.Entities;
using CaseGraph.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace CaseGraph.Infrastructure.Tests;

public sealed class OpenCaseQueryServicesSqliteTests
{
    [Fact]
    public async Task GetRecentCasesAsync_DoesNotThrow_AndReturnsExpectedOrder()
    {
        await using var fixture = await WorkspaceFixture.CreateAsync();

        var caseFromLastOpened = Guid.NewGuid();
        var caseFromCreated = Guid.NewGuid();
        var caseOldest = Guid.NewGuid();

        await using (var db = await fixture.CreateDbContextAsync())
        {
            db.Cases.AddRange(
                new CaseRecord
                {
                    CaseId = caseOldest,
                    Name = "Oldest",
                    CreatedAtUtc = new DateTimeOffset(2026, 2, 15, 8, 0, 0, TimeSpan.Zero),
                    LastOpenedAtUtc = null
                },
                new CaseRecord
                {
                    CaseId = caseFromCreated,
                    Name = "Created Order",
                    CreatedAtUtc = new DateTimeOffset(2026, 2, 17, 10, 0, 0, TimeSpan.Zero),
                    LastOpenedAtUtc = null
                },
                new CaseRecord
                {
                    CaseId = caseFromLastOpened,
                    Name = "Last Opened Order",
                    CreatedAtUtc = new DateTimeOffset(2026, 2, 16, 9, 0, 0, TimeSpan.Zero),
                    LastOpenedAtUtc = new DateTimeOffset(2026, 2, 17, 11, 0, 0, TimeSpan.Zero)
                }
            );

            await db.SaveChangesAsync();
        }

        IReadOnlyList<CaseGraph.Core.Models.CaseInfo>? recentCases = null;
        var exception = await Record.ExceptionAsync(async () =>
        {
            recentCases = await fixture.CaseQueryService.GetRecentCasesAsync(CancellationToken.None);
        });

        Assert.Null(exception);
        Assert.NotNull(recentCases);
        Assert.Equal(3, recentCases.Count);
        Assert.Equal(caseFromLastOpened, recentCases[0].CaseId);
        Assert.Equal(caseFromCreated, recentCases[1].CaseId);
        Assert.Equal(caseOldest, recentCases[2].CaseId);
    }

    [Fact]
    public async Task GetEvidenceForCaseAsync_DoesNotThrow_AndReturnsExpectedOrder()
    {
        await using var fixture = await WorkspaceFixture.CreateAsync();

        var caseId = Guid.NewGuid();
        var otherCaseId = Guid.NewGuid();
        var newestEvidenceId = Guid.NewGuid();
        var middleEvidenceId = Guid.NewGuid();
        var oldestEvidenceId = Guid.NewGuid();

        await using (var db = await fixture.CreateDbContextAsync())
        {
            db.Cases.AddRange(
                new CaseRecord
                {
                    CaseId = caseId,
                    Name = "Evidence Case",
                    CreatedAtUtc = new DateTimeOffset(2026, 2, 16, 8, 0, 0, TimeSpan.Zero)
                },
                new CaseRecord
                {
                    CaseId = otherCaseId,
                    Name = "Other Evidence Case",
                    CreatedAtUtc = new DateTimeOffset(2026, 2, 16, 8, 5, 0, TimeSpan.Zero)
                }
            );

            db.EvidenceItems.AddRange(
                CreateEvidenceItem(
                    caseId,
                    oldestEvidenceId,
                    "oldest.xlsx",
                    new DateTimeOffset(2026, 2, 17, 9, 0, 0, TimeSpan.Zero)
                ),
                CreateEvidenceItem(
                    caseId,
                    newestEvidenceId,
                    "newest.xlsx",
                    new DateTimeOffset(2026, 2, 17, 11, 0, 0, TimeSpan.Zero)
                ),
                CreateEvidenceItem(
                    caseId,
                    middleEvidenceId,
                    "middle.xlsx",
                    new DateTimeOffset(2026, 2, 17, 10, 0, 0, TimeSpan.Zero)
                ),
                CreateEvidenceItem(
                    otherCaseId,
                    Guid.NewGuid(),
                    "other-case.xlsx",
                    new DateTimeOffset(2026, 2, 18, 8, 0, 0, TimeSpan.Zero)
                )
            );

            await db.SaveChangesAsync();
        }

        IReadOnlyList<CaseGraph.Core.Models.EvidenceItem>? evidence = null;
        var exception = await Record.ExceptionAsync(async () =>
        {
            evidence = await fixture.CaseQueryService.GetEvidenceForCaseAsync(
                caseId,
                CancellationToken.None
            );
        });

        Assert.Null(exception);
        Assert.NotNull(evidence);
        Assert.Equal(3, evidence.Count);
        Assert.Equal(newestEvidenceId, evidence[0].EvidenceItemId);
        Assert.Equal(middleEvidenceId, evidence[1].EvidenceItemId);
        Assert.Equal(oldestEvidenceId, evidence[2].EvidenceItemId);
    }

    [Fact]
    public async Task GetRecentAuditAsync_DoesNotThrow_AndReturnsExpectedOrder()
    {
        await using var fixture = await WorkspaceFixture.CreateAsync();

        var caseId = Guid.NewGuid();
        var otherCaseId = Guid.NewGuid();
        var newestAuditId = Guid.NewGuid();
        var middleAuditId = Guid.NewGuid();
        var oldestAuditId = Guid.NewGuid();

        await using (var db = await fixture.CreateDbContextAsync())
        {
            db.Cases.AddRange(
                new CaseRecord
                {
                    CaseId = caseId,
                    Name = "Audit Case",
                    CreatedAtUtc = new DateTimeOffset(2026, 2, 16, 8, 0, 0, TimeSpan.Zero)
                },
                new CaseRecord
                {
                    CaseId = otherCaseId,
                    Name = "Other Audit Case",
                    CreatedAtUtc = new DateTimeOffset(2026, 2, 16, 8, 10, 0, TimeSpan.Zero)
                }
            );

            db.AuditEvents.AddRange(
                CreateAuditEvent(
                    oldestAuditId,
                    caseId,
                    new DateTimeOffset(2026, 2, 17, 8, 0, 0, TimeSpan.Zero),
                    "Oldest"
                ),
                CreateAuditEvent(
                    newestAuditId,
                    caseId,
                    new DateTimeOffset(2026, 2, 17, 10, 0, 0, TimeSpan.Zero),
                    "Newest"
                ),
                CreateAuditEvent(
                    middleAuditId,
                    caseId,
                    new DateTimeOffset(2026, 2, 17, 9, 0, 0, TimeSpan.Zero),
                    "Middle"
                ),
                CreateAuditEvent(
                    Guid.NewGuid(),
                    otherCaseId,
                    new DateTimeOffset(2026, 2, 18, 8, 0, 0, TimeSpan.Zero),
                    "Other Case"
                )
            );

            await db.SaveChangesAsync();
        }

        IReadOnlyList<CaseGraph.Core.Models.AuditEvent>? auditEvents = null;
        var exception = await Record.ExceptionAsync(async () =>
        {
            auditEvents = await fixture.AuditQueryService.GetRecentAuditAsync(
                caseId,
                take: 2,
                CancellationToken.None
            );
        });

        Assert.Null(exception);
        Assert.NotNull(auditEvents);
        Assert.Equal(2, auditEvents.Count);
        Assert.Equal(newestAuditId, auditEvents[0].AuditEventId);
        Assert.Equal(middleAuditId, auditEvents[1].AuditEventId);
    }

    private static EvidenceItemRecord CreateEvidenceItem(
        Guid caseId,
        Guid evidenceItemId,
        string fileName,
        DateTimeOffset addedAtUtc
    )
    {
        return new EvidenceItemRecord
        {
            EvidenceItemId = evidenceItemId,
            CaseId = caseId,
            DisplayName = fileName,
            OriginalPath = $@"C:\input\{fileName}",
            OriginalFileName = fileName,
            AddedAtUtc = addedAtUtc,
            SizeBytes = 2048,
            Sha256Hex = "abc123",
            FileExtension = ".xlsx",
            SourceType = "XLSX",
            ManifestRelativePath = $"vault/{evidenceItemId:D}/manifest.json",
            StoredRelativePath = $"vault/{evidenceItemId:D}/original/{fileName}"
        };
    }

    private static AuditEventRecord CreateAuditEvent(
        Guid auditEventId,
        Guid caseId,
        DateTimeOffset timestampUtc,
        string summary
    )
    {
        return new AuditEventRecord
        {
            AuditEventId = auditEventId,
            TimestampUtc = timestampUtc,
            Operator = "tester",
            ActionType = "CaseOpened",
            CaseId = caseId,
            EvidenceItemId = null,
            Summary = summary,
            JsonPayload = null
        };
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

        public ICaseQueryService CaseQueryService => _provider.GetRequiredService<ICaseQueryService>();

        public IAuditQueryService AuditQueryService => _provider.GetRequiredService<IAuditQueryService>();

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
                new FixedClock(new DateTimeOffset(2026, 2, 18, 12, 0, 0, TimeSpan.Zero))
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
            services.AddSingleton<ICaseQueryService, CaseQueryService>();
            services.AddSingleton<IAuditQueryService, AuditQueryService>();

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
