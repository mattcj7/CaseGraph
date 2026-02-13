namespace CaseGraph.Core.Abstractions;

public interface IWorkspacePathProvider
{
    string WorkspaceRoot { get; }

    string WorkspaceDbPath { get; }

    string CasesRoot { get; }
}
