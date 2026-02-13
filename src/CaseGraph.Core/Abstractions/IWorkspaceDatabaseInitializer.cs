namespace CaseGraph.Core.Abstractions;

public interface IWorkspaceDatabaseInitializer
{
    Task EnsureInitializedAsync(CancellationToken ct);
}
