namespace CaseGraph.Core.Models;

public sealed record AssociationGraphResult(
    Guid CaseId,
    AssociationGraphBuildOptions Options,
    IReadOnlyList<AssociationGraphNode> Nodes,
    IReadOnlyList<AssociationGraphEdge> Edges
);
