namespace CaseGraph.Core.Models;

public sealed class GlobalPersonIdentifierConflictException : InvalidOperationException
{
    public GlobalPersonIdentifierConflictException(GlobalPersonIdentifierConflictInfo conflict)
        : base(
            $"Global person identifier conflict: {conflict.Type} '{conflict.ValueDisplay}' is already linked to global person '{conflict.ExistingGlobalDisplayName}'."
        )
    {
        Conflict = conflict;
    }

    public GlobalPersonIdentifierConflictInfo Conflict { get; }
}
