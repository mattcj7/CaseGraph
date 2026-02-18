using CaseGraph.Core.Abstractions;

namespace CaseGraph.App.Services;

public sealed class WorkspaceMigrationService : IWorkspaceMigrationService
{
    private readonly IWorkspaceDbInitializer _workspaceDbInitializer;

    public WorkspaceMigrationService(IWorkspaceDbInitializer workspaceDbInitializer)
    {
        _workspaceDbInitializer = workspaceDbInitializer;
    }

    public Task EnsureMigratedAsync(CancellationToken ct)
    {
        return _workspaceDbInitializer.InitializeAsync(ct);
    }
}
