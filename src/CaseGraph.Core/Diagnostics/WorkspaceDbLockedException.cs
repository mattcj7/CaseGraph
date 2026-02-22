namespace CaseGraph.Core.Diagnostics;

public sealed class WorkspaceDbLockedException : Exception
{
    public WorkspaceDbLockedException(
        string operationName,
        int attemptCount,
        string workspaceDbPath,
        Exception innerException
    )
        : base(
            $"Workspace database remained locked while executing \"{operationName}\" after {attemptCount} attempt(s).",
            innerException
        )
    {
        OperationName = operationName;
        AttemptCount = attemptCount;
        WorkspaceDbPath = workspaceDbPath;
    }

    public string OperationName { get; }

    public int AttemptCount { get; }

    public string WorkspaceDbPath { get; }
}
