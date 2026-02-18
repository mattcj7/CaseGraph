using CaseGraph.Core.Abstractions;

namespace CaseGraph.Infrastructure.Services;

public sealed class DefaultWorkspacePathProvider : IWorkspacePathProvider
{
    private const string WorkspaceRootOverrideEnvironmentVariable = "CASEGRAPH_WORKSPACE_ROOT";

    public DefaultWorkspacePathProvider()
    {
        var overrideRoot = Environment.GetEnvironmentVariable(
            WorkspaceRootOverrideEnvironmentVariable
        );
        WorkspaceRoot = string.IsNullOrWhiteSpace(overrideRoot)
            ? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "CaseGraphOffline"
            )
            : overrideRoot.Trim();

        WorkspaceDbPath = Path.Combine(WorkspaceRoot, "workspace.db");
        CasesRoot = Path.Combine(WorkspaceRoot, "cases");
    }

    public string WorkspaceRoot { get; }

    public string WorkspaceDbPath { get; }

    public string CasesRoot { get; }
}
