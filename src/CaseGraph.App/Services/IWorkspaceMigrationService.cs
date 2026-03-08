namespace CaseGraph.App.Services;

public interface IWorkspaceMigrationService
{
    Task EnsureMigratedAsync(CancellationToken ct);

    Task RunDeferredStartupWorkAsync(CancellationToken ct);
}
