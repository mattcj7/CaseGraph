using CaseGraph.Core.Abstractions;
using CaseGraph.Core.Models;
using CaseGraph.Infrastructure.Persistence;
using CaseGraph.Infrastructure.Persistence.Entities;
using CaseGraph.Infrastructure.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using System.Text.Json;

namespace CaseGraph.Infrastructure.Tests;

public sealed class SqliteLockResilienceTests
{
    [Fact]
    public async Task WorkspaceWriteGate_RunAsync_SerializesConcurrentWriters()
    {
        var gate = new WorkspaceWriteGate();
        var firstEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirst = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var order = new List<int>();

        var firstTask = gate.RunAsync(
            async _ =>
            {
                order.Add(1);
                firstEntered.TrySetResult();
                await releaseFirst.Task;
            },
            CancellationToken.None
        );

        await firstEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));

        var secondTask = gate.RunAsync(
            _ =>
            {
                order.Add(2);
                secondEntered.TrySetResult();
                return Task.CompletedTask;
            },
            CancellationToken.None
        );

        await Assert.ThrowsAsync<TimeoutException>(
            () => secondEntered.Task.WaitAsync(TimeSpan.FromMilliseconds(200))
        );

        releaseFirst.TrySetResult();
        await Task.WhenAll(firstTask, secondTask);

        Assert.Equal([1, 2], order);
    }

    [Fact]
    public async Task SqliteWriteRetryPolicy_WhenBusyThenUnlocked_RetriesAndSucceeds()
    {
        var attemptCount = 0;

        await SqliteWriteRetryPolicy.ExecuteAsync(
            _ =>
            {
                attemptCount++;
                if (attemptCount <= 3)
                {
                    throw new SqliteException("database is locked", 6);
                }

                return Task.CompletedTask;
            },
            CancellationToken.None,
            maxRetries: 4,
            retryDelaySelector: _ => TimeSpan.Zero
        );

        Assert.Equal(4, attemptCount);
    }

    [Fact]
    public async Task JobRunnerHostedService_DoesNotBeginProcessingUntilWorkspaceInitCompletes()
    {
        await using var fixture = await RunnerGateFixture.CreateAsync();
        var runner = ActivatorUtilities.CreateInstance<JobRunnerHostedService>(
            (IServiceProvider)fixture.Services
        );

        try
        {
            await runner.StartAsync(CancellationToken.None);
            await fixture.BlockingInitializer.InitializationRequested.WaitAsync(
                TimeSpan.FromSeconds(2)
            );

            await Task.Delay(200);
            await using (var db = await fixture.CreateDbContextAsync())
            {
                var queued = await db.Jobs
                    .AsNoTracking()
                    .FirstAsync(job => job.JobId == fixture.QueuedJobId);
                Assert.Equal("Queued", queued.Status);
            }

            fixture.BlockingInitializer.ReleaseInitialization();

            var succeeded = await WaitForJobStatusAsync(
                fixture,
                fixture.QueuedJobId,
                expectedStatus: "Succeeded",
                timeout: TimeSpan.FromSeconds(10)
            );

            Assert.Equal("Succeeded", succeeded.Status);
        }
        finally
        {
            using var stopCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await runner.StopAsync(stopCts.Token);
            runner.Dispose();
        }
    }

    private static async Task<JobRecord> WaitForJobStatusAsync(
        RunnerGateFixture fixture,
        Guid jobId,
        string expectedStatus,
        TimeSpan timeout
    )
    {
        var startedAt = DateTimeOffset.UtcNow;
        while (DateTimeOffset.UtcNow - startedAt < timeout)
        {
            await using var db = await fixture.CreateDbContextAsync();
            var record = await db.Jobs
                .AsNoTracking()
                .FirstOrDefaultAsync(job => job.JobId == jobId);

            if (record is not null && string.Equals(record.Status, expectedStatus, StringComparison.Ordinal))
            {
                return record;
            }

            await Task.Delay(50);
        }

        throw new TimeoutException(
            $"Job {jobId:D} did not reach {expectedStatus} within {timeout.TotalSeconds:0.##}s."
        );
    }

    private sealed class RunnerGateFixture : IAsyncDisposable
    {
        private readonly ServiceProvider _provider;

        private RunnerGateFixture(
            ServiceProvider provider,
            TestWorkspacePathProvider pathProvider,
            BlockingWorkspaceInitializer blockingInitializer,
            Guid queuedJobId
        )
        {
            _provider = provider;
            PathProvider = pathProvider;
            BlockingInitializer = blockingInitializer;
            QueuedJobId = queuedJobId;
        }

        public IServiceProvider Services => _provider;

        public TestWorkspacePathProvider PathProvider { get; }

        public BlockingWorkspaceInitializer BlockingInitializer { get; }

        public Guid QueuedJobId { get; }

        public static async Task<RunnerGateFixture> CreateAsync()
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
                new FixedClock(new DateTimeOffset(2026, 2, 22, 12, 0, 0, TimeSpan.Zero))
            );
            services.AddSingleton<IWorkspacePathProvider>(pathProvider);
            services.AddDbContextFactory<WorkspaceDbContext>(options =>
            {
                Directory.CreateDirectory(pathProvider.WorkspaceRoot);
                options.UseSqlite($"Data Source={pathProvider.WorkspaceDbPath}");
            });

            services.AddSingleton<WorkspaceDbRebuilder>();
            services.AddSingleton<WorkspaceDbInitializer>();
            services.AddSingleton<BlockingWorkspaceInitializer>(provider =>
                new BlockingWorkspaceInitializer(provider.GetRequiredService<WorkspaceDbInitializer>())
            );
            services.AddSingleton<IWorkspaceDbInitializer>(provider =>
                provider.GetRequiredService<BlockingWorkspaceInitializer>()
            );
            services.AddSingleton<IWorkspaceDatabaseInitializer>(provider =>
                provider.GetRequiredService<BlockingWorkspaceInitializer>()
            );
            services.AddSingleton<IWorkspaceWriteGate, WorkspaceWriteGate>();
            services.AddSingleton<IAuditLogService, AuditLogService>();
            services.AddSingleton<ICaseWorkspaceService, CaseWorkspaceService>();
            services.AddSingleton<IEvidenceVaultService, EvidenceVaultService>();
            services.AddSingleton<IMessageSearchService, MessageSearchService>();
            services.AddSingleton<IMessageIngestService, MessageIngestService>();
            services.AddSingleton<IJobQueryService, JobQueryService>();
            services.AddSingleton<JobQueueService>();
            services.AddSingleton<IJobQueueService>(provider =>
                provider.GetRequiredService<JobQueueService>()
            );

            var provider = services.BuildServiceProvider();
            var realInitializer = provider.GetRequiredService<WorkspaceDbInitializer>();
            await realInitializer.InitializeAsync(CancellationToken.None);

            var queuedJobId = Guid.NewGuid();
            await using (var db = await provider.GetRequiredService<IDbContextFactory<WorkspaceDbContext>>()
                .CreateDbContextAsync(CancellationToken.None))
            {
                db.Jobs.Add(new JobRecord
                {
                    JobId = queuedJobId,
                    CreatedAtUtc = DateTimeOffset.UtcNow,
                    Status = JobStatus.Queued.ToString(),
                    JobType = JobQueueService.TestLongRunningJobType,
                    CaseId = null,
                    EvidenceItemId = null,
                    Progress = 0,
                    StatusMessage = "Queued.",
                    JsonPayload = JsonSerializer.Serialize(new
                    {
                        SchemaVersion = 1,
                        DelayMilliseconds = 100
                    }),
                    CorrelationId = Guid.NewGuid().ToString("N"),
                    Operator = Environment.UserName
                });
                await db.SaveChangesAsync(CancellationToken.None);
            }

            var blockingInitializer = provider.GetRequiredService<BlockingWorkspaceInitializer>();
            return new RunnerGateFixture(provider, pathProvider, blockingInitializer, queuedJobId);
        }

        public Task<WorkspaceDbContext> CreateDbContextAsync()
        {
            var factory = _provider.GetRequiredService<IDbContextFactory<WorkspaceDbContext>>();
            return factory.CreateDbContextAsync(CancellationToken.None);
        }

        public async ValueTask DisposeAsync()
        {
            await _provider.DisposeAsync();

            if (!Directory.Exists(PathProvider.WorkspaceRoot))
            {
                return;
            }

            for (var attempt = 1; attempt <= 5; attempt++)
            {
                try
                {
                    Directory.Delete(PathProvider.WorkspaceRoot, recursive: true);
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

    private sealed class BlockingWorkspaceInitializer : IWorkspaceDbInitializer, IWorkspaceDatabaseInitializer
    {
        private readonly WorkspaceDbInitializer _inner;
        private readonly TaskCompletionSource _initializationRequested = new(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        private readonly TaskCompletionSource _releaseInitialization = new(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        private int _firstCallSeen;

        public BlockingWorkspaceInitializer(WorkspaceDbInitializer inner)
        {
            _inner = inner;
        }

        public Task InitializationRequested => _initializationRequested.Task;

        public void ReleaseInitialization()
        {
            _releaseInitialization.TrySetResult();
        }

        public async Task InitializeAsync(CancellationToken ct)
        {
            if (Interlocked.CompareExchange(ref _firstCallSeen, 1, 0) == 0)
            {
                _initializationRequested.TrySetResult();
                await _releaseInitialization.Task.WaitAsync(ct);
            }

            await _inner.InitializeAsync(ct);
        }

        public Task EnsureUpgradedAsync(CancellationToken ct)
        {
            return _inner.EnsureUpgradedAsync(ct);
        }

        public Task EnsureInitializedAsync(CancellationToken ct)
        {
            return InitializeAsync(ct);
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
