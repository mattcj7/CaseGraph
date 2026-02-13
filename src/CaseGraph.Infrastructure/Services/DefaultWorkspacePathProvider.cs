using CaseGraph.Core.Abstractions;

namespace CaseGraph.Infrastructure.Services;

public sealed class DefaultWorkspacePathProvider : IWorkspacePathProvider
{
    public DefaultWorkspacePathProvider()
    {
        WorkspaceRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "CaseGraphOffline"
        );

        WorkspaceDbPath = Path.Combine(WorkspaceRoot, "workspace.db");
        CasesRoot = Path.Combine(WorkspaceRoot, "cases");
    }

    public string WorkspaceRoot { get; }

    public string WorkspaceDbPath { get; }

    public string CasesRoot { get; }
}
