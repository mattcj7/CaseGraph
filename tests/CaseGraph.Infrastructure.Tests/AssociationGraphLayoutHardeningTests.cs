using CaseGraph.App.Views.Pages;
using CaseGraph.Core.Models;

namespace CaseGraph.Infrastructure.Tests;

public sealed class AssociationGraphLayoutHardeningTests
{
    [Fact]
    public void BuildRenderPlan_EmptyGraph_ReturnsSafeEmptyPlan()
    {
        var plan = AssociationGraphRenderPipeline.BuildRenderPlan(
            new AssociationGraphResult(
                Guid.NewGuid(),
                new AssociationGraphBuildOptions(true, false, 1),
                Array.Empty<AssociationGraphNode>(),
                Array.Empty<AssociationGraphEdge>()
            )
        );

        Assert.True(plan.IsEmpty);
        Assert.Empty(plan.Nodes);
        Assert.Empty(plan.Edges);
        Assert.Empty(plan.Placements);
        Assert.Equal("Empty", plan.LayoutAlgorithm);
    }

    [Fact]
    public void BuildRenderPlan_SingleNode_ReturnsSingleNodeLayout()
    {
        var graph = BuildGraph(
            [
                BuildNode("target:1", AssociationGraphNodeKind.Target, "Alpha")
            ],
            []
        );

        var plan = AssociationGraphRenderPipeline.BuildRenderPlan(graph);

        Assert.False(plan.IsEmpty);
        Assert.False(plan.FallbackUsed);
        Assert.Equal("SingleNode", plan.LayoutAlgorithm);
        Assert.Single(plan.Nodes);
        Assert.Single(plan.Placements);
        Assert.True(plan.Placements.ContainsKey("target:1"));
    }

    [Fact]
    public void BuildRenderPlan_SkipsInvalidEdgeReferences()
    {
        var graph = BuildGraph(
            [
                BuildNode("target:1", AssociationGraphNodeKind.Target, "Alpha"),
                BuildNode("target:2", AssociationGraphNodeKind.Target, "Bravo")
            ],
            [
                BuildEdge("edge:valid", "target:1", "target:2"),
                BuildEdge("edge:missing", "target:1", "target:404"),
                BuildEdge("edge:self", "target:2", "target:2")
            ]
        );

        var plan = AssociationGraphRenderPipeline.BuildRenderPlan(graph);

        Assert.False(plan.IsEmpty);
        Assert.Equal(2, plan.Nodes.Count);
        Assert.Single(plan.Edges);
        Assert.Equal(2, plan.InvalidEdgeCount);
    }

    [Fact]
    public void BuildRenderPlan_SkipsMalformedNodesAndDoesNotThrow()
    {
        var graph = BuildGraph(
            [
                BuildNode("target:1", AssociationGraphNodeKind.Target, "Alpha"),
                BuildNode(" ", AssociationGraphNodeKind.Target, "Missing Id"),
                BuildNode("target:1", AssociationGraphNodeKind.Target, "Duplicate Id"),
                new AssociationGraphNode(
                    "identifier:1",
                    AssociationGraphNodeKind.Identifier,
                    null!,
                    null,
                    Guid.NewGuid(),
                    null,
                    TargetIdentifierType.Phone,
                    null,
                    null,
                    0,
                    0,
                    0,
                    0,
                    null,
                    null!,
                    null!
                )
            ],
            [
                BuildEdge("edge:1", "target:1", "identifier:1")
            ]
        );

        var plan = AssociationGraphRenderPipeline.BuildRenderPlan(graph);

        Assert.False(plan.IsEmpty);
        Assert.Equal(2, plan.Nodes.Count);
        Assert.Equal(2, plan.InvalidNodeCount);
        Assert.Single(plan.Edges);
        Assert.Contains(plan.Nodes, node => node.NodeId == "identifier:1" && !string.IsNullOrWhiteSpace(node.Label));
    }

    [Fact]
    public void BuildRenderPlan_WhenPreferredLayoutThrows_UsesFallbackWithoutThrowing()
    {
        var graph = BuildGraph(
            [
                BuildNode("target:1", AssociationGraphNodeKind.Target, "Alpha"),
                BuildNode("target:2", AssociationGraphNodeKind.Target, "Bravo")
            ],
            [
                BuildEdge("edge:1", "target:1", "target:2")
            ]
        );

        var plan = AssociationGraphRenderPipeline.BuildRenderPlan(
            graph,
            _ => throw new InvalidOperationException("mds failed")
        );

        Assert.False(plan.IsEmpty);
        Assert.True(plan.FallbackUsed);
        Assert.Equal("FallbackGrid", plan.LayoutAlgorithm);
        Assert.Equal(2, plan.Placements.Count);
    }

    [Fact]
    public void BuildSnapshotExportInfo_WithRenderableGraph_PrefersCanvasSurfaceAndNonZeroCanvasSize()
    {
        var graph = BuildGraph(
            [
                BuildNode("target:1", AssociationGraphNodeKind.Target, "Alpha"),
                BuildNode("target:2", AssociationGraphNodeKind.Target, "Bravo")
            ],
            [
                BuildEdge("edge:1", "target:1", "target:2")
            ]
        );
        var plan = AssociationGraphRenderPipeline.BuildRenderPlan(graph);

        var exportInfo = AssociationGraphRenderPipeline.BuildSnapshotExportInfo(
            plan,
            viewportWidth: 0,
            viewportHeight: 0,
            canvasWidth: 640.4,
            canvasHeight: 480.1
        );

        Assert.Equal("GraphCanvas", exportInfo.PreferredSurface);
        Assert.Equal(641, exportInfo.Width);
        Assert.Equal(481, exportInfo.Height);
        Assert.Equal(plan.Nodes.Count, exportInfo.NodeCount);
        Assert.Equal(plan.Edges.Count, exportInfo.EdgeCount);
        Assert.False(exportInfo.IsEmpty);
    }

    [Fact]
    public void BuildSnapshotExportInfo_WithRenderableGraphAndMissingCanvas_FallsBackToViewportSize()
    {
        var graph = BuildGraph(
            [
                BuildNode("target:1", AssociationGraphNodeKind.Target, "Alpha")
            ],
            []
        );
        var plan = AssociationGraphRenderPipeline.BuildRenderPlan(graph);

        var exportInfo = AssociationGraphRenderPipeline.BuildSnapshotExportInfo(
            plan,
            viewportWidth: 320,
            viewportHeight: 180,
            canvasWidth: 0,
            canvasHeight: 0
        );

        Assert.Equal("GraphCanvas", exportInfo.PreferredSurface);
        Assert.Equal(320, exportInfo.Width);
        Assert.Equal(180, exportInfo.Height);
        Assert.False(exportInfo.IsEmpty);
    }

    private static AssociationGraphResult BuildGraph(
        IReadOnlyList<AssociationGraphNode> nodes,
        IReadOnlyList<AssociationGraphEdge> edges
    )
    {
        return new AssociationGraphResult(
            Guid.NewGuid(),
            new AssociationGraphBuildOptions(true, false, 1),
            nodes,
            edges
        );
    }

    private static AssociationGraphNode BuildNode(
        string nodeId,
        AssociationGraphNodeKind kind,
        string label
    )
    {
        return new AssociationGraphNode(
            nodeId,
            kind,
            label,
            kind == AssociationGraphNodeKind.Target ? Guid.NewGuid() : null,
            kind == AssociationGraphNodeKind.Identifier ? Guid.NewGuid() : null,
            kind == AssociationGraphNodeKind.GlobalPerson ? Guid.NewGuid() : null,
            kind == AssociationGraphNodeKind.Identifier ? TargetIdentifierType.Phone : null,
            null,
            null,
            0,
            0,
            0,
            0,
            null,
            Array.Empty<Guid>(),
            Array.Empty<Guid>()
        );
    }

    private static AssociationGraphEdge BuildEdge(string edgeId, string sourceNodeId, string targetNodeId)
    {
        return new AssociationGraphEdge(
            edgeId,
            sourceNodeId,
            targetNodeId,
            AssociationGraphEdgeKind.TargetTarget,
            1,
            1,
            1,
            null
        );
    }
}
