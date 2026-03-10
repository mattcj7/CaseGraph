using CaseGraph.Core.Diagnostics;
using CaseGraph.Core.Models;
using Microsoft.Msagl.Drawing;
using Microsoft.Msagl.Layout.MDS;
using Microsoft.Msagl.Miscellaneous;
using MsaglColor = Microsoft.Msagl.Drawing.Color;

namespace CaseGraph.App.Views.Pages;

internal static class AssociationGraphRenderPipeline
{
    private const double MinimumNodeWidth = 88;
    private const double MinimumNodeHeight = 40;
    private const double LayoutSpacingX = 160;
    private const double LayoutSpacingY = 120;
    private const double ComponentSpacing = 200;

    public static AssociationGraphRenderPlan BuildRenderPlan(
        AssociationGraphResult? graphResult,
        Func<AssociationGraphComponent, AssociationGraphComponentLayout>? preferredLayoutOverride = null
    )
    {
        if (graphResult is null)
        {
            return EmptyPlan("No graph data is available.");
        }

        var validated = ValidateGraph(graphResult);
        LogPrepared(graphResult, validated);

        if (validated.Nodes.Count == 0)
        {
            var message = graphResult.Nodes.Count == 0
                ? "No association graph nodes to display."
                : "All graph items were invalid and were skipped.";
            var emptyPlan = EmptyPlan(
                message,
                graphResult.CaseId,
                validated.InvalidNodeCount,
                validated.InvalidEdgeCount,
                validated.ComponentCount
            );
            LogCompleted(emptyPlan);
            return emptyPlan;
        }

        var layouts = new List<AssociationGraphComponentLayout>(validated.Components.Count);
        var fallbackUsed = false;
        foreach (var component in validated.Components)
        {
            if (component.Nodes.Count == 1)
            {
                layouts.Add(LayoutSingleNode(component.Nodes[0]));
                continue;
            }

            try
            {
                layouts.Add((preferredLayoutOverride ?? LayoutPreferred)(component));
            }
            catch (Exception ex)
            {
                fallbackUsed = true;
                LogFallback(graphResult, component, ex);
                layouts.Add(LayoutFallback(component));
            }
        }

        var placements = ArrangeComponents(layouts);
        var layoutAlgorithm = string.Join(
            ",",
            layouts.Select(layout => layout.Algorithm).Distinct(StringComparer.Ordinal)
        );
        var renderedEdges = validated.Edges
            .Where(edge => placements.ContainsKey(edge.SourceNodeId) && placements.ContainsKey(edge.TargetNodeId))
            .ToList();

        var plan = new AssociationGraphRenderPlan(
            graphResult.CaseId,
            validated.Nodes,
            renderedEdges,
            placements,
            validated.InvalidNodeCount,
            validated.InvalidEdgeCount,
            validated.ComponentCount,
            layoutAlgorithm,
            fallbackUsed,
            IsEmpty: false,
            Message: string.Empty
        );
        LogCompleted(plan);
        return plan;
    }

    public static AssociationGraphSnapshotExportInfo BuildSnapshotExportInfo(
        AssociationGraphRenderPlan? renderPlan,
        double viewportWidth,
        double viewportHeight,
        double canvasWidth,
        double canvasHeight
    )
    {
        var normalizedViewportWidth = NormalizePixelDimension(viewportWidth);
        var normalizedViewportHeight = NormalizePixelDimension(viewportHeight);
        var normalizedCanvasWidth = NormalizePixelDimension(canvasWidth);
        var normalizedCanvasHeight = NormalizePixelDimension(canvasHeight);
        var hasRenderableGraph = renderPlan is { IsEmpty: false } plan
            && plan.Nodes.Count > 0
            && plan.Placements.Count > 0;

        var exportWidth = hasRenderableGraph
            ? normalizedCanvasWidth > 0 ? normalizedCanvasWidth : normalizedViewportWidth
            : Math.Max(normalizedViewportWidth, normalizedCanvasWidth);
        var exportHeight = hasRenderableGraph
            ? normalizedCanvasHeight > 0 ? normalizedCanvasHeight : normalizedViewportHeight
            : Math.Max(normalizedViewportHeight, normalizedCanvasHeight);

        return new AssociationGraphSnapshotExportInfo(
            Width: Math.Max(exportWidth, 1),
            Height: Math.Max(exportHeight, 1),
            PreferredSurface: hasRenderableGraph ? "GraphCanvas" : "GraphViewport",
            NodeCount: renderPlan?.Nodes.Count ?? 0,
            EdgeCount: renderPlan?.Edges.Count ?? 0,
            LayoutAlgorithm: renderPlan?.LayoutAlgorithm ?? "Unknown",
            FallbackUsed: renderPlan?.FallbackUsed ?? false,
            IsEmpty: renderPlan?.IsEmpty ?? true
        );
    }

    private static ValidatedAssociationGraph ValidateGraph(AssociationGraphResult graphResult)
    {
        var nodes = new List<AssociationGraphNode>(graphResult.Nodes.Count);
        var nodesById = new Dictionary<string, AssociationGraphNode>(StringComparer.Ordinal);
        var invalidNodeCount = 0;

        foreach (var node in graphResult.Nodes)
        {
            var normalized = NormalizeNode(node);
            if (normalized is null || nodesById.ContainsKey(normalized.NodeId))
            {
                invalidNodeCount++;
                continue;
            }

            nodes.Add(normalized);
            nodesById[normalized.NodeId] = normalized;
        }

        var edges = new List<AssociationGraphEdge>(graphResult.Edges.Count);
        var invalidEdgeCount = 0;
        for (var i = 0; i < graphResult.Edges.Count; i++)
        {
            var normalized = NormalizeEdge(graphResult.Edges[i], i, nodesById);
            if (normalized is null)
            {
                invalidEdgeCount++;
                continue;
            }

            edges.Add(normalized);
        }

        var components = BuildComponents(nodes, edges);
        return new ValidatedAssociationGraph(nodes, edges, invalidNodeCount, invalidEdgeCount, components);
    }

    private static AssociationGraphNode? NormalizeNode(AssociationGraphNode node)
    {
        var nodeId = node.NodeId?.Trim();
        if (string.IsNullOrWhiteSpace(nodeId))
        {
            return null;
        }

        var label = string.IsNullOrWhiteSpace(node.Label)
            ? BuildFallbackLabel(node)
            : node.Label.Trim();

        return node with
        {
            NodeId = nodeId,
            Label = label,
            MessageEventCount = Math.Max(0, node.MessageEventCount),
            ConnectionCount = Math.Max(0, node.ConnectionCount),
            LinkedIdentifierCount = Math.Max(0, node.LinkedIdentifierCount),
            ContributingTargetCount = Math.Max(0, node.ContributingTargetCount),
            ContributingTargetIds = NormalizeGuidList(node.ContributingTargetIds),
            ContributingIdentifierIds = NormalizeGuidList(node.ContributingIdentifierIds)
        };
    }

    private static AssociationGraphEdge? NormalizeEdge(
        AssociationGraphEdge edge,
        int index,
        IReadOnlyDictionary<string, AssociationGraphNode> nodesById
    )
    {
        var sourceNodeId = edge.SourceNodeId?.Trim();
        var targetNodeId = edge.TargetNodeId?.Trim();
        if (string.IsNullOrWhiteSpace(sourceNodeId)
            || string.IsNullOrWhiteSpace(targetNodeId)
            || string.Equals(sourceNodeId, targetNodeId, StringComparison.Ordinal)
            || !nodesById.ContainsKey(sourceNodeId)
            || !nodesById.ContainsKey(targetNodeId))
        {
            return null;
        }

        var edgeId = string.IsNullOrWhiteSpace(edge.EdgeId)
            ? $"edge:{index}:{sourceNodeId}|{targetNodeId}|{edge.Kind}"
            : edge.EdgeId.Trim();

        return edge with
        {
            EdgeId = edgeId,
            SourceNodeId = sourceNodeId,
            TargetNodeId = targetNodeId,
            Weight = Math.Max(0, edge.Weight),
            DistinctThreadCount = Math.Max(0, edge.DistinctThreadCount),
            DistinctEventCount = Math.Max(0, edge.DistinctEventCount)
        };
    }

    private static Dictionary<string, AssociationGraphNodePlacement> ArrangeComponents(
        IReadOnlyList<AssociationGraphComponentLayout> layouts
    )
    {
        var placements = new Dictionary<string, AssociationGraphNodePlacement>(StringComparer.Ordinal);
        var offsetX = 0d;

        foreach (var layout in layouts)
        {
            foreach (var placement in layout.Placements.Values)
            {
                placements[placement.NodeId] = placement with
                {
                    CenterX = placement.CenterX + offsetX
                };
            }

            offsetX += layout.Width + ComponentSpacing;
        }

        return placements;
    }

    private static AssociationGraphComponentLayout LayoutSingleNode(AssociationGraphNode node)
    {
        var (width, height) = EstimateNodeSize(node.Label);
        return new AssociationGraphComponentLayout(
            "SingleNode",
            new Dictionary<string, AssociationGraphNodePlacement>(StringComparer.Ordinal)
            {
                [node.NodeId] = new AssociationGraphNodePlacement(node.NodeId, width / 2d, height / 2d, width, height)
            },
            width,
            height
        );
    }

    private static AssociationGraphComponentLayout LayoutPreferred(AssociationGraphComponent component)
    {
        var graph = new Graph
        {
            LayoutAlgorithmSettings = new MdsLayoutSettings()
        };

        var nodeMap = new Dictionary<string, Node>(StringComparer.Ordinal);
        foreach (var node in component.Nodes)
        {
            var drawingNode = graph.AddNode(node.NodeId);
            drawingNode.LabelText = node.Label;
            drawingNode.Attr.Shape = Shape.Box;
            drawingNode.Attr.Color = MsaglColor.Gray;
            drawingNode.Attr.FillColor = ResolveFillColor(node);
            drawingNode.Attr.LabelMargin = 6;
            drawingNode.UserData = node.NodeId;
            nodeMap[node.NodeId] = drawingNode;
        }

        foreach (var edge in component.Edges)
        {
            if (!nodeMap.ContainsKey(edge.SourceNodeId) || !nodeMap.ContainsKey(edge.TargetNodeId))
            {
                continue;
            }

            var drawingEdge = graph.AddEdge(edge.SourceNodeId, edge.TargetNodeId);
            drawingEdge.Attr.ArrowheadAtTarget = ArrowStyle.None;
            drawingEdge.Attr.Color = MsaglColor.DarkSlateGray;
            drawingEdge.Attr.LineWidth = 1.2;
            drawingEdge.LabelText = edge.Weight.ToString();
            drawingEdge.UserData = edge.EdgeId;
        }

        graph.CreateGeometryGraph();
        foreach (var drawingNode in graph.Nodes)
        {
            if (drawingNode.GeometryNode is null || drawingNode.GeometryNode.BoundaryCurve is null)
            {
                throw new InvalidOperationException($"MSAGL geometry was not initialized for node '{drawingNode.Id}'.");
            }
        }

        LayoutHelpers.CalculateLayout(
            graph.GeometryGraph,
            graph.LayoutAlgorithmSettings,
            null,
            "AssociationGraph"
        );

        var rawPlacements = new List<AssociationGraphNodePlacement>(component.Nodes.Count);
        foreach (var drawingNode in graph.Nodes)
        {
            var x = drawingNode.Pos.X;
            var y = drawingNode.Pos.Y;
            if (double.IsNaN(x) || double.IsInfinity(x) || double.IsNaN(y) || double.IsInfinity(y))
            {
                throw new InvalidOperationException($"MSAGL returned a non-finite position for node '{drawingNode.Id}'.");
            }

            var (width, height) = ResolveNodeSize(drawingNode.LabelText);
            rawPlacements.Add(new AssociationGraphNodePlacement(drawingNode.Id, x, y, width, height));
        }

        return NormalizeComponentLayout("MDS", rawPlacements);
    }

    private static AssociationGraphComponentLayout LayoutFallback(AssociationGraphComponent component)
    {
        var orderedNodes = component.Nodes
            .OrderBy(node => node.Kind)
            .ThenBy(node => node.Label, StringComparer.OrdinalIgnoreCase)
            .ThenBy(node => node.NodeId, StringComparer.Ordinal)
            .ToList();

        var columns = Math.Max(1, (int)Math.Ceiling(Math.Sqrt(orderedNodes.Count)));
        var placements = new List<AssociationGraphNodePlacement>(orderedNodes.Count);
        for (var index = 0; index < orderedNodes.Count; index++)
        {
            var node = orderedNodes[index];
            var row = index / columns;
            var column = index % columns;
            var (width, height) = EstimateNodeSize(node.Label);
            placements.Add(
                new AssociationGraphNodePlacement(
                    node.NodeId,
                    (column * LayoutSpacingX) + (width / 2d),
                    (row * LayoutSpacingY) + (height / 2d),
                    width,
                    height
                )
            );
        }

        return NormalizeComponentLayout("FallbackGrid", placements);
    }

    private static AssociationGraphComponentLayout NormalizeComponentLayout(
        string algorithm,
        IReadOnlyList<AssociationGraphNodePlacement> placements
    )
    {
        var minLeft = double.PositiveInfinity;
        var maxRight = double.NegativeInfinity;
        var minTop = double.PositiveInfinity;
        var maxBottom = double.NegativeInfinity;

        foreach (var placement in placements)
        {
            minLeft = Math.Min(minLeft, placement.CenterX - (placement.Width / 2d));
            maxRight = Math.Max(maxRight, placement.CenterX + (placement.Width / 2d));
            minTop = Math.Min(minTop, placement.CenterY - (placement.Height / 2d));
            maxBottom = Math.Max(maxBottom, placement.CenterY + (placement.Height / 2d));
        }

        var normalized = new Dictionary<string, AssociationGraphNodePlacement>(StringComparer.Ordinal);
        foreach (var placement in placements)
        {
            normalized[placement.NodeId] = placement with
            {
                CenterX = placement.CenterX - minLeft,
                CenterY = placement.CenterY - minTop
            };
        }

        return new AssociationGraphComponentLayout(
            algorithm,
            normalized,
            Math.Max(maxRight - minLeft, MinimumNodeWidth),
            Math.Max(maxBottom - minTop, MinimumNodeHeight)
        );
    }

    private static List<AssociationGraphComponent> BuildComponents(
        IReadOnlyList<AssociationGraphNode> nodes,
        IReadOnlyList<AssociationGraphEdge> edges
    )
    {
        var nodeLookup = nodes.ToDictionary(node => node.NodeId, StringComparer.Ordinal);
        var adjacency = nodes.ToDictionary(
            node => node.NodeId,
            _ => new HashSet<string>(StringComparer.Ordinal),
            StringComparer.Ordinal
        );

        foreach (var edge in edges)
        {
            adjacency[edge.SourceNodeId].Add(edge.TargetNodeId);
            adjacency[edge.TargetNodeId].Add(edge.SourceNodeId);
        }

        var visited = new HashSet<string>(StringComparer.Ordinal);
        var components = new List<AssociationGraphComponent>();
        foreach (var node in nodes.OrderBy(node => node.NodeId, StringComparer.Ordinal))
        {
            if (!visited.Add(node.NodeId))
            {
                continue;
            }

            var queue = new Queue<string>();
            queue.Enqueue(node.NodeId);
            var componentNodeIds = new HashSet<string>(StringComparer.Ordinal) { node.NodeId };

            while (queue.Count > 0)
            {
                var current = queue.Dequeue();
                foreach (var neighbor in adjacency[current])
                {
                    if (visited.Add(neighbor))
                    {
                        componentNodeIds.Add(neighbor);
                        queue.Enqueue(neighbor);
                    }
                }
            }

            var componentNodes = componentNodeIds
                .Select(id => nodeLookup[id])
                .OrderBy(item => item.NodeId, StringComparer.Ordinal)
                .ToList();
            var componentEdges = edges
                .Where(edge => componentNodeIds.Contains(edge.SourceNodeId) && componentNodeIds.Contains(edge.TargetNodeId))
                .OrderBy(edge => edge.EdgeId, StringComparer.Ordinal)
                .ToList();
            components.Add(new AssociationGraphComponent(componentNodes, componentEdges));
        }

        return components;
    }

    private static string BuildFallbackLabel(AssociationGraphNode node)
    {
        return node.Kind switch
        {
            AssociationGraphNodeKind.Target => node.TargetId.HasValue
                ? $"Target {node.TargetId.Value:D}"
                : "Target",
            AssociationGraphNodeKind.Identifier => node.IdentifierValueDisplay
                ?? node.IdentifierValueNormalized
                ?? "Identifier",
            AssociationGraphNodeKind.GlobalPerson => node.GlobalEntityId.HasValue
                ? $"Global Person {node.GlobalEntityId.Value:D}"
                : "Global Person",
            _ => "Graph Node"
        };
    }

    private static IReadOnlyList<Guid> NormalizeGuidList(IReadOnlyList<Guid>? values)
    {
        return values?
            .Where(value => value != Guid.Empty)
            .Distinct()
            .OrderBy(value => value)
            .ToArray()
            ?? Array.Empty<Guid>();
    }

    private static (double Width, double Height) ResolveNodeSize(string? label)
    {
        return EstimateNodeSize(string.IsNullOrWhiteSpace(label) ? "Graph Node" : label.Trim());
    }

    private static (double Width, double Height) EstimateNodeSize(string label)
    {
        var normalized = label.Length == 0 ? "Graph Node" : label;
        var width = Math.Max(MinimumNodeWidth, Math.Min(220, 52 + (normalized.Length * 6.5)));
        var lines = Math.Max(1, (int)Math.Ceiling(normalized.Length / 22d));
        var height = Math.Max(MinimumNodeHeight, 26 + (lines * 16));
        return (width, height);
    }

    private static MsaglColor ResolveFillColor(AssociationGraphNode node)
    {
        return node.Kind switch
        {
            AssociationGraphNodeKind.Target => MsaglColor.LightSteelBlue,
            AssociationGraphNodeKind.Identifier => MsaglColor.Bisque,
            AssociationGraphNodeKind.GlobalPerson => MsaglColor.LightGreen,
            _ => MsaglColor.LightGray
        };
    }

    private static int NormalizePixelDimension(double value)
    {
        if (double.IsNaN(value) || double.IsInfinity(value) || value <= 0)
        {
            return 0;
        }

        return Math.Max((int)Math.Ceiling(value), 1);
    }

    private static AssociationGraphRenderPlan EmptyPlan(
        string message,
        Guid? caseId = null,
        int invalidNodeCount = 0,
        int invalidEdgeCount = 0,
        int componentCount = 0
    )
    {
        return new AssociationGraphRenderPlan(
            caseId,
            Array.Empty<AssociationGraphNode>(),
            Array.Empty<AssociationGraphEdge>(),
            new Dictionary<string, AssociationGraphNodePlacement>(StringComparer.Ordinal),
            invalidNodeCount,
            invalidEdgeCount,
            componentCount,
            "Empty",
            FallbackUsed: false,
            IsEmpty: true,
            Message: message
        );
    }

    private static void LogPrepared(AssociationGraphResult graphResult, ValidatedAssociationGraph validated)
    {
        AppFileLogger.LogEvent(
            eventName: "AssociationGraphRenderPrepared",
            level: "INFO",
            message: "Association graph render validation completed.",
            fields: new Dictionary<string, object?>
            {
                ["caseId"] = graphResult.CaseId.ToString("D"),
                ["nodeCount"] = graphResult.Nodes.Count,
                ["edgeCount"] = graphResult.Edges.Count,
                ["validNodeCount"] = validated.Nodes.Count,
                ["validEdgeCount"] = validated.Edges.Count,
                ["invalidNodeCount"] = validated.InvalidNodeCount,
                ["invalidEdgeCount"] = validated.InvalidEdgeCount,
                ["componentCount"] = validated.ComponentCount
            }
        );
    }

    private static void LogFallback(
        AssociationGraphResult graphResult,
        AssociationGraphComponent component,
        Exception ex
    )
    {
        AppFileLogger.LogEvent(
            eventName: "AssociationGraphLayoutFallback",
            level: "WARN",
            message: "Association graph preferred layout failed. Using fallback layout.",
            ex: ex,
            fields: new Dictionary<string, object?>
            {
                ["caseId"] = graphResult.CaseId.ToString("D"),
                ["componentNodeCount"] = component.Nodes.Count,
                ["componentEdgeCount"] = component.Edges.Count,
                ["preferredLayout"] = "MDS",
                ["fallbackLayout"] = "FallbackGrid"
            }
        );
    }

    private static void LogCompleted(AssociationGraphRenderPlan plan)
    {
        AppFileLogger.LogEvent(
            eventName: "AssociationGraphRenderCompleted",
            level: "INFO",
            message: "Association graph render plan built.",
            fields: new Dictionary<string, object?>
            {
                ["caseId"] = plan.CaseId?.ToString("D"),
                ["nodeCount"] = plan.Nodes.Count,
                ["edgeCount"] = plan.Edges.Count,
                ["invalidNodeCount"] = plan.InvalidNodeCount,
                ["invalidEdgeCount"] = plan.InvalidEdgeCount,
                ["componentCount"] = plan.ComponentCount,
                ["layoutAlgorithm"] = plan.LayoutAlgorithm,
                ["fallbackUsed"] = plan.FallbackUsed,
                ["isEmpty"] = plan.IsEmpty
            }
        );
    }

    internal sealed record AssociationGraphRenderPlan(
        Guid? CaseId,
        IReadOnlyList<AssociationGraphNode> Nodes,
        IReadOnlyList<AssociationGraphEdge> Edges,
        IReadOnlyDictionary<string, AssociationGraphNodePlacement> Placements,
        int InvalidNodeCount,
        int InvalidEdgeCount,
        int ComponentCount,
        string LayoutAlgorithm,
        bool FallbackUsed,
        bool IsEmpty,
        string Message
    );

    internal sealed record AssociationGraphSnapshotExportInfo(
        int Width,
        int Height,
        string PreferredSurface,
        int NodeCount,
        int EdgeCount,
        string LayoutAlgorithm,
        bool FallbackUsed,
        bool IsEmpty
    );

    internal sealed record AssociationGraphNodePlacement(
        string NodeId,
        double CenterX,
        double CenterY,
        double Width,
        double Height
    );

    internal sealed record AssociationGraphComponent(
        IReadOnlyList<AssociationGraphNode> Nodes,
        IReadOnlyList<AssociationGraphEdge> Edges
    );

    internal sealed record AssociationGraphComponentLayout(
        string Algorithm,
        IReadOnlyDictionary<string, AssociationGraphNodePlacement> Placements,
        double Width,
        double Height
    );

    private sealed record ValidatedAssociationGraph(
        IReadOnlyList<AssociationGraphNode> Nodes,
        IReadOnlyList<AssociationGraphEdge> Edges,
        int InvalidNodeCount,
        int InvalidEdgeCount,
        IReadOnlyList<AssociationGraphComponent> Components
    )
    {
        public int ComponentCount => Components.Count;
    }
}
