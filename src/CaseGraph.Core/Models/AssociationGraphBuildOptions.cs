namespace CaseGraph.Core.Models;

public sealed record AssociationGraphBuildOptions(
    bool IncludeIdentifiers = true,
    bool GroupByGlobalPerson = false,
    int MinEdgeWeight = 2
)
{
    public AssociationGraphBuildOptions Normalize()
    {
        return this with
        {
            MinEdgeWeight = Math.Max(0, MinEdgeWeight)
        };
    }
}
