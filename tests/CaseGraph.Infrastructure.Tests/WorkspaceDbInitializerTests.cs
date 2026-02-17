using CaseGraph.Core.Abstractions;
using CaseGraph.Infrastructure.Persistence;
using CaseGraph.Infrastructure.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using System.Text.Json;

namespace CaseGraph.Infrastructure.Tests;

public sealed class WorkspaceDbInitializerTests
{
    [Fact]
    public async Task InitializeAsync_LegacyDb_BacksUpAndRecreatesWithJobTable()
    {
        await using var fixture = await WorkspaceFixture.CreateAsync();
        await fixture.CreateLegacyDatabaseAsync();

        var initializer = fixture.Services.GetRequiredService<IWorkspaceDbInitializer>();
        await initializer.InitializeAsync(CancellationToken.None);

        var backupFiles = Directory.GetFiles(
            fixture.PathProvider.WorkspaceRoot,
            "workspace.broken.*.db",
            SearchOption.TopDirectoryOnly
        );
        Assert.NotEmpty(backupFiles);
        Assert.True(File.Exists(fixture.PathProvider.WorkspaceDbPath));

        await using var connection = new SqliteConnection(
            $"Data Source={fixture.PathProvider.WorkspaceDbPath}"
        );
        await connection.OpenAsync();

        Assert.True(await TableExistsAsync(connection, "JobRecord"));
        Assert.True(await TableExistsAsync(connection, "__EFMigrationsHistory"));
    }

    [Fact]
    public async Task InitializeAsync_EmptyPath_AppliesRequiredTables()
    {
        await using var fixture = await WorkspaceFixture.CreateAsync();

        var initializer = fixture.Services.GetRequiredService<IWorkspaceDbInitializer>();
        await initializer.InitializeAsync(CancellationToken.None);

        await using var connection = new SqliteConnection(
            $"Data Source={fixture.PathProvider.WorkspaceDbPath}"
        );
        await connection.OpenAsync();

        Assert.True(await TableExistsAsync(connection, "CaseRecord"));
        Assert.True(await TableExistsAsync(connection, "EvidenceItemRecord"));
        Assert.True(await TableExistsAsync(connection, "AuditEventRecord"));
        Assert.True(await TableExistsAsync(connection, "JobRecord"));
    }

    [Fact]
    public async Task InitializeAsync_LegacyPath_RebuildsEvidenceFromManifest()
    {
        await using var fixture = await WorkspaceFixture.CreateAsync();
        await fixture.CreateLegacyDatabaseAsync();

        var caseId = Guid.NewGuid();
        var evidenceItemId = Guid.NewGuid();
        var caseDirectory = Path.Combine(fixture.PathProvider.CasesRoot, caseId.ToString("D"));
        var manifestDirectory = Path.Combine(
            caseDirectory,
            "vault",
            evidenceItemId.ToString("D")
        );
        var originalDirectory = Path.Combine(manifestDirectory, "original");
        Directory.CreateDirectory(originalDirectory);

        var originalFileName = "messages.xlsx";
        var originalFilePath = Path.Combine(originalDirectory, originalFileName);
        await File.WriteAllTextAsync(originalFilePath, "placeholder");

        var manifestPath = Path.Combine(manifestDirectory, "manifest.json");
        var manifest = new
        {
            SchemaVersion = 1,
            EvidenceItemId = evidenceItemId,
            CaseId = caseId,
            AddedAtUtc = "2026-02-13T15:00:00Z",
            Operator = "tester",
            OriginalPath = @"C:\input\messages.xlsx",
            OriginalFileName = originalFileName,
            StoredRelativePath = $"vault/{evidenceItemId:D}/original/{originalFileName}",
            SizeBytes = 1234,
            Sha256Hex = "abc123",
            FileExtension = ".xlsx",
            SourceType = "XLSX"
        };
        await File.WriteAllTextAsync(manifestPath, JsonSerializer.Serialize(manifest));

        var initializer = fixture.Services.GetRequiredService<IWorkspaceDbInitializer>();
        await initializer.InitializeAsync(CancellationToken.None);

        await using var db = await fixture.CreateDbContextAsync();
        var rebuiltCase = await db.Cases.AsNoTracking().FirstOrDefaultAsync(c => c.CaseId == caseId);
        Assert.NotNull(rebuiltCase);
        Assert.StartsWith("Recovered Case", rebuiltCase.Name);

        var rebuiltEvidence = await db.EvidenceItems.AsNoTracking()
            .FirstOrDefaultAsync(e => e.EvidenceItemId == evidenceItemId);
        Assert.NotNull(rebuiltEvidence);
        Assert.Equal(caseId, rebuiltEvidence.CaseId);
        Assert.Equal(".xlsx", rebuiltEvidence.FileExtension);
        Assert.Equal("XLSX", rebuiltEvidence.SourceType);
        Assert.Equal($"vault/{evidenceItemId:D}/original/{originalFileName}", rebuiltEvidence.StoredRelativePath);
    }

    [Fact]
    public async Task JobRunnerHostedService_LegacyDb_RepairsBeforeQueuePriming()
    {
        await using var fixture = await WorkspaceFixture.CreateAsync();
        await fixture.CreateLegacyDatabaseAsync();

        var runner = ActivatorUtilities.CreateInstance<JobRunnerHostedService>(
            (IServiceProvider)fixture.Services
        );

        try
        {
            await runner.StartAsync(CancellationToken.None);
            await Task.Delay(200);
        }
        finally
        {
            using var stopCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await runner.StopAsync(stopCts.Token);
            runner.Dispose();
        }

        var backupFiles = Directory.GetFiles(
            fixture.PathProvider.WorkspaceRoot,
            "workspace.broken.*.db",
            SearchOption.TopDirectoryOnly
        );
        Assert.NotEmpty(backupFiles);

        await using var connection = new SqliteConnection(
            $"Data Source={fixture.PathProvider.WorkspaceDbPath}"
        );
        await connection.OpenAsync();
        Assert.True(await TableExistsAsync(connection, "JobRecord"));
    }

    private static async Task<bool> TableExistsAsync(SqliteConnection connection, string tableName)
    {
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT 1
            FROM sqlite_master
            WHERE type = 'table'
              AND name = $name
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("$name", tableName);
        var value = await command.ExecuteScalarAsync();
        return value is not null;
    }

    private sealed class WorkspaceFixture : IAsyncDisposable
    {
        private readonly ServiceProvider _provider;

        private WorkspaceFixture(ServiceProvider provider, TestWorkspacePathProvider pathProvider)
        {
            _provider = provider;
            PathProvider = pathProvider;
        }

        public IServiceProvider Services => _provider;

        public TestWorkspacePathProvider PathProvider { get; }

        public static Task<WorkspaceFixture> CreateAsync()
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
            services.AddSingleton<IWorkspaceDbInitializer>(
                provider => provider.GetRequiredService<WorkspaceDbInitializer>()
            );
            services.AddSingleton<IWorkspaceDatabaseInitializer>(
                provider => provider.GetRequiredService<WorkspaceDbInitializer>()
            );
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

            var provider = services.BuildServiceProvider();
            return Task.FromResult(new WorkspaceFixture(provider, pathProvider));
        }

        public async Task CreateLegacyDatabaseAsync()
        {
            Directory.CreateDirectory(PathProvider.WorkspaceRoot);
            await using var connection = new SqliteConnection(
                $"Data Source={PathProvider.WorkspaceDbPath}"
            );
            await connection.OpenAsync();

            await using var command = connection.CreateCommand();
            command.CommandText =
                """
                CREATE TABLE IF NOT EXISTS CaseRecord (
                    CaseId TEXT NOT NULL CONSTRAINT PK_CaseRecord PRIMARY KEY,
                    Name TEXT NOT NULL
                );
                """;
            await command.ExecuteNonQueryAsync();
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

    public sealed class TestWorkspacePathProvider : IWorkspacePathProvider
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
