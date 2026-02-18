namespace CaseGraph.App.Services;

public interface IWorkspaceMigrationService
{
    Task EnsureMigratedAsync(CancellationToken ct);
}
