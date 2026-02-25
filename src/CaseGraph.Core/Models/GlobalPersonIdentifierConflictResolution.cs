namespace CaseGraph.Core.Models;

public enum GlobalPersonIdentifierConflictResolution
{
    Cancel = 0,
    MoveIdentifierToRequestedPerson = 1,
    UseExistingPerson = 2
}
