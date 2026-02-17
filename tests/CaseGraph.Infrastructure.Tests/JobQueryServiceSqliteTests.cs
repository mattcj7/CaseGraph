using CaseGraph.Core.Abstractions;
using CaseGraph.Core.Models;
using CaseGraph.Infrastructure.Persistence;
using CaseGraph.Infrastructure.Persistence.Entities;
using CaseGraph.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace CaseGraph.Infrastructure.Tests;

public sealed class JobQueryServiceSqliteTests
{
    [Fact]
    public async Task GetLatestJobForEvidenceAsync_DoesNotThrow_AndReturnsLatestByCoalescedTimestamp()
    {
        await using var fixture = await WorkspaceFixture.CreateAsync();

        var caseId = Guid.NewGuid();
        var evidenceItemId = Guid.NewGuid();

        var jobCreatedOnlyId = Guid.NewGuid();
        var jobStartedId = Guid.NewGuid();
        var jobCompletedId = Guid.NewGuid();
        var otherTypeJobId = Guid.NewGuid();

        await using (var db = await fixture.CreateDbContextAsync())
        {
            db.Cases.Add(CreateCase(caseId, "Latest Case"));
            db.EvidenceItems.Add(CreateEvidenceItem(caseId, evidenceItemId, "chat.xlsx"));
            db.Jobs.AddRange(
                CreateJob(
                    jobCreatedOnlyId,
                    caseId,
                    evidenceItemId,
                    "MessagesIngest",
                    new DateTimeOffset(2026, 2, 17, 10, 0, 0, TimeSpan.Zero),
                    null,
                    null
                ),
                CreateJob(
                    jobStartedId,
                    caseId,
                    evidenceItemId,
                    "MessagesIngest",
                    new DateTimeOffset(2026, 2, 17, 9, 0, 0, TimeSpan.Zero),
                    new DateTimeOffset(2026, 2, 17, 11, 0, 0, TimeSpan.Zero),
                    null
                ),
                CreateJob(
                    jobCompletedId,
                    caseId,
                    evidenceItemId,
                    "MessagesIngest",
                    new DateTimeOffset(2026, 2, 17, 8, 0, 0, TimeSpan.Zero),
                    new DateTimeOffset(2026, 2, 17, 8, 30, 0, TimeSpan.Zero),
                    new DateTimeOffset(2026, 2, 17, 12, 0, 0, TimeSpan.Zero)
                ),
                CreateJob(
                    otherTypeJobId,
                    caseId,
                    evidenceItemId,
                    "EvidenceVerify",
                    new DateTimeOffset(2026, 2, 17, 13, 0, 0, TimeSpan.Zero),
                    null,
                    null
                )
            );
            await db.SaveChangesAsync();
        }

        JobInfo? latest = null;
        var exception = await Record.ExceptionAsync(async () =>
        {
            latest = await fixture.JobQueryService.GetLatestJobForEvidenceAsync(
                caseId,
                evidenceItemId,
                "MessagesIngest",
                CancellationToken.None
            );
        });

        Assert.Null(exception);
        Assert.NotNull(latest);
        Assert.Equal(jobCompletedId, latest.JobId);
        Assert.Equal(JobStatus.Succeeded, latest.Status);
    }

    [Fact]
    public async Task GetRecentJobsAsync_DoesNotThrow_AndReturnsExpectedOrder()
    {
        await using var fixture = await WorkspaceFixture.CreateAsync();

        var caseId = Guid.NewGuid();
        var evidenceItemId = Guid.NewGuid();
        var otherCaseId = Guid.NewGuid();
        var otherEvidenceItemId = Guid.NewGuid();

        var oldestId = Guid.NewGuid();
        var createdOnlyId = Guid.NewGuid();
        var startedId = Guid.NewGuid();
        var completedId = Guid.NewGuid();
        var otherCaseJobId = Guid.NewGuid();

        await using (var db = await fixture.CreateDbContextAsync())
        {
            db.Cases.AddRange(
                CreateCase(caseId, "Recent Case"),
                CreateCase(otherCaseId, "Other Case")
            );
            db.EvidenceItems.AddRange(
                CreateEvidenceItem(caseId, evidenceItemId, "recent.xlsx"),
                CreateEvidenceItem(otherCaseId, otherEvidenceItemId, "other.xlsx")
            );
            db.Jobs.AddRange(
                CreateJob(
                    oldestId,
                    caseId,
                    evidenceItemId,
                    "MessagesIngest",
                    new DateTimeOffset(2026, 2, 17, 6, 0, 0, TimeSpan.Zero),
                    null,
                    null
                ),
                CreateJob(
                    createdOnlyId,
                    caseId,
                    evidenceItemId,
                    "MessagesIngest",
                    new DateTimeOffset(2026, 2, 17, 9, 0, 0, TimeSpan.Zero),
                    null,
                    null
                ),
                CreateJob(
                    startedId,
                    caseId,
                    evidenceItemId,
                    "MessagesIngest",
                    new DateTimeOffset(2026, 2, 17, 8, 0, 0, TimeSpan.Zero),
                    new DateTimeOffset(2026, 2, 17, 10, 0, 0, TimeSpan.Zero),
                    null
                ),
                CreateJob(
                    completedId,
                    caseId,
                    evidenceItemId,
                    "EvidenceVerify",
                    new DateTimeOffset(2026, 2, 17, 7, 0, 0, TimeSpan.Zero),
                    new DateTimeOffset(2026, 2, 17, 7, 30, 0, TimeSpan.Zero),
                    new DateTimeOffset(2026, 2, 17, 11, 0, 0, TimeSpan.Zero)
                ),
                CreateJob(
                    otherCaseJobId,
                    otherCaseId,
                    otherEvidenceItemId,
                    "MessagesIngest",
                    new DateTimeOffset(2026, 2, 17, 12, 0, 0, TimeSpan.Zero),
                    null,
                    null
                )
            );
            await db.SaveChangesAsync();
        }

        IReadOnlyList<JobInfo>? recent = null;
        var exception = await Record.ExceptionAsync(async () =>
        {
            recent = await fixture.JobQueryService.GetRecentJobsAsync(
                caseId,
                take: 3,
                CancellationToken.None
            );
        });

        Assert.Null(exception);
        Assert.NotNull(recent);
        Assert.Equal(3, recent.Count);
        Assert.Equal(completedId, recent[0].JobId);
        Assert.Equal(startedId, recent[1].JobId);
        Assert.Equal(createdOnlyId, recent[2].JobId);
    }

    private static CaseRecord CreateCase(Guid caseId, string name)
    {
        return new CaseRecord
        {
            CaseId = caseId,
            Name = name,
            CreatedAtUtc = new DateTimeOffset(2026, 2, 17, 5, 0, 0, TimeSpan.Zero)
        };
    }

    private static EvidenceItemRecord CreateEvidenceItem(Guid caseId, Guid evidenceItemId, string fileName)
    {
        return new EvidenceItemRecord
        {
            EvidenceItemId = evidenceItemId,
            CaseId = caseId,
            DisplayName = fileName,
            OriginalPath = $@"C:\input\{fileName}",
            OriginalFileName = fileName,
            AddedAtUtc = new DateTimeOffset(2026, 2, 17, 5, 5, 0, TimeSpan.Zero),
            SizeBytes = 1024,
            Sha256Hex = "abc123",
            FileExtension = ".xlsx",
            SourceType = "XLSX",
            ManifestRelativePath = $"vault/{evidenceItemId:D}/manifest.json",
            StoredRelativePath = $"vault/{evidenceItemId:D}/original/{fileName}"
        };
    }

    private static JobRecord CreateJob(
        Guid jobId,
        Guid caseId,
        Guid evidenceItemId,
        string jobType,
        DateTimeOffset createdAtUtc,
        DateTimeOffset? startedAtUtc,
        DateTimeOffset? completedAtUtc
    )
    {
        return new JobRecord
        {
            JobId = jobId,
            CreatedAtUtc = createdAtUtc,
            StartedAtUtc = startedAtUtc,
            CompletedAtUtc = completedAtUtc,
            Status = JobStatus.Succeeded.ToString(),
            JobType = jobType,
            CaseId = caseId,
            EvidenceItemId = evidenceItemId,
            Progress = 1,
            StatusMessage = "Succeeded.",
            ErrorMessage = null,
            JsonPayload = "{}",
            CorrelationId = Guid.NewGuid().ToString("N"),
            Operator = "tester"
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

        public IJobQueryService JobQueryService => _provider.GetRequiredService<IJobQueryService>();

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
                new FixedClock(new DateTimeOffset(2026, 2, 17, 12, 0, 0, TimeSpan.Zero))
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
            services.AddSingleton<IJobQueryService, JobQueryService>();

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
