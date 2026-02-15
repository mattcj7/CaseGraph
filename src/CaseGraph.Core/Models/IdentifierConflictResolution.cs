namespace CaseGraph.Core.Models;

public enum IdentifierConflictResolution
{
    Cancel = 0,
    MoveIdentifierToRequestedTarget = 1,
    UseExistingTarget = 2
}
