namespace CaseGraph.Core.Abstractions;

public interface IWorkspaceDbInitializer
{
    Task InitializeAsync(CancellationToken ct);

    Task EnsureUpgradedAsync(CancellationToken ct);
}
