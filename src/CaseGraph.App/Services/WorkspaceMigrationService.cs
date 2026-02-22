using CaseGraph.Core.Abstractions;
using CaseGraph.Core.Diagnostics;

namespace CaseGraph.App.Services;

public sealed class WorkspaceMigrationService : IWorkspaceMigrationService
{
    private static readonly TimeSpan WorkspaceMigrationTimeout = TimeSpan.FromSeconds(15);

    private readonly IWorkspaceDbInitializer _workspaceDbInitializer;
    private readonly IWorkspacePathProvider _workspacePathProvider;
    private readonly SemaphoreSlim _migrationSemaphore = new(1, 1);
    private bool _migrationSucceeded;
    private string? _migratedWorkspaceDbPath;

    public WorkspaceMigrationService(
        IWorkspaceDbInitializer workspaceDbInitializer,
        IWorkspacePathProvider workspacePathProvider
    )
    {
        _workspaceDbInitializer = workspaceDbInitializer;
        _workspacePathProvider = workspacePathProvider;
    }

    public async Task EnsureMigratedAsync(CancellationToken ct)
    {
        var workspaceDbPath = _workspacePathProvider.WorkspaceDbPath;
        if (HasSuccessfulMigration(workspaceDbPath))
        {
            return;
        }

        await _migrationSemaphore.WaitAsync(ct);
        try
        {
            if (HasSuccessfulMigration(workspaceDbPath))
            {
                return;
            }

            await EnsureMigratedCoreAsync(workspaceDbPath, ct);
            _migrationSucceeded = true;
            _migratedWorkspaceDbPath = workspaceDbPath;
        }
        finally
        {
            _migrationSemaphore.Release();
        }
    }

    private async Task EnsureMigratedCoreAsync(string workspaceDbPath, CancellationToken ct)
    {
        var timeoutMessage =
            $"Workspace initialization timed out after {WorkspaceMigrationTimeout.TotalSeconds:0} seconds. "
            + $"WorkspaceDbPath={workspaceDbPath}";

        try
        {
            await TimeoutWatchdog.RunAsync(
                operation: token => _workspaceDbInitializer.InitializeAsync(token),
                timeout: WorkspaceMigrationTimeout,
                timeoutMessage: timeoutMessage,
                ct: ct,
                onTimeout: task => task.Forget("WorkspaceInitializeAfterTimeout")
            );
        }
        catch (TimeoutException ex)
        {
            AppFileLogger.LogEvent(
                eventName: "WorkspaceInitializationTimedOut",
                level: "ERROR",
                message: "Workspace initialization timed out; workspace.db may be locked by another process or another CaseGraph instance.",
                ex: ex,
                fields: new Dictionary<string, object?>
                {
                    ["workspaceDbPath"] = workspaceDbPath,
                    ["timeoutSeconds"] = WorkspaceMigrationTimeout.TotalSeconds
                }
            );
            throw;
        }
    }

    private bool HasSuccessfulMigration(string workspaceDbPath)
    {
        return _migrationSucceeded
            && string.Equals(
                _migratedWorkspaceDbPath,
                workspaceDbPath,
                StringComparison.OrdinalIgnoreCase
            );
    }
}
