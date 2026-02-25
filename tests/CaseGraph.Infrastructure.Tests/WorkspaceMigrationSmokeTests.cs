using CaseGraph.Core.Abstractions;
using CaseGraph.Infrastructure.Persistence;
using CaseGraph.Infrastructure.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace CaseGraph.Infrastructure.Tests;

public sealed class WorkspaceMigrationSmokeTests
{
    private static readonly Guid LegacyFixtureCaseId = Guid.Parse(
        "11111111-1111-1111-1111-111111111111"
    );

    [Fact]
    public async Task Workspace_Migrate_OldDb_UpgradesToLatest()
    {
        await using var fixture = await WorkspaceFixture.CreateFromOldDbFixtureAsync();

        var initializer = fixture.Services.GetRequiredService<IWorkspaceDbInitializer>();
        await initializer.InitializeAsync(CancellationToken.None);

        await using var db = await fixture.CreateDbContextAsync();
        var allMigrations = db.Database.GetMigrations().ToList();
        var latestMigration = allMigrations.Last();
        var appliedMigrations = await db.Database.GetAppliedMigrationsAsync();

        Assert.Contains(latestMigration, appliedMigrations);

        await using var connection = new SqliteConnection(
            $"Data Source={fixture.PathProvider.WorkspaceDbPath}"
        );
        await connection.OpenAsync();

        Assert.True(await TableExistsAsync(connection, "TargetRecord"));
        Assert.True(await TableExistsAsync(connection, "IdentifierRecord"));
        Assert.True(await TableExistsAsync(connection, "TargetAliasRecord"));
        Assert.True(await TableExistsAsync(connection, "TargetIdentifierLinkRecord"));
        Assert.True(await TableExistsAsync(connection, "MessageParticipantLinkRecord"));
        Assert.True(await TableExistsAsync(connection, "TargetMessagePresenceRecord"));
    }

    [Fact]
    public async Task Workspace_Open_OldDb_DoesNotThrow_WhenLoadingCase()
    {
        await using var fixture = await WorkspaceFixture.CreateFromOldDbFixtureAsync();
        var workspaceService = fixture.Services.GetRequiredService<ICaseWorkspaceService>();

        (CaseGraph.Core.Models.CaseInfo caseInfo, List<CaseGraph.Core.Models.EvidenceItem> evidence)? loaded = null;
        var exception = await Record.ExceptionAsync(async () =>
        {
            loaded = await workspaceService.LoadCaseAsync(LegacyFixtureCaseId, CancellationToken.None);
        });

        Assert.Null(exception);
        Assert.NotNull(loaded);
        Assert.Equal(LegacyFixtureCaseId, loaded.Value.caseInfo.CaseId);
        Assert.Equal("Legacy Fixture Case", loaded.Value.caseInfo.Name);
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

        public static Task<WorkspaceFixture> CreateFromOldDbFixtureAsync()
        {
            var workspaceRoot = Path.Combine(
                Path.GetTempPath(),
                "CaseGraph.Infrastructure.Tests",
                Guid.NewGuid().ToString("N")
            );
            Directory.CreateDirectory(workspaceRoot);

            var fixturePath = ResolveOldDbFixturePath();
            var workspaceDbPath = Path.Combine(workspaceRoot, "workspace.db");
            File.Copy(fixturePath, workspaceDbPath, overwrite: true);

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
            services.AddSingleton<IWorkspaceWriteGate, WorkspaceWriteGate>();
            services.AddSingleton<IAuditLogService, AuditLogService>();
            services.AddSingleton<IAuditQueryService, AuditQueryService>();
            services.AddSingleton<ICaseWorkspaceService, CaseWorkspaceService>();
            services.AddSingleton<ICaseQueryService, CaseQueryService>();

            var provider = services.BuildServiceProvider();
            return Task.FromResult(new WorkspaceFixture(provider, pathProvider));
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

        private static string ResolveOldDbFixturePath()
        {
            var directory = new DirectoryInfo(AppContext.BaseDirectory);
            while (directory is not null)
            {
                var solutionPath = Path.Combine(directory.FullName, "CaseGraph.sln");
                if (File.Exists(solutionPath))
                {
                    var fixturePath = Path.Combine(
                        directory.FullName,
                        "tests",
                        "Fixtures",
                        "workspace-old-initial.db"
                    );
                    if (File.Exists(fixturePath))
                    {
                        return fixturePath;
                    }

                    throw new FileNotFoundException(
                        $"Old DB fixture was not found at \"{fixturePath}\"."
                    );
                }

                directory = directory.Parent;
            }

            throw new DirectoryNotFoundException(
                "Unable to locate solution root while resolving old DB fixture."
            );
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
