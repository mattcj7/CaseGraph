namespace CaseGraph.Core.Models;

public sealed class IdentifierConflictException : InvalidOperationException
{
    public IdentifierConflictException(IdentifierConflictInfo conflict)
        : base(
            $"Identifier conflict: {conflict.Type} '{conflict.ValueRaw}' is already linked to target '{conflict.ExistingTargetDisplayName}'."
        )
    {
        Conflict = conflict;
    }

    public IdentifierConflictInfo Conflict { get; }
}
