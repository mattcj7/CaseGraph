using CaseGraph.Core.Abstractions;
using CaseGraph.Core.Diagnostics;

namespace CaseGraph.App.Services;

public sealed class WorkspaceMigrationService : IWorkspaceMigrationService
{
    private static readonly TimeSpan WorkspaceMigrationTimeout = TimeSpan.FromSeconds(15);

    private readonly IWorkspaceDbInitializer _workspaceDbInitializer;
    private readonly IWorkspacePathProvider _workspacePathProvider;

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
        var timeoutMessage =
            $"Workspace initialization timed out after {WorkspaceMigrationTimeout.TotalSeconds:0} seconds. "
            + $"WorkspaceDbPath={_workspacePathProvider.WorkspaceDbPath}";

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
                    ["workspaceDbPath"] = _workspacePathProvider.WorkspaceDbPath,
                    ["timeoutSeconds"] = WorkspaceMigrationTimeout.TotalSeconds
                }
            );
            throw;
        }
    }
}
