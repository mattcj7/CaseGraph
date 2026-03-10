using CaseGraph.App.Services;
using CaseGraph.Core.Abstractions;
using CaseGraph.Infrastructure.Persistence;
using CaseGraph.Infrastructure.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace CaseGraph.Infrastructure.Tests;

public sealed class SearchReadinessTests
{
    [Fact]
    public async Task SearchReadiness_CurrentIndex_CompletesWithoutPendingMaintenance()
    {
        await using var fixture = await ReadinessFixture.CreateAsync();
        var service = fixture.Services.GetRequiredService<IFeatureReadinessService>();
        var initializer = fixture.Services.GetRequiredService<WorkspaceDbInitializer>();

        await initializer.InitializeAsync(CancellationToken.None);
        var messageEventId = await SeedMessageWithMissingFtsRowAsync(fixture.PathProvider.WorkspaceDbPath);
        await initializer.EnsureMessageSearchReadyAsync(CancellationToken.None);

        var result = await service.EnsureReadyAsync(
            ReadinessFeature.Search,
            Guid.NewGuid(),
            requiresMessageSearchIndex: true,
            progress: null,
            ct: CancellationToken.None
        );

        await using var connection = new SqliteConnection(
            $"Data Source={fixture.PathProvider.WorkspaceDbPath}"
        );
        await connection.OpenAsync();

        Assert.True(result.IsReady);
        Assert.False(result.IsPreparing);
        Assert.False(result.WorkPerformed);
        Assert.Null(result.PendingWork);
        Assert.Equal(1, await CountFtsRowsAsync(connection, messageEventId));
    }

    [Fact]
    public async Task SearchReadiness_StaleIndex_ReturnsPreparingWithoutBlockingCaller()
    {
        await using var fixture = await ReadinessFixture.CreateAsync();
        var service = fixture.Services.GetRequiredService<IFeatureReadinessService>();
        var initializer = fixture.Services.GetRequiredService<WorkspaceDbInitializer>();

        await initializer.InitializeAsync(CancellationToken.None);
        var messageEventId = await SeedMessageWithMissingFtsRowAsync(fixture.PathProvider.WorkspaceDbPath);
        var readinessTask = service.EnsureReadyAsync(
            ReadinessFeature.Search,
            Guid.NewGuid(),
            requiresMessageSearchIndex: true,
            progress: null,
            ct: CancellationToken.None
        );
        var completedTask = await Task.WhenAny(readinessTask, Task.Delay(500));
        Assert.Same(readinessTask, completedTask);

        var readinessResult = await readinessTask;
        Assert.False(readinessResult.IsReady);
        Assert.True(readinessResult.IsPreparing);
        Assert.NotNull(readinessResult.PendingWork);

        await readinessResult.PendingWork!;

        await using var verificationConnection = new SqliteConnection(
            $"Data Source={fixture.PathProvider.WorkspaceDbPath}"
        );
        await verificationConnection.OpenAsync();
        Assert.Equal(1, await CountFtsRowsAsync(verificationConnection, messageEventId));
    }

    private static async Task<Guid> SeedMessageWithMissingFtsRowAsync(string workspaceDbPath)
    {
        var caseId = Guid.NewGuid();
        var evidenceItemId = Guid.NewGuid();
        var threadId = Guid.NewGuid();
        var messageEventId = Guid.NewGuid();

        await using var connection = new SqliteConnection($"Data Source={workspaceDbPath}");
        await connection.OpenAsync();

        await ExecuteAsync(
            connection,
            """
            INSERT INTO CaseRecord (CaseId, Name, CreatedAtUtc, LastOpenedAtUtc)
            VALUES ($caseId, $name, $createdAtUtc, NULL);
            """,
            ("$caseId", caseId),
            ("$name", "Readiness Test Case"),
            ("$createdAtUtc", "2026-03-08T12:00:00.0000000Z")
        );
        await ExecuteAsync(
            connection,
            """
            INSERT INTO EvidenceItemRecord (
                EvidenceItemId,
                CaseId,
                DisplayName,
                OriginalPath,
                OriginalFileName,
                AddedAtUtc,
                SizeBytes,
                Sha256Hex,
                FileExtension,
                SourceType,
                ManifestRelativePath,
                StoredRelativePath
            )
            VALUES (
                $evidenceItemId,
                $caseId,
                $displayName,
                $originalPath,
                $originalFileName,
                $addedAtUtc,
                $sizeBytes,
                $sha256Hex,
                $fileExtension,
                $sourceType,
                $manifestRelativePath,
                $storedRelativePath
            );
            """,
            ("$evidenceItemId", evidenceItemId),
            ("$caseId", caseId),
            ("$displayName", "messages.xlsx"),
            ("$originalPath", @"C:\input\messages.xlsx"),
            ("$originalFileName", "messages.xlsx"),
            ("$addedAtUtc", "2026-03-08T12:00:00.0000000Z"),
            ("$sizeBytes", 128L),
            ("$sha256Hex", "abc123"),
            ("$fileExtension", ".xlsx"),
            ("$sourceType", "XLSX"),
            ("$manifestRelativePath", "vault/manifest.json"),
            ("$storedRelativePath", "vault/messages.xlsx")
        );
        await ExecuteAsync(
            connection,
            """
            INSERT INTO MessageThreadRecord (
                ThreadId,
                CaseId,
                EvidenceItemId,
                Platform,
                ThreadKey,
                Title,
                CreatedAtUtc,
                SourceLocator,
                IngestModuleVersion
            )
            VALUES (
                $threadId,
                $caseId,
                $evidenceItemId,
                $platform,
                $threadKey,
                NULL,
                $createdAtUtc,
                $sourceLocator,
                $ingestModuleVersion
            );
            """,
            ("$threadId", threadId),
            ("$caseId", caseId),
            ("$evidenceItemId", evidenceItemId),
            ("$platform", "SMS"),
            ("$threadKey", "thread-1"),
            ("$createdAtUtc", "2026-03-08T12:00:00.0000000Z"),
            ("$sourceLocator", "sheet:messages!A1"),
            ("$ingestModuleVersion", "test")
        );
        await ExecuteAsync(
            connection,
            """
            INSERT INTO MessageEventRecord (
                MessageEventId,
                ThreadId,
                CaseId,
                EvidenceItemId,
                Platform,
                TimestampUtc,
                Direction,
                Sender,
                Recipients,
                Body,
                IsDeleted,
                SourceLocator,
                IngestModuleVersion
            )
            VALUES (
                $messageEventId,
                $threadId,
                $caseId,
                $evidenceItemId,
                $platform,
                $timestampUtc,
                $direction,
                $sender,
                $recipients,
                $body,
                0,
                $sourceLocator,
                $ingestModuleVersion
            );
            """,
            ("$messageEventId", messageEventId),
            ("$threadId", threadId),
            ("$caseId", caseId),
            ("$evidenceItemId", evidenceItemId),
            ("$platform", "SMS"),
            ("$timestampUtc", "2026-03-08T12:05:00.0000000Z"),
            ("$direction", "Incoming"),
            ("$sender", "+15555550100"),
            ("$recipients", "+15555550101"),
            ("$body", "hello world"),
            ("$sourceLocator", "sheet:messages!A2"),
            ("$ingestModuleVersion", "test")
        );
        await ExecuteAsync(
            connection,
            "DELETE FROM MessageEventFts WHERE MessageEventId = $messageEventId;",
            ("$messageEventId", messageEventId)
        );

        return messageEventId;
    }

    private static async Task<int> CountFtsRowsAsync(
        SqliteConnection connection,
        Guid messageEventId
    )
    {
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT COUNT(1)
            FROM MessageEventFts
            WHERE MessageEventId = $messageEventId;
            """;
        command.Parameters.AddWithValue("$messageEventId", messageEventId);
        var value = await command.ExecuteScalarAsync();
        return value is null or DBNull ? 0 : Convert.ToInt32(value);
    }

    private static async Task ExecuteAsync(
        SqliteConnection connection,
        string sql,
        params (string Name, object Value)[] parameters
    )
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        await command.ExecuteNonQueryAsync();
    }

    private sealed class ReadinessFixture : IAsyncDisposable
    {
        private readonly ServiceProvider _provider;

        private ReadinessFixture(ServiceProvider provider, TestWorkspacePathProvider pathProvider)
        {
            _provider = provider;
            PathProvider = pathProvider;
        }

        public IServiceProvider Services => _provider;

        public TestWorkspacePathProvider PathProvider { get; }

        public static Task<ReadinessFixture> CreateAsync()
        {
            var workspaceRoot = Path.Combine(
                Path.GetTempPath(),
                "CaseGraph.SearchReadinessTests",
                Guid.NewGuid().ToString("N")
            );
            Directory.CreateDirectory(workspaceRoot);

            var pathProvider = new TestWorkspacePathProvider(workspaceRoot);
            var services = new ServiceCollection();
            services.AddSingleton<IClock>(
                new FixedClock(new DateTimeOffset(2026, 3, 8, 12, 0, 0, TimeSpan.Zero))
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
            services.AddSingleton<IEvidenceVaultService, EvidenceVaultService>();
            services.AddSingleton<IMessageSearchService, MessageSearchService>();
            services.AddSingleton<IMessageIngestService, MessageIngestService>();
            services.AddSingleton<IJobQueryService, JobQueryService>();
            services.AddSingleton<JobQueueService>();
            services.AddSingleton<IJobQueueService>(
                provider => provider.GetRequiredService<JobQueueService>()
            );
            services.AddSingleton<IBackgroundMaintenanceManager, BackgroundMaintenanceManager>();
            services.AddSingleton<IFeatureReadinessService, FeatureReadinessService>();

            return Task.FromResult(new ReadinessFixture(services.BuildServiceProvider(), pathProvider));
        }

        public async ValueTask DisposeAsync()
        {
            await _provider.DisposeAsync();
            SqliteConnection.ClearAllPools();

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
                catch (IOException) when (attempt < 5)
                {
                    await Task.Delay(50);
                }
                catch (UnauthorizedAccessException) when (attempt < 5)
                {
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
