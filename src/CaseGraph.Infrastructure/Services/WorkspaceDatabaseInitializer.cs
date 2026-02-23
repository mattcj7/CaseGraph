using CaseGraph.Core.Abstractions;
using CaseGraph.Core.Diagnostics;
using CaseGraph.Infrastructure.Persistence;
using CaseGraph.Infrastructure.Persistence.Entities;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using System.Text.Json;

namespace CaseGraph.Infrastructure.Services;

public class WorkspaceDbInitializer : IWorkspaceDbInitializer, IWorkspaceDatabaseInitializer
{
    private const int SqliteBusyTimeoutSeconds = 5;

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
    private readonly IWorkspaceWriteGate _workspaceWriteGate;
    private readonly IClock _clock;
    private readonly SemaphoreSlim _semaphore = new(1, 1);
    private bool _initialized;
    private string? _initializedWorkspaceDbPath;

    public WorkspaceDbInitializer(
        IDbContextFactory<WorkspaceDbContext> dbContextFactory,
        IWorkspacePathProvider workspacePathProvider,
        WorkspaceDbRebuilder workspaceDbRebuilder,
        IWorkspaceWriteGate workspaceWriteGate,
        IClock clock
    )
    {
        _dbContextFactory = dbContextFactory;
        _workspacePathProvider = workspacePathProvider;
        _workspaceDbRebuilder = workspaceDbRebuilder;
        _workspaceWriteGate = workspaceWriteGate;
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
            using var workspaceScope = AppFileLogger.BeginWorkspaceScope(_workspacePathProvider.WorkspaceRoot);
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

            AppFileLogger.LogEvent(
                eventName: "WorkspaceInitStarted",
                level: "INFO",
                message: "Workspace initializer started.",
                fields: new Dictionary<string, object?>
                {
                    ["workspaceDbPath"] = currentWorkspaceDbPath
                }
            );
            LogWorkspaceInitStep("InspectDatabase", "Inspecting existing workspace database state.");

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

            LogWorkspaceInitStep("SchemaVerify", "Running workspace schema verification query.");
            await VerifySchemaHealthAsync(ct);
            LogWorkspaceInitStep("Finalize", "Marking stale running jobs as abandoned.");
            await MarkRunningJobsAsAbandonedAsync(ct);
            _initialized = true;
            _initializedWorkspaceDbPath = currentWorkspaceDbPath;
            AppFileLogger.LogEvent(
                eventName: "WorkspaceInitCompleted",
                level: "INFO",
                message: "Workspace initializer completed.",
                fields: new Dictionary<string, object?>
                {
                    ["workspaceDbPath"] = currentWorkspaceDbPath
                }
            );
        }
        finally
        {
            _semaphore.Release();
        }
    }

    public async Task EnsureUpgradedAsync(CancellationToken ct)
    {
        await using var db = await _dbContextFactory.CreateDbContextAsync(ct);

        LogWorkspaceInitStep("OpenConnection", "Opening workspace database connection.");
        await db.Database.OpenConnectionAsync(ct);
        try
        {
            await db.Database.ExecuteSqlRawAsync($"PRAGMA busy_timeout = {SqliteBusyTimeoutSeconds * 1000};", ct);

            LogWorkspaceInitStep("LoadMigrations", "Loading pending EF migrations.");
            var pendingMigrations = (await db.Database.GetPendingMigrationsAsync(ct)).ToArray();
            AppFileLogger.LogEvent(
                eventName: "WorkspacePendingMigrations",
                level: "INFO",
                message: "Workspace pending migrations loaded.",
                fields: new Dictionary<string, object?>
                {
                    ["pendingMigrationCount"] = pendingMigrations.Length,
                    ["pendingMigrations"] = pendingMigrations.Length == 0
                        ? "(none)"
                        : string.Join(", ", pendingMigrations)
                }
            );

            LogWorkspaceInitStep("ApplyMigrations", "Applying EF Core migrations.");
            await db.Database.MigrateAsync(ct);
            LogWorkspaceInitStep("ApplyMigrationsCompleted", "EF Core migrations applied.");
            await EnsureMessageFtsObjectsAsync(db, ct);
        }
        finally
        {
            await db.Database.CloseConnectionAsync();
        }
    }

    private async Task VerifySchemaHealthAsync(CancellationToken ct)
    {
        try
        {
            await using var db = await _dbContextFactory.CreateDbContextAsync(ct);
            var appliedMigrations = (await db.Database.GetAppliedMigrationsAsync(ct)).ToArray();
            var migrationText = appliedMigrations.Length == 0
                ? "(none)"
                : string.Join(", ", appliedMigrations);
            AppFileLogger.LogEvent(
                eventName: "WorkspaceMigrations",
                level: "INFO",
                message: "Workspace migrations loaded.",
                fields: new Dictionary<string, object?>
                {
                    ["migrationCount"] = appliedMigrations.Length,
                    ["migrations"] = migrationText
                }
            );

            LogWorkspaceInitStep("SchemaVerifyQuery", "Executing schema verification query against CaseRecord.");
            var caseCount = await db.Cases.AsNoTracking().CountAsync(ct);
            AppFileLogger.LogEvent(
                eventName: "WorkspaceSchemaVerified",
                level: "INFO",
                message: "Workspace schema verification query succeeded.",
                fields: new Dictionary<string, object?>
                {
                    ["caseCount"] = caseCount
                }
            );
        }
        catch (Exception ex)
        {
            var correlationId = AppFileLogger.NewCorrelationId();
            AppFileLogger.LogEvent(
                eventName: "WorkspaceSchemaVerificationFailed",
                level: "FATAL",
                message: "Workspace database verification failed.",
                ex: ex,
                fields: new Dictionary<string, object?>
                {
                    ["correlationId"] = correlationId,
                    ["workspaceDbPath"] = _workspacePathProvider.WorkspaceDbPath
                }
            );
            throw new InvalidOperationException(
                $"Workspace database verification failed. CorrelationId={correlationId}",
                ex
            );
        }
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
        var connectionStringBuilder = new SqliteConnectionStringBuilder
        {
            DataSource = _workspacePathProvider.WorkspaceDbPath,
            Mode = SqliteOpenMode.ReadWrite,
            DefaultTimeout = SqliteBusyTimeoutSeconds
        };
        await using var connection = new SqliteConnection(connectionStringBuilder.ConnectionString);
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
        AppFileLogger.LogEvent(
            eventName: "WorkspaceRepairStarted",
            level: "WARN",
            message: "Workspace database repair started."
        );
        if (File.Exists(_workspacePathProvider.WorkspaceDbPath))
        {
            await BackupBrokenDatabaseAsync(ct);
        }

        await EnsureUpgradedAsync(ct);
        await _workspaceDbRebuilder.RebuildAsync(ct);
        AppFileLogger.LogEvent(
            eventName: "WorkspaceRepairCompleted",
            level: "INFO",
            message: "Workspace database repair completed."
        );
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
        await _workspaceWriteGate.ExecuteWriteAsync(
            operationName: "WorkspaceInit.MarkRunningJobsAbandoned",
            async writeCt =>
            {
                await using var db = await _dbContextFactory.CreateDbContextAsync(writeCt);
                var runningJobs = await db.Jobs
                    .Where(job => job.Status == "Running")
                    .ToListAsync(writeCt);

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

                await db.SaveChangesAsync(writeCt);
            },
            ct,
            correlationId: AppFileLogger.GetScopeValue("correlationId")
        );
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

    private void LogWorkspaceInitStep(string step, string message)
    {
        AppFileLogger.LogEvent(
            eventName: "WorkspaceInitStep",
            level: "INFO",
            message: message,
            fields: new Dictionary<string, object?>
            {
                ["step"] = step,
                ["workspaceDbPath"] = _workspacePathProvider.WorkspaceDbPath
            }
        );
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
        IWorkspaceWriteGate workspaceWriteGate,
        IClock clock
    )
        : base(dbContextFactory, workspacePathProvider, workspaceDbRebuilder, workspaceWriteGate, clock)
    {
    }
}
