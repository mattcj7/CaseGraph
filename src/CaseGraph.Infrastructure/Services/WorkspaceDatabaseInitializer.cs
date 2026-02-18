using CaseGraph.Core.Abstractions;
using CaseGraph.Infrastructure.Persistence;
using CaseGraph.Infrastructure.Persistence.Entities;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using System.Text.Json;

namespace CaseGraph.Infrastructure.Services;

public class WorkspaceDbInitializer : IWorkspaceDbInitializer, IWorkspaceDatabaseInitializer
{
    private static readonly string[] RequiredTableNames =
    {
        "CaseRecord",
        "EvidenceItemRecord",
        "AuditEventRecord",
        "JobRecord",
        "TargetRecord",
        "TargetAliasRecord",
        "IdentifierRecord",
        "TargetIdentifierLinkRecord",
        "MessageParticipantLinkRecord"
    };

    private readonly IDbContextFactory<WorkspaceDbContext> _dbContextFactory;
    private readonly IWorkspacePathProvider _workspacePathProvider;
    private readonly WorkspaceDbRebuilder _workspaceDbRebuilder;
    private readonly IClock _clock;
    private readonly SemaphoreSlim _semaphore = new(1, 1);
    private bool _initialized;
    private string? _initializedWorkspaceDbPath;

    public WorkspaceDbInitializer(
        IDbContextFactory<WorkspaceDbContext> dbContextFactory,
        IWorkspacePathProvider workspacePathProvider,
        WorkspaceDbRebuilder workspaceDbRebuilder,
        IClock clock
    )
    {
        _dbContextFactory = dbContextFactory;
        _workspacePathProvider = workspacePathProvider;
        _workspaceDbRebuilder = workspaceDbRebuilder;
        _clock = clock;
    }

    public Task EnsureInitializedAsync(CancellationToken ct)
    {
        return InitializeAsync(ct);
    }

    public async Task InitializeAsync(CancellationToken ct)
    {
        await _semaphore.WaitAsync(ct);
        try
        {
            var currentWorkspaceDbPath = _workspacePathProvider.WorkspaceDbPath;
            var sameWorkspacePath = string.Equals(
                _initializedWorkspaceDbPath,
                currentWorkspaceDbPath,
                StringComparison.OrdinalIgnoreCase
            );

            if (_initialized && sameWorkspacePath)
            {
                return;
            }

            Directory.CreateDirectory(_workspacePathProvider.WorkspaceRoot);
            Directory.CreateDirectory(_workspacePathProvider.CasesRoot);

            var inspection = await InspectDatabaseAsync(ct);
            if (!inspection.Exists)
            {
                await EnsureUpgradedAsync(ct);
            }
            else if (inspection.HasMigrationHistory)
            {
                // Existing migration-based DBs must be upgraded in-place.
                await EnsureUpgradedAsync(ct);
            }
            else if (inspection.IsBroken)
            {
                await RepairDatabaseAsync(ct);
            }
            else
            {
                await EnsureUpgradedAsync(ct);
            }

            var missingAfterUpgrade = await GetMissingRequiredTablesAsync(ct);
            if (missingAfterUpgrade.Count > 0)
            {
                var postUpgradeInspection = await InspectDatabaseAsync(ct);
                if (postUpgradeInspection.HasMigrationHistory)
                {
                    await RepairDatabaseAsync(ct);
                }
            }

            var missingRequiredTables = await GetMissingRequiredTablesAsync(ct);
            if (missingRequiredTables.Count > 0)
            {
                throw new InvalidOperationException(
                    $"Workspace DB is missing required tables: {string.Join(", ", missingRequiredTables)}"
                );
            }

            await MarkRunningJobsAsAbandonedAsync(ct);
            _initialized = true;
            _initializedWorkspaceDbPath = currentWorkspaceDbPath;
        }
        finally
        {
            _semaphore.Release();
        }
    }

    public async Task EnsureUpgradedAsync(CancellationToken ct)
    {
        await using var db = await _dbContextFactory.CreateDbContextAsync(ct);
        await db.Database.MigrateAsync(ct);
        await EnsureMessageFtsObjectsAsync(db, ct);
    }

    private async Task<DatabaseInspection> InspectDatabaseAsync(CancellationToken ct)
    {
        if (!File.Exists(_workspacePathProvider.WorkspaceDbPath))
        {
            return new DatabaseInspection(
                exists: false,
                hasMigrationHistory: false,
                missingRequiredTables: Array.Empty<string>()
            );
        }

        try
        {
            var tableNames = await ReadTableNamesAsync(ct);
            var hasMigrationHistory = tableNames.Contains("__EFMigrationsHistory");
            var missingRequiredTables = RequiredTableNames
                .Where(tableName => !tableNames.Contains(tableName))
                .ToArray();

            return new DatabaseInspection(
                exists: true,
                hasMigrationHistory: hasMigrationHistory,
                missingRequiredTables: missingRequiredTables
            );
        }
        catch (SqliteException)
        {
            return new DatabaseInspection(
                exists: true,
                hasMigrationHistory: false,
                missingRequiredTables: RequiredTableNames
            );
        }
    }

    private async Task<HashSet<string>> ReadTableNamesAsync(CancellationToken ct)
    {
        await using var connection = new SqliteConnection(
            $"Data Source={_workspacePathProvider.WorkspaceDbPath};Mode=ReadWrite"
        );
        await connection.OpenAsync(ct);

        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT name
            FROM sqlite_master
            WHERE type = 'table';
            """;

        var tableNames = new HashSet<string>(StringComparer.Ordinal);
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            if (reader.IsDBNull(0))
            {
                continue;
            }

            var tableName = reader.GetString(0);
            if (!string.IsNullOrWhiteSpace(tableName))
            {
                tableNames.Add(tableName);
            }
        }

        return tableNames;
    }

    private async Task<List<string>> GetMissingRequiredTablesAsync(CancellationToken ct)
    {
        if (!File.Exists(_workspacePathProvider.WorkspaceDbPath))
        {
            return RequiredTableNames.ToList();
        }

        var tableNames = await ReadTableNamesAsync(ct);
        return RequiredTableNames
            .Where(requiredTable => !tableNames.Contains(requiredTable))
            .ToList();
    }

    private async Task RepairDatabaseAsync(CancellationToken ct)
    {
        if (File.Exists(_workspacePathProvider.WorkspaceDbPath))
        {
            await BackupBrokenDatabaseAsync(ct);
        }

        await EnsureUpgradedAsync(ct);
        await _workspaceDbRebuilder.RebuildAsync(ct);
    }

    private async Task BackupBrokenDatabaseAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        SqliteConnection.ClearAllPools();

        var timestamp = _clock.UtcNow.ToUniversalTime().ToString("yyyyMMdd-HHmmss");
        var backupPath = Path.Combine(
            _workspacePathProvider.WorkspaceRoot,
            $"workspace.broken.{timestamp}.db"
        );

        var suffix = 1;
        while (File.Exists(backupPath))
        {
            backupPath = Path.Combine(
                _workspacePathProvider.WorkspaceRoot,
                $"workspace.broken.{timestamp}.{suffix}.db"
            );
            suffix++;
        }

        for (var attempt = 1; attempt <= 6; attempt++)
        {
            ct.ThrowIfCancellationRequested();

            try
            {
                File.Move(_workspacePathProvider.WorkspaceDbPath, backupPath);
                MoveIfExists($"{_workspacePathProvider.WorkspaceDbPath}-wal", $"{backupPath}-wal");
                MoveIfExists($"{_workspacePathProvider.WorkspaceDbPath}-shm", $"{backupPath}-shm");
                return;
            }
            catch (IOException) when (attempt < 6)
            {
                await Task.Delay(50, ct);
                SqliteConnection.ClearAllPools();
            }
        }
    }

    private async Task MarkRunningJobsAsAbandonedAsync(CancellationToken ct)
    {
        await using var db = await _dbContextFactory.CreateDbContextAsync(ct);
        var runningJobs = await db.Jobs
            .Where(job => job.Status == "Running")
            .ToListAsync(ct);

        if (runningJobs.Count == 0)
        {
            return;
        }

        var now = _clock.UtcNow.ToUniversalTime();
        foreach (var job in runningJobs)
        {
            job.Status = "Abandoned";
            job.CompletedAtUtc = now;
            job.Progress = 1;
            job.StatusMessage = "Abandoned (app shutdown before completion)";
            job.ErrorMessage ??= "Job abandoned after unexpected app shutdown.";

            db.AuditEvents.Add(
                new AuditEventRecord
                {
                    AuditEventId = Guid.NewGuid(),
                    TimestampUtc = now,
                    Operator = string.IsNullOrWhiteSpace(job.Operator)
                        ? Environment.UserName
                        : job.Operator,
                    ActionType = "JobAbandoned",
                    CaseId = job.CaseId,
                    EvidenceItemId = job.EvidenceItemId,
                    Summary = $"{job.JobType} job abandoned after app shutdown.",
                    JsonPayload = JsonSerializer.Serialize(new
                    {
                        job.JobId,
                        job.JobType,
                        job.CorrelationId,
                        job.StatusMessage
                    })
                }
            );
        }

        await db.SaveChangesAsync(ct);
    }

    private static async Task EnsureMessageFtsObjectsAsync(WorkspaceDbContext db, CancellationToken ct)
    {
        await db.Database.ExecuteSqlRawAsync(
            """
            CREATE VIRTUAL TABLE IF NOT EXISTS MessageEventFts
            USING fts5(MessageEventId UNINDEXED, CaseId UNINDEXED, Platform, Sender, Recipients, Body);
            """,
            ct
        );

        await db.Database.ExecuteSqlRawAsync(
            """
            CREATE TRIGGER IF NOT EXISTS MessageEventRecord_Fts_Insert
            AFTER INSERT ON MessageEventRecord
            BEGIN
                INSERT INTO MessageEventFts(MessageEventId, CaseId, Platform, Sender, Recipients, Body)
                VALUES (
                    NEW.MessageEventId,
                    NEW.CaseId,
                    COALESCE(NEW.Platform, ''),
                    COALESCE(NEW.Sender, ''),
                    COALESCE(NEW.Recipients, ''),
                    COALESCE(NEW.Body, '')
                );
            END;
            """,
            ct
        );

        await db.Database.ExecuteSqlRawAsync(
            """
            CREATE TRIGGER IF NOT EXISTS MessageEventRecord_Fts_Update
            AFTER UPDATE ON MessageEventRecord
            BEGIN
                DELETE FROM MessageEventFts WHERE MessageEventId = OLD.MessageEventId;
                INSERT INTO MessageEventFts(MessageEventId, CaseId, Platform, Sender, Recipients, Body)
                VALUES (
                    NEW.MessageEventId,
                    NEW.CaseId,
                    COALESCE(NEW.Platform, ''),
                    COALESCE(NEW.Sender, ''),
                    COALESCE(NEW.Recipients, ''),
                    COALESCE(NEW.Body, '')
                );
            END;
            """,
            ct
        );

        await db.Database.ExecuteSqlRawAsync(
            """
            CREATE TRIGGER IF NOT EXISTS MessageEventRecord_Fts_Delete
            AFTER DELETE ON MessageEventRecord
            BEGIN
                DELETE FROM MessageEventFts WHERE MessageEventId = OLD.MessageEventId;
            END;
            """,
            ct
        );

        await db.Database.ExecuteSqlRawAsync(
            """
            INSERT INTO MessageEventFts(MessageEventId, CaseId, Platform, Sender, Recipients, Body)
            SELECT
                me.MessageEventId,
                me.CaseId,
                COALESCE(me.Platform, ''),
                COALESCE(me.Sender, ''),
                COALESCE(me.Recipients, ''),
                COALESCE(me.Body, '')
            FROM MessageEventRecord me
            WHERE NOT EXISTS (
                SELECT 1
                FROM MessageEventFts fts
                WHERE fts.MessageEventId = me.MessageEventId
            );
            """,
            ct
        );
    }

    private static void MoveIfExists(string sourcePath, string destinationPath)
    {
        if (!File.Exists(sourcePath))
        {
            return;
        }

        File.Move(sourcePath, destinationPath);
    }

    private sealed class DatabaseInspection
    {
        public DatabaseInspection(
            bool exists,
            bool hasMigrationHistory,
            IReadOnlyList<string> missingRequiredTables
        )
        {
            Exists = exists;
            HasMigrationHistory = hasMigrationHistory;
            MissingRequiredTables = missingRequiredTables;
        }

        public bool Exists { get; }

        public bool HasMigrationHistory { get; }

        public IReadOnlyList<string> MissingRequiredTables { get; }

        public bool IsBroken => !HasMigrationHistory || MissingRequiredTables.Count > 0;
    }
}

public sealed class WorkspaceDatabaseInitializer : WorkspaceDbInitializer
{
    public WorkspaceDatabaseInitializer(
        IDbContextFactory<WorkspaceDbContext> dbContextFactory,
        IWorkspacePathProvider workspacePathProvider,
        WorkspaceDbRebuilder workspaceDbRebuilder,
        IClock clock
    )
        : base(dbContextFactory, workspacePathProvider, workspaceDbRebuilder, clock)
    {
    }
}
