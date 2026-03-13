using CaseGraph.Core.Abstractions;
using CaseGraph.Core.Diagnostics;
using CaseGraph.Infrastructure.Diagnostics;
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
        "PersonEntity",
        "PersonAlias",
        "PersonIdentifier",
        "TargetAliasRecord",
        "IdentifierRecord",
        "TargetIdentifierLinkRecord",
        "MessageParticipantLinkRecord",
        "TargetMessagePresenceRecord",
        "LocationObservationRecord",
        "IncidentRecord",
        "IncidentLocationRecord",
        "IncidentPinnedResultRecord"
    };

    private readonly IDbContextFactory<WorkspaceDbContext> _dbContextFactory;
    private readonly IWorkspacePathProvider _workspacePathProvider;
    private readonly WorkspaceDbRebuilder _workspaceDbRebuilder;
    private readonly IWorkspaceWriteGate _workspaceWriteGate;
    private readonly IClock _clock;
    private readonly IStartupStageReporter? _startupStageReporter;
    private readonly IPerformanceInstrumentation _performanceInstrumentation;
    private readonly SemaphoreSlim _semaphore = new(1, 1);
    private bool _initialized;
    private string? _initializedWorkspaceDbPath;
    private bool _caseOpenReadinessCompleted;
    private string? _caseOpenReadinessWorkspaceDbPath;
    private bool _messageSearchReady;
    private string? _messageSearchReadyWorkspaceDbPath;
    private Task<MessageSearchMaintenanceResult>? _messageSearchMaintenanceTask;
    private string? _messageSearchMaintenanceWorkspaceDbPath;

    public WorkspaceDbInitializer(
        IDbContextFactory<WorkspaceDbContext> dbContextFactory,
        IWorkspacePathProvider workspacePathProvider,
        WorkspaceDbRebuilder workspaceDbRebuilder,
        IWorkspaceWriteGate workspaceWriteGate,
        IClock clock
    )
        : this(
            dbContextFactory,
            workspacePathProvider,
            workspaceDbRebuilder,
            workspaceWriteGate,
            clock,
            startupStageReporter: null,
            performanceInstrumentation: null
        )
    {
    }

    public WorkspaceDbInitializer(
        IDbContextFactory<WorkspaceDbContext> dbContextFactory,
        IWorkspacePathProvider workspacePathProvider,
        WorkspaceDbRebuilder workspaceDbRebuilder,
        IWorkspaceWriteGate workspaceWriteGate,
        IClock clock,
        IStartupStageReporter? startupStageReporter,
        IPerformanceInstrumentation? performanceInstrumentation = null
    )
    {
        _dbContextFactory = dbContextFactory;
        _workspacePathProvider = workspacePathProvider;
        _workspaceDbRebuilder = workspaceDbRebuilder;
        _workspaceWriteGate = workspaceWriteGate;
        _clock = clock;
        _startupStageReporter = startupStageReporter;
        _performanceInstrumentation = performanceInstrumentation
            ?? new PerformanceInstrumentation(new PerformanceBudgetOptions(), TimeProvider.System);
    }

    public Task EnsureInitializedAsync(CancellationToken ct)
    {
        return InitializeAsync(ct);
    }

    public async Task InitializeAsync(CancellationToken ct)
    {
        await _performanceInstrumentation.TrackAsync(
            new PerformanceOperationContext(
                PerformanceOperationKinds.Startup,
                "WorkspaceInitialize",
                Fields: new Dictionary<string, object?>
                {
                    ["workspaceDbPath"] = _workspacePathProvider.WorkspaceDbPath
                }
            ),
            async innerCt =>
            {
                await _semaphore.WaitAsync(innerCt);
                try
                {
                    using var workspaceScope = AppFileLogger.BeginWorkspaceScope(
                        _workspacePathProvider.WorkspaceRoot
                    );
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

                    _caseOpenReadinessCompleted = false;
                    _caseOpenReadinessWorkspaceDbPath = null;
                    _messageSearchReady = false;
                    _messageSearchReadyWorkspaceDbPath = null;
                    _messageSearchMaintenanceTask = null;
                    _messageSearchMaintenanceWorkspaceDbPath = null;

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

                    var inspection = await InspectDatabaseAsync(innerCt);
                    if (!inspection.Exists)
                    {
                        await EnsureUpgradedAsync(innerCt);
                    }
                    else if (inspection.HasMigrationHistory)
                    {
                        await EnsureUpgradedAsync(innerCt);
                    }
                    else if (inspection.IsBroken)
                    {
                        await RepairDatabaseAsync(innerCt);
                    }
                    else
                    {
                        await EnsureUpgradedAsync(innerCt);
                    }

                    var missingAfterUpgrade = await GetMissingRequiredTablesAsync(innerCt);
                    if (missingAfterUpgrade.Count > 0)
                    {
                        var postUpgradeInspection = await InspectDatabaseAsync(innerCt);
                        if (postUpgradeInspection.HasMigrationHistory)
                        {
                            await RepairDatabaseAsync(innerCt);
                        }
                    }

                    var missingRequiredTables = await GetMissingRequiredTablesAsync(innerCt);
                    if (missingRequiredTables.Count > 0)
                    {
                        throw new InvalidOperationException(
                            $"Workspace DB is missing required tables: {string.Join(", ", missingRequiredTables)}"
                        );
                    }

                    LogWorkspaceInitStep("SchemaVerify", "Running workspace schema verification query.");
                    await VerifySchemaHealthAsync(innerCt);
                    LogWorkspaceInitStep(
                        "SchemaVerifyCompleted",
                        "Workspace schema verification query completed."
                    );
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
            },
            ct
        );
    }

    public async Task<bool> RunCaseOpenReadinessAsync(CancellationToken ct)
    {
        await InitializeAsync(ct);

        return await _performanceInstrumentation.TrackAsync(
            new PerformanceOperationContext(
                PerformanceOperationKinds.CaseOpen,
                "WorkspaceCaseOpenReadiness",
                Fields: new Dictionary<string, object?>
                {
                    ["workspaceDbPath"] = _workspacePathProvider.WorkspaceDbPath
                }
            ),
            async innerCt =>
            {
                await _semaphore.WaitAsync(innerCt);
                try
                {
                    var currentWorkspaceDbPath = _workspacePathProvider.WorkspaceDbPath;
                    var sameWorkspacePath = string.Equals(
                        _caseOpenReadinessWorkspaceDbPath,
                        currentWorkspaceDbPath,
                        StringComparison.OrdinalIgnoreCase
                    );
                    if (_caseOpenReadinessCompleted && sameWorkspacePath)
                    {
                        return false;
                    }

                    AppFileLogger.LogEvent(
                        eventName: "WorkspaceCaseOpenReadinessStarted",
                        level: "INFO",
                        message: "Workspace case-open readiness started.",
                        fields: new Dictionary<string, object?>
                        {
                            ["workspaceDbPath"] = currentWorkspaceDbPath
                        }
                    );

                    await MarkRunningJobsAsAbandonedAsync(innerCt);

                    _caseOpenReadinessCompleted = true;
                    _caseOpenReadinessWorkspaceDbPath = currentWorkspaceDbPath;
                    AppFileLogger.LogEvent(
                        eventName: "WorkspaceCaseOpenReadinessCompleted",
                        level: "INFO",
                        message: "Workspace case-open readiness completed.",
                        fields: new Dictionary<string, object?>
                        {
                            ["workspaceDbPath"] = currentWorkspaceDbPath
                        }
                    );
                    return true;
                }
                finally
                {
                    _semaphore.Release();
                }
            },
            ct
        );
    }

    public async Task<bool> EnsureMessageSearchReadyAsync(CancellationToken ct)
    {
        var maintenanceTask = await EnsureMessageSearchMaintenanceScheduledAsync(ct);
        var result = await maintenanceTask.WaitAsync(ct);
        return result.WorkPerformed;
    }

    public async Task<MessageSearchReadinessStatus> GetMessageSearchReadinessStatusAsync(CancellationToken ct)
    {
        await InitializeAsync(ct);

        return await _performanceInstrumentation.TrackAsync(
            new PerformanceOperationContext(
                PerformanceOperationKinds.FeatureReadiness,
                "ReadinessStatus",
                FeatureName: "Search",
                Fields: new Dictionary<string, object?>
                {
                    ["workspaceDbPath"] = _workspacePathProvider.WorkspaceDbPath
                }
            ),
            async innerCt =>
            {
                await _semaphore.WaitAsync(innerCt);
                try
                {
                    var currentWorkspaceDbPath = _workspacePathProvider.WorkspaceDbPath;
                    AppFileLogger.LogEvent(
                        eventName: "WorkspaceMessageSearchReadinessCheckStarted",
                        level: "INFO",
                        message: "Workspace message search readiness check started.",
                        fields: new Dictionary<string, object?>
                        {
                            ["workspaceDbPath"] = currentWorkspaceDbPath
                        }
                    );

                    var readyForCurrentWorkspace = IsSameWorkspacePath(
                            _messageSearchReadyWorkspaceDbPath,
                            currentWorkspaceDbPath
                        )
                        && _messageSearchReady;
                    if (readyForCurrentWorkspace)
                    {
                        AppFileLogger.LogEvent(
                            eventName: "WorkspaceMessageSearchReadinessCheckCompleted",
                            level: "INFO",
                            message: "Workspace message search readiness check completed.",
                            fields: BuildMessageSearchReadinessFields(
                                currentWorkspaceDbPath,
                                MessageSearchReadinessStatus.CurrentCached
                            )
                        );
                        return MessageSearchReadinessStatus.CurrentCached;
                    }

                    var maintenanceInProgress = IsSameWorkspacePath(
                            _messageSearchMaintenanceWorkspaceDbPath,
                            currentWorkspaceDbPath
                        )
                        && _messageSearchMaintenanceTask is { IsCompleted: false };
                    if (maintenanceInProgress)
                    {
                        AppFileLogger.LogEvent(
                            eventName: "WorkspaceMessageSearchReadinessCheckCompleted",
                            level: "INFO",
                            message: "Workspace message search readiness check completed.",
                            fields: BuildMessageSearchReadinessFields(
                                currentWorkspaceDbPath,
                                MessageSearchReadinessStatus.MaintenanceInProgressCached
                            )
                        );
                        return MessageSearchReadinessStatus.MaintenanceInProgressCached;
                    }

                    await using var db = await _dbContextFactory.CreateDbContextAsync(innerCt);
                    await db.Database.OpenConnectionAsync(innerCt);
                    try
                    {
                        var inspection = await InspectMessageSearchFtsStateAsync(db, innerCt);
                        var status = MessageSearchReadinessStatus.FromInspection(inspection);

                        AppFileLogger.LogEvent(
                            eventName: "WorkspaceMessageSearchReadinessCheckCompleted",
                            level: "INFO",
                            message: "Workspace message search readiness check completed.",
                            fields: BuildMessageSearchReadinessFields(currentWorkspaceDbPath, status)
                        );

                        if (status.IsCurrent)
                        {
                            _messageSearchReady = true;
                            _messageSearchReadyWorkspaceDbPath = currentWorkspaceDbPath;
                        }

                        return status;
                    }
                    finally
                    {
                        await db.Database.CloseConnectionAsync();
                    }
                }
                finally
                {
                    _semaphore.Release();
                }
            },
            ct
        );
    }

    public async Task<Task<MessageSearchMaintenanceResult>> EnsureMessageSearchMaintenanceScheduledAsync(
        CancellationToken ct
    )
    {
        await InitializeAsync(ct);

        Task<MessageSearchMaintenanceResult>? maintenanceTask = null;
        var currentWorkspaceDbPath = _workspacePathProvider.WorkspaceDbPath;

        await _semaphore.WaitAsync(ct);
        try
        {
            var readyForCurrentWorkspace = IsSameWorkspacePath(_messageSearchReadyWorkspaceDbPath, currentWorkspaceDbPath)
                && _messageSearchReady;
            if (readyForCurrentWorkspace)
            {
                return Task.FromResult(
                    new MessageSearchMaintenanceResult(
                        WorkPerformed: false,
                        Summary: "Message search readiness already current."
                    )
                );
            }

            var existingTask = _messageSearchMaintenanceTask;
            var sameMaintenanceWorkspace = IsSameWorkspacePath(
                _messageSearchMaintenanceWorkspaceDbPath,
                currentWorkspaceDbPath
            );
            if (sameMaintenanceWorkspace && existingTask is { IsCompleted: false })
            {
                return existingTask;
            }

            maintenanceTask = RunMessageSearchMaintenanceAsync(currentWorkspaceDbPath);
            _messageSearchMaintenanceTask = maintenanceTask;
            _messageSearchMaintenanceWorkspaceDbPath = currentWorkspaceDbPath;
        }
        finally
        {
            _semaphore.Release();
        }

        ObserveMessageSearchMaintenanceCompletion(maintenanceTask!, currentWorkspaceDbPath).Forget(
            "ObserveMessageSearchMaintenanceCompletion"
        );
        return maintenanceTask!;
    }

    private async Task<MessageSearchMaintenanceResult> RunMessageSearchMaintenanceAsync(string workspaceDbPath)
    {
        return await _performanceInstrumentation.TrackAsync(
            new PerformanceOperationContext(
                PerformanceOperationKinds.ImportMaintenance,
                "Maintenance",
                FeatureName: "MessageSearch",
                Fields: new Dictionary<string, object?>
                {
                    ["workspaceDbPath"] = workspaceDbPath
                }
            ),
            async _ =>
            {
                await using var db = await _dbContextFactory.CreateDbContextAsync();
                await db.Database.OpenConnectionAsync();
                try
                {
                    var inspection = await InspectMessageSearchFtsStateAsync(db, CancellationToken.None);
                    if (!inspection.RequiresMaintenance)
                    {
                        AppFileLogger.LogEvent(
                            eventName: "WorkspaceMessageSearchMaintenanceSkipped",
                            level: "INFO",
                            message: "Workspace message search maintenance skipped because the index is already current.",
                            fields: BuildMessageSearchReadinessFields(
                                workspaceDbPath,
                                MessageSearchReadinessStatus.FromInspection(inspection)
                            )
                        );
                        await MarkMessageSearchReadyAsync(workspaceDbPath);
                        return new MessageSearchMaintenanceResult(
                            WorkPerformed: false,
                            Summary: "Message search readiness already current."
                        );
                    }

                    AppFileLogger.LogEvent(
                        eventName: "WorkspaceMessageSearchMaintenanceStarted",
                        level: "INFO",
                        message: "Workspace message search maintenance started.",
                        fields: BuildMessageSearchReadinessFields(
                            workspaceDbPath,
                            MessageSearchReadinessStatus.FromInspection(inspection)
                        )
                    );

                    var maintenanceResult = await EnsureMessageFtsObjectsAsync(
                        db,
                        workspaceDbPath,
                        inspection,
                        CancellationToken.None
                    );

                    await MarkMessageSearchReadyAsync(workspaceDbPath);
                    AppFileLogger.LogEvent(
                        eventName: "WorkspaceMessageSearchReadinessCompleted",
                        level: "INFO",
                        message: "Workspace message search readiness completed.",
                        fields: new Dictionary<string, object?>
                        {
                            ["workspaceDbPath"] = workspaceDbPath,
                            ["workPerformed"] = maintenanceResult.WorkPerformed,
                            ["maintenanceAction"] = maintenanceResult.Action
                        }
                    );
                    return maintenanceResult;
                }
                finally
                {
                    await db.Database.CloseConnectionAsync();
                }
            },
            CancellationToken.None
        );
    }

    private async Task ObserveMessageSearchMaintenanceCompletion(
        Task<MessageSearchMaintenanceResult> maintenanceTask,
        string workspaceDbPath
    )
    {
        try
        {
            await maintenanceTask;
        }
        finally
        {
            await _semaphore.WaitAsync();
            try
            {
                if (ReferenceEquals(_messageSearchMaintenanceTask, maintenanceTask)
                    && IsSameWorkspacePath(_messageSearchMaintenanceWorkspaceDbPath, workspaceDbPath))
                {
                    _messageSearchMaintenanceTask = null;
                    _messageSearchMaintenanceWorkspaceDbPath = null;
                }
            }
            finally
            {
                _semaphore.Release();
            }
        }
    }

    private static Dictionary<string, object?> BuildMessageSearchReadinessFields(
        string workspaceDbPath,
        MessageSearchReadinessStatus status
    )
    {
        var fields = new Dictionary<string, object?>
        {
            ["workspaceDbPath"] = workspaceDbPath,
            ["status"] = status.State.ToString(),
            ["ftsTableExists"] = status.FtsTableExists,
            ["insertTriggerExists"] = status.InsertTriggerExists,
            ["updateTriggerExists"] = status.UpdateTriggerExists,
            ["deleteTriggerExists"] = status.DeleteTriggerExists
        };

        if (status.MessageEventCount.HasValue)
        {
            fields["messageEventCount"] = status.MessageEventCount.Value;
        }

        if (status.FtsRowCount.HasValue)
        {
            fields["ftsRowCount"] = status.FtsRowCount.Value;
        }

        return fields;
    }

    private static bool IsSameWorkspacePath(string? left, string? right)
    {
        return string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
    }

    private async Task MarkMessageSearchReadyAsync(string workspaceDbPath)
    {
        await _semaphore.WaitAsync();
        try
        {
            _messageSearchReady = true;
            _messageSearchReadyWorkspaceDbPath = workspaceDbPath;
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

    private static async Task<MessageSearchMaintenanceResult> EnsureMessageFtsObjectsAsync(
        WorkspaceDbContext db,
        string workspaceDbPath,
        MessageSearchFtsInspection inspection,
        CancellationToken ct
    )
    {
        try
        {
            AppFileLogger.LogEvent(
                eventName: "WorkspaceMessageFtsEnsureStarted",
                level: "INFO",
                message: "Ensuring workspace message FTS objects exist.",
                fields: BuildMessageSearchReadinessFields(
                    workspaceDbPath,
                    MessageSearchReadinessStatus.FromInspection(inspection)
                )
            );

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

            var action = inspection.RequiresRebuild
                ? "Rebuild"
                : inspection.RequiresBackfill
                    ? "Backfill"
                    : "None";

            AppFileLogger.LogEvent(
                eventName: "WorkspaceMessageFtsMaintenanceDecision",
                level: "INFO",
                message: "Workspace message FTS maintenance action decided.",
                fields: new Dictionary<string, object?>
                {
                    ["workspaceDbPath"] = workspaceDbPath,
                    ["action"] = action,
                    ["status"] = MessageSearchReadinessStatus.FromInspection(inspection).State.ToString()
                }
            );

            if (action == "None")
            {
                AppFileLogger.LogEvent(
                    eventName: "WorkspaceMessageFtsMaintenanceSkipped",
                    level: "INFO",
                    message: "Workspace message FTS maintenance skipped because no repair was required.",
                    fields: new Dictionary<string, object?>
                    {
                        ["workspaceDbPath"] = workspaceDbPath
                    }
                );
                return new MessageSearchMaintenanceResult(
                    WorkPerformed: false,
                    Summary: "Message search readiness already current.",
                    Action: action
                );
            }

            if (action == "Rebuild")
            {
                AppFileLogger.LogEvent(
                    eventName: "WorkspaceMessageFtsRebuildStarted",
                    level: "INFO",
                    message: "Workspace message FTS rebuild started.",
                    fields: new Dictionary<string, object?>
                    {
                        ["workspaceDbPath"] = workspaceDbPath
                    }
                );
                await db.Database.ExecuteSqlRawAsync("DELETE FROM MessageEventFts;", ct);
                var affectedRows = await db.Database.ExecuteSqlRawAsync(
                    """
                    INSERT INTO MessageEventFts(MessageEventId, CaseId, Platform, Sender, Recipients, Body)
                    SELECT
                        me.MessageEventId,
                        me.CaseId,
                        COALESCE(me.Platform, ''),
                        COALESCE(me.Sender, ''),
                        COALESCE(me.Recipients, ''),
                        COALESCE(me.Body, '')
                    FROM MessageEventRecord me;
                    """,
                    ct
                );
                AppFileLogger.LogEvent(
                    eventName: "WorkspaceMessageFtsRebuildCompleted",
                    level: "INFO",
                    message: "Workspace message FTS rebuild completed.",
                    fields: new Dictionary<string, object?>
                    {
                        ["workspaceDbPath"] = workspaceDbPath,
                        ["affectedRows"] = affectedRows
                    }
                );
                return new MessageSearchMaintenanceResult(
                    WorkPerformed: true,
                    Summary: "Message search index rebuild completed.",
                    Action: action
                );
            }

            AppFileLogger.LogEvent(
                eventName: "WorkspaceMessageFtsBackfillStarted",
                level: "INFO",
                message: "Workspace message FTS backfill started.",
                fields: new Dictionary<string, object?>
                {
                    ["workspaceDbPath"] = workspaceDbPath
                }
            );
            var backfillAffectedRows = await db.Database.ExecuteSqlRawAsync(
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
            AppFileLogger.LogEvent(
                eventName: "WorkspaceMessageFtsBackfillCompleted",
                level: "INFO",
                message: "Workspace message FTS backfill completed.",
                fields: new Dictionary<string, object?>
                {
                    ["workspaceDbPath"] = workspaceDbPath,
                    ["affectedRows"] = backfillAffectedRows
                }
            );
            return new MessageSearchMaintenanceResult(
                WorkPerformed: true,
                Summary: "Message search index backfill completed.",
                Action: action
            );
        }
        catch (Exception ex)
        {
            AppFileLogger.LogEvent(
                eventName: "WorkspaceMessageFtsMaintenanceFailed",
                level: "ERROR",
                message: "Workspace message FTS maintenance failed.",
                ex: ex,
                fields: new Dictionary<string, object?>
                {
                    ["workspaceDbPath"] = workspaceDbPath
                }
            );
            throw;
        }
    }

    private static async Task<bool> SqliteObjectExistsAsync(
        WorkspaceDbContext db,
        string objectType,
        string objectName,
        CancellationToken ct
    )
    {
        var connection = db.Database.GetDbConnection();
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT 1
            FROM sqlite_master
            WHERE type = $type
              AND name = $name
            LIMIT 1;
            """;

        var typeParameter = command.CreateParameter();
        typeParameter.ParameterName = "$type";
        typeParameter.Value = objectType;
        command.Parameters.Add(typeParameter);

        var nameParameter = command.CreateParameter();
        nameParameter.ParameterName = "$name";
        nameParameter.Value = objectName;
        command.Parameters.Add(nameParameter);

        var result = await command.ExecuteScalarAsync(ct);
        return result is not null;
    }

    private static async Task<MessageSearchFtsInspection> InspectMessageSearchFtsStateAsync(
        WorkspaceDbContext db,
        CancellationToken ct
    )
    {
        var ftsTableExists = await SqliteObjectExistsAsync(db, "table", "MessageEventFts", ct);
        var insertTriggerExists = await SqliteObjectExistsAsync(db, "trigger", "MessageEventRecord_Fts_Insert", ct);
        var updateTriggerExists = await SqliteObjectExistsAsync(db, "trigger", "MessageEventRecord_Fts_Update", ct);
        var deleteTriggerExists = await SqliteObjectExistsAsync(db, "trigger", "MessageEventRecord_Fts_Delete", ct);
        var messageEventCount = await CountRowsAsync(db, "MessageEventRecord", ct);
        long? ftsRowCount = null;
        if (ftsTableExists)
        {
            ftsRowCount = await CountRowsAsync(db, "MessageEventFts", ct);
        }

        return new MessageSearchFtsInspection(
            ftsTableExists,
            insertTriggerExists,
            updateTriggerExists,
            deleteTriggerExists,
            messageEventCount,
            ftsRowCount
        );
    }

    private static async Task<long> CountRowsAsync(
        WorkspaceDbContext db,
        string tableName,
        CancellationToken ct
    )
    {
        var connection = db.Database.GetDbConnection();
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT COUNT(1) FROM {tableName};";
        var result = await command.ExecuteScalarAsync(ct);
        return result is null or DBNull
            ? 0
            : Convert.ToInt64(result, System.Globalization.CultureInfo.InvariantCulture);
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
        ReportStartupStage(step, message);
    }

    private void ReportStartupStage(string step, string message)
    {
        if (_startupStageReporter is null)
        {
            return;
        }

        switch (step)
        {
            case "InspectDatabase":
            case "OpenConnection":
                _startupStageReporter.ReportStage(
                    StartupStageKeys.OpeningWorkspace,
                    "Opening workspace",
                    message
                );
                break;

            case "LoadMigrations":
            case "ApplyMigrations":
                _startupStageReporter.ReportStage(
                    StartupStageKeys.LoadingMigrations,
                    "Loading and applying migrations",
                    message
                );
                break;

            case "EnsureMessageFtsObjects":
                _startupStageReporter.ReportStage(
                    StartupStageKeys.EnsuringMessageSearchIndex,
                    "Ensuring message search index",
                    message
                );
                break;

            case "SchemaVerify":
            case "SchemaVerifyQuery":
                _startupStageReporter.ReportStage(
                    StartupStageKeys.VerifyingSchema,
                    "Verifying schema",
                    message
                );
                break;

            case "Finalize":
                _startupStageReporter.ReportStage(
                    StartupStageKeys.FinalizingStartup,
                    "Finalizing startup",
                    message
                );
                break;
        }
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

public enum MessageSearchReadinessState
{
    Current,
    MissingObjects,
    Stale,
    MaintenanceInProgress
}

public sealed record MessageSearchReadinessStatus(
    MessageSearchReadinessState State,
    bool FtsTableExists,
    bool InsertTriggerExists,
    bool UpdateTriggerExists,
    bool DeleteTriggerExists,
    long? MessageEventCount,
    long? FtsRowCount
)
{
    public static MessageSearchReadinessStatus CurrentCached { get; } = new(
        MessageSearchReadinessState.Current,
        FtsTableExists: true,
        InsertTriggerExists: true,
        UpdateTriggerExists: true,
        DeleteTriggerExists: true,
        MessageEventCount: null,
        FtsRowCount: null
    );

    public static MessageSearchReadinessStatus MaintenanceInProgressCached { get; } = new(
        MessageSearchReadinessState.MaintenanceInProgress,
        FtsTableExists: true,
        InsertTriggerExists: true,
        UpdateTriggerExists: true,
        DeleteTriggerExists: true,
        MessageEventCount: null,
        FtsRowCount: null
    );

    public bool IsCurrent => State == MessageSearchReadinessState.Current;

    public static MessageSearchReadinessStatus FromInspection(MessageSearchFtsInspection inspection)
    {
        var state = inspection.RequiresRebuild
            ? MessageSearchReadinessState.MissingObjects
            : inspection.RequiresBackfill
                ? MessageSearchReadinessState.Stale
                : MessageSearchReadinessState.Current;

        return new MessageSearchReadinessStatus(
            state,
            inspection.FtsTableExists,
            inspection.InsertTriggerExists,
            inspection.UpdateTriggerExists,
            inspection.DeleteTriggerExists,
            inspection.MessageEventCount,
            inspection.FtsRowCount
        );
    }
}

public sealed record MessageSearchMaintenanceResult(
    bool WorkPerformed,
    string Summary,
    string Action = "None"
);

public sealed record MessageSearchFtsInspection(
    bool FtsTableExists,
    bool InsertTriggerExists,
    bool UpdateTriggerExists,
    bool DeleteTriggerExists,
    long MessageEventCount,
    long? FtsRowCount
)
{
    public bool RequiresRebuild => !FtsTableExists
        || !InsertTriggerExists
        || !UpdateTriggerExists
        || !DeleteTriggerExists;

    public bool RequiresBackfill => FtsTableExists
        && MessageEventCount != (FtsRowCount ?? 0);

    public bool RequiresMaintenance => RequiresRebuild || RequiresBackfill;
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

    public WorkspaceDatabaseInitializer(
        IDbContextFactory<WorkspaceDbContext> dbContextFactory,
        IWorkspacePathProvider workspacePathProvider,
        WorkspaceDbRebuilder workspaceDbRebuilder,
        IWorkspaceWriteGate workspaceWriteGate,
        IClock clock,
        IStartupStageReporter? startupStageReporter
    )
        : base(
            dbContextFactory,
            workspacePathProvider,
            workspaceDbRebuilder,
            workspaceWriteGate,
            clock,
            startupStageReporter
        )
    {
    }
}
