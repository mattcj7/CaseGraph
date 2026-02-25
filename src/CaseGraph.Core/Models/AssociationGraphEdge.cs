namespace CaseGraph.Core.Models;

public sealed record AssociationGraphEdge(
    string EdgeId,
    string SourceNodeId,
    string TargetNodeId,
    AssociationGraphEdgeKind Kind,
    int Weight,
    int DistinctThreadCount,
    int DistinctEventCount,
    DateTimeOffset? LastSeenUtc
);
