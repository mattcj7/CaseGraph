using CaseGraph.Core.Abstractions;
using CaseGraph.Core.Models;
using CaseGraph.Infrastructure.Persistence;
using CaseGraph.Infrastructure.Persistence.Entities;
using CaseGraph.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace CaseGraph.Infrastructure.Tests;

public sealed class EvidenceVaultServiceTests
{
    [Fact]
    public async Task CreateCaseAsync_WritesCaseRecordToSqlite()
    {
        await using var fixture = await WorkspaceFixture.CreateAsync();
        var workspace = fixture.Services.GetRequiredService<ICaseWorkspaceService>();

        var caseInfo = await workspace.CreateCaseAsync("Case One", CancellationToken.None);

        await using var db = await fixture.CreateDbContextAsync();
        var caseRecord = await db.Cases.FirstOrDefaultAsync(c => c.CaseId == caseInfo.CaseId);

        Assert.NotNull(caseRecord);
        Assert.Equal("Case One", caseRecord.Name);
    }

    [Fact]
    public async Task ImportEvidenceFileAsync_CreatesStoredFileManifestHashAndAudit()
    {
        await using var fixture = await WorkspaceFixture.CreateAsync();
        var workspace = fixture.Services.GetRequiredService<ICaseWorkspaceService>();
        var vault = fixture.Services.GetRequiredService<IEvidenceVaultService>();

        var caseInfo = await workspace.CreateCaseAsync("Import Case", CancellationToken.None);
        var sourceFile = fixture.CreateSourceFile("sample.txt", "Alpha bravo charlie.");

        var evidenceItem = await vault.ImportEvidenceFileAsync(caseInfo, sourceFile, null, CancellationToken.None);
        var storedPath = fixture.ResolveCasePath(caseInfo.CaseId, evidenceItem.StoredRelativePath);
        var manifestPath = fixture.ResolveCasePath(caseInfo.CaseId, evidenceItem.ManifestRelativePath);

        Assert.True(File.Exists(storedPath));
        Assert.True(File.Exists(manifestPath));

        var computedHash = await ComputeSha256Async(storedPath);
        Assert.Equal(evidenceItem.Sha256Hex, computedHash);

        var manifestJson = await File.ReadAllTextAsync(manifestPath);
        using var manifest = JsonDocument.Parse(manifestJson);
        Assert.Equal(1, manifest.RootElement.GetProperty("SchemaVersion").GetInt32());
        Assert.Equal(evidenceItem.Sha256Hex, manifest.RootElement.GetProperty("Sha256Hex").GetString());

        await using var db = await fixture.CreateDbContextAsync();
        var evidenceRecord = await db.EvidenceItems.FirstOrDefaultAsync(
            e => e.EvidenceItemId == evidenceItem.EvidenceItemId
        );
        Assert.NotNull(evidenceRecord);

        var importAudit = await db.AuditEvents
            .Where(a => a.ActionType == "EvidenceImported" && a.EvidenceItemId == evidenceItem.EvidenceItemId)
            .FirstOrDefaultAsync();
        Assert.NotNull(importAudit);
    }

    [Fact]
    public async Task VerifyEvidenceAsync_WritesOkAndFailAuditEvents()
    {
        await using var fixture = await WorkspaceFixture.CreateAsync();
        var workspace = fixture.Services.GetRequiredService<ICaseWorkspaceService>();
        var vault = fixture.Services.GetRequiredService<IEvidenceVaultService>();

        var caseInfo = await workspace.CreateCaseAsync("Verify Case", CancellationToken.None);
        var sourceFile = fixture.CreateSourceFile("verify.bin", "Integrity baseline");
        var evidenceItem = await vault.ImportEvidenceFileAsync(caseInfo, sourceFile, null, CancellationToken.None);

        var (okBeforeTamper, _) = await vault.VerifyEvidenceAsync(
            caseInfo,
            evidenceItem,
            null,
            CancellationToken.None
        );
        Assert.True(okBeforeTamper);

        var storedPath = fixture.ResolveCasePath(caseInfo.CaseId, evidenceItem.StoredRelativePath);
        await File.AppendAllTextAsync(storedPath, "tampered");

        var (okAfterTamper, _) = await vault.VerifyEvidenceAsync(
            caseInfo,
            evidenceItem,
            null,
            CancellationToken.None
        );
        Assert.False(okAfterTamper);

        await using var db = await fixture.CreateDbContextAsync();
        var okAudit = await db.AuditEvents
            .Where(a => a.ActionType == "EvidenceVerifiedOk" && a.EvidenceItemId == evidenceItem.EvidenceItemId)
            .FirstOrDefaultAsync();
        var failAudit = await db.AuditEvents
            .Where(a => a.ActionType == "EvidenceVerifiedFail" && a.EvidenceItemId == evidenceItem.EvidenceItemId)
            .FirstOrDefaultAsync();

        Assert.NotNull(okAudit);
        Assert.NotNull(failAudit);
    }

    [Fact]
    public async Task EnqueueAsync_PersistsQueuedJobRecord()
    {
        await using var fixture = await WorkspaceFixture.CreateAsync(startRunner: false);
        var jobQueue = fixture.Services.GetRequiredService<IJobQueueService>();

        var payload = JsonSerializer.Serialize(
            new
            {
                SchemaVersion = 1,
                DelayMilliseconds = 5000
            }
        );

        var jobId = await jobQueue.EnqueueAsync(
            new JobEnqueueRequest(
                JobQueueService.TestLongRunningJobType,
                CaseId: null,
                EvidenceItemId: null,
                payload
            ),
            CancellationToken.None
        );

        await using var db = await fixture.CreateDbContextAsync();
        var record = await db.Jobs.FirstOrDefaultAsync(job => job.JobId == jobId);
        Assert.NotNull(record);
        Assert.Equal("Queued", record.Status);
        Assert.Equal(JobQueueService.TestLongRunningJobType, record.JobType);
        Assert.Equal(Environment.UserName, record.Operator);
        Assert.False(string.IsNullOrWhiteSpace(record.CorrelationId));
    }

    [Fact]
    public async Task Runner_ExecutesEvidenceVerifyJob_SucceedsThenFailsAfterTamper()
    {
        await using var fixture = await WorkspaceFixture.CreateAsync(startRunner: true);
        var workspace = fixture.Services.GetRequiredService<ICaseWorkspaceService>();
        var vault = fixture.Services.GetRequiredService<IEvidenceVaultService>();
        var jobQueue = fixture.Services.GetRequiredService<IJobQueueService>();

        var caseInfo = await workspace.CreateCaseAsync("Runner Verify Case", CancellationToken.None);
        var sourceFile = fixture.CreateSourceFile("runner-verify.bin", "Verification baseline");
        var evidenceItem = await vault.ImportEvidenceFileAsync(caseInfo, sourceFile, null, CancellationToken.None);

        var successJobId = await jobQueue.EnqueueAsync(
            new JobEnqueueRequest(
                JobQueueService.EvidenceVerifyJobType,
                caseInfo.CaseId,
                evidenceItem.EvidenceItemId,
                JsonSerializer.Serialize(
                    new
                    {
                        SchemaVersion = 1,
                        caseInfo.CaseId,
                        evidenceItem.EvidenceItemId
                    }
                )
            ),
            CancellationToken.None
        );

        var succeeded = await WaitForJobStatusAsync(
            fixture,
            successJobId,
            status => status == "Succeeded",
            TimeSpan.FromSeconds(10)
        );
        Assert.Equal("Succeeded", succeeded.Status);

        var storedPath = fixture.ResolveCasePath(caseInfo.CaseId, evidenceItem.StoredRelativePath);
        await File.AppendAllTextAsync(storedPath, "tampered");

        var failJobId = await jobQueue.EnqueueAsync(
            new JobEnqueueRequest(
                JobQueueService.EvidenceVerifyJobType,
                caseInfo.CaseId,
                evidenceItem.EvidenceItemId,
                JsonSerializer.Serialize(
                    new
                    {
                        SchemaVersion = 1,
                        caseInfo.CaseId,
                        evidenceItem.EvidenceItemId
                    }
                )
            ),
            CancellationToken.None
        );

        var failed = await WaitForJobStatusAsync(
            fixture,
            failJobId,
            status => status == "Failed",
            TimeSpan.FromSeconds(10)
        );
        Assert.Equal("Failed", failed.Status);
        Assert.Contains("mismatch", failed.StatusMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CancelAsync_LongRunningJob_TransitionsToCanceled()
    {
        await using var fixture = await WorkspaceFixture.CreateAsync(startRunner: true);
        var jobQueue = fixture.Services.GetRequiredService<IJobQueueService>();

        var longRunningJobId = await jobQueue.EnqueueAsync(
            new JobEnqueueRequest(
                JobQueueService.TestLongRunningJobType,
                CaseId: null,
                EvidenceItemId: null,
                JsonSerializer.Serialize(
                    new
                    {
                        SchemaVersion = 1,
                        DelayMilliseconds = 15000
                    }
                )
            ),
            CancellationToken.None
        );

        await WaitForJobStatusAsync(
            fixture,
            longRunningJobId,
            status => status == "Running",
            TimeSpan.FromSeconds(10)
        );

        await jobQueue.CancelAsync(longRunningJobId, CancellationToken.None);

        var canceled = await WaitForJobStatusAsync(
            fixture,
            longRunningJobId,
            status => status == "Canceled",
            TimeSpan.FromSeconds(10)
        );

        Assert.Equal("Canceled", canceled.Status);
    }

    private static async Task<JobRecord> WaitForJobStatusAsync(
        WorkspaceFixture fixture,
        Guid jobId,
        Func<string, bool> statusPredicate,
        TimeSpan timeout
    )
    {
        var timer = Stopwatch.StartNew();
        while (timer.Elapsed < timeout)
        {
            await using var db = await fixture.CreateDbContextAsync();
            var record = await db.Jobs
                .AsNoTracking()
                .FirstOrDefaultAsync(job => job.JobId == jobId);

            if (record is not null && statusPredicate(record.Status))
            {
                return record;
            }

            await Task.Delay(50);
        }

        throw new TimeoutException(
            $"Job {jobId:D} did not reach expected state within {timeout.TotalSeconds:0.##} seconds."
        );
    }

    private static async Task<string> ComputeSha256Async(string filePath)
    {
        await using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var sha256 = SHA256.Create();
        var hashBytes = await sha256.ComputeHashAsync(stream);
        return Convert.ToHexString(hashBytes).ToLowerInvariant();
    }

    private sealed class WorkspaceFixture : IAsyncDisposable
    {
        private readonly ServiceProvider _provider;
        private readonly TestWorkspacePathProvider _pathProvider;
        private readonly JobRunnerHostedService? _jobRunner;

        private WorkspaceFixture(
            ServiceProvider provider,
            TestWorkspacePathProvider pathProvider,
            JobRunnerHostedService? jobRunner
        )
        {
            _provider = provider;
            _pathProvider = pathProvider;
            _jobRunner = jobRunner;
        }

        public IServiceProvider Services => _provider;

        public static async Task<WorkspaceFixture> CreateAsync(bool startRunner = false)
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
                new FixedClock(new DateTimeOffset(2026, 2, 13, 12, 0, 0, TimeSpan.Zero))
            );
            services.AddSingleton<IWorkspacePathProvider>(pathProvider);
            services.AddDbContextFactory<WorkspaceDbContext>(options =>
            {
                Directory.CreateDirectory(pathProvider.WorkspaceRoot);
                options.UseSqlite($"Data Source={pathProvider.WorkspaceDbPath}");
            });
            services.AddSingleton<WorkspaceDbRebuilder>();
            services.AddSingleton<WorkspaceDbInitializer>();
            services.AddSingleton<IWorkspaceDbInitializer>(provider => provider.GetRequiredService<WorkspaceDbInitializer>());
            services.AddSingleton<IWorkspaceDatabaseInitializer>(provider => provider.GetRequiredService<WorkspaceDbInitializer>());
            services.AddSingleton<IAuditLogService, AuditLogService>();
            services.AddSingleton<ICaseWorkspaceService, CaseWorkspaceService>();
            services.AddSingleton<IEvidenceVaultService, EvidenceVaultService>();
            services.AddSingleton<IMessageSearchService, MessageSearchService>();
            services.AddSingleton<IMessageIngestService, MessageIngestService>();
            services.AddSingleton<JobQueueService>();
            services.AddSingleton<IJobQueueService>(
                provider => provider.GetRequiredService<JobQueueService>()
            );

            var provider = services.BuildServiceProvider();

            var initializer = provider.GetRequiredService<IWorkspaceDatabaseInitializer>();
            await initializer.EnsureInitializedAsync(CancellationToken.None);

            JobRunnerHostedService? runner = null;
            if (startRunner)
            {
                runner = ActivatorUtilities.CreateInstance<JobRunnerHostedService>(provider);
                await runner.StartAsync(CancellationToken.None);
            }

            return new WorkspaceFixture(provider, pathProvider, runner);
        }

        public string CreateSourceFile(string fileName, string content)
        {
            var sourceDirectory = Path.Combine(_pathProvider.WorkspaceRoot, "source");
            Directory.CreateDirectory(sourceDirectory);

            var path = Path.Combine(sourceDirectory, fileName);
            File.WriteAllText(path, content, Encoding.UTF8);
            return path;
        }

        public string ResolveCasePath(Guid caseId, string relativePath)
        {
            var root = Path.Combine(_pathProvider.CasesRoot, caseId.ToString("D"));
            return Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar));
        }

        public Task<WorkspaceDbContext> CreateDbContextAsync()
        {
            var factory = _provider.GetRequiredService<IDbContextFactory<WorkspaceDbContext>>();
            return factory.CreateDbContextAsync(CancellationToken.None);
        }

        public ValueTask DisposeAsync()
        {
            return DisposeInternalAsync();
        }

        private async ValueTask DisposeInternalAsync()
        {
            if (_jobRunner is not null)
            {
                using var stopCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                await _jobRunner.StopAsync(stopCts.Token);
                _jobRunner.Dispose();
            }

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
