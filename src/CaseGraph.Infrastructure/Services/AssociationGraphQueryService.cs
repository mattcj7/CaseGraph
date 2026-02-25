using CaseGraph.Core.Abstractions;
using CaseGraph.Core.Models;
using CaseGraph.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace CaseGraph.Infrastructure.Services;

public sealed class AssociationGraphQueryService : IAssociationGraphQueryService
{
    private readonly IDbContextFactory<WorkspaceDbContext> _dbContextFactory;
    private readonly IWorkspaceDatabaseInitializer _databaseInitializer;

    public AssociationGraphQueryService(
        IDbContextFactory<WorkspaceDbContext> dbContextFactory,
        IWorkspaceDatabaseInitializer databaseInitializer
    )
    {
        _dbContextFactory = dbContextFactory;
        _databaseInitializer = databaseInitializer;
    }

    public async Task<AssociationGraphResult> BuildAsync(
        Guid caseId,
        AssociationGraphBuildOptions options,
        CancellationToken ct
    )
    {
        if (caseId == Guid.Empty)
        {
            throw new ArgumentException("CaseId is required.", nameof(caseId));
        }

        var normalizedOptions = options.Normalize();

        await _databaseInitializer.EnsureInitializedAsync(ct);
        await using var db = await _dbContextFactory.CreateDbContextAsync(ct);

        var targets = await QueryTargetsAsync(db, caseId, ct);
        if (targets.Count == 0)
        {
            return new AssociationGraphResult(
                caseId,
                normalizedOptions,
                Nodes: Array.Empty<AssociationGraphNode>(),
                Edges: Array.Empty<AssociationGraphEdge>()
            );
        }

        var targetPresenceStats = await QueryTargetPresenceStatsAsync(db, caseId, ct);
        var targetEventRows = await QueryTargetEventRowsAsync(db, caseId, ct);
        var targetPairStats = BuildTargetPairStats(targetEventRows);

        var nodeAccumulators = new Dictionary<string, NodeAccumulator>(StringComparer.Ordinal);
        var targetNodeByTargetId = BuildTargetNodes(
            targets,
            targetPresenceStats,
            nodeAccumulators,
            normalizedOptions.GroupByGlobalPerson
        );

        var edgeAccumulators = new Dictionary<EdgeKey, EdgeAccumulator>();

        if (normalizedOptions.IncludeIdentifiers)
        {
            var identifierLinks = await QueryIdentifierLinksAsync(db, caseId, ct);
            var identifierPairStats = await QueryIdentifierPairStatsAsync(db, caseId, ct);
            var identifierTotals = await QueryIdentifierTotalsAsync(db, caseId, ct);

            AddIdentifierNodesAndEdges(
                identifierLinks,
                identifierPairStats,
                identifierTotals,
                targetNodeByTargetId,
                nodeAccumulators,
                edgeAccumulators
            );
        }

        AddTargetTargetEdges(
            targetPairStats,
            normalizedOptions.MinEdgeWeight,
            targetNodeByTargetId,
            edgeAccumulators
        );

        PopulateConnectionCounts(nodeAccumulators, edgeAccumulators.Values);

        var nodes = nodeAccumulators.Values
            .Select(acc => acc.ToNode())
            .OrderBy(node => node.Kind)
            .ThenBy(node => node.Label, StringComparer.OrdinalIgnoreCase)
            .ThenBy(node => node.NodeId, StringComparer.Ordinal)
            .ToList();

        var edges = edgeAccumulators.Values
            .Select(acc => acc.ToEdge())
            .OrderBy(edge => edge.SourceNodeId, StringComparer.Ordinal)
            .ThenBy(edge => edge.TargetNodeId, StringComparer.Ordinal)
            .ThenBy(edge => edge.Kind)
            .ToList();

        return new AssociationGraphResult(caseId, normalizedOptions, nodes, edges);
    }

    private static async Task<List<TargetRow>> QueryTargetsAsync(
        WorkspaceDbContext db,
        Guid caseId,
        CancellationToken ct
    )
    {
        var rows = await db.Targets
            .AsNoTracking()
            .Where(target => target.CaseId == caseId)
            .Select(target => new
            {
                target.TargetId,
                target.DisplayName,
                target.GlobalEntityId,
                GlobalDisplayName = target.GlobalPerson == null
                    ? null
                    : target.GlobalPerson.DisplayName
            })
            .ToListAsync(ct);

        return rows
            .Select(row => new TargetRow(
                row.TargetId,
                row.DisplayName,
                row.GlobalEntityId,
                row.GlobalDisplayName
            ))
            .OrderBy(row => row.TargetId)
            .ToList();
    }

    private static async Task<Dictionary<Guid, PresenceStats>> QueryTargetPresenceStatsAsync(
        WorkspaceDbContext db,
        Guid caseId,
        CancellationToken ct
    )
    {
        var rows = await db.TargetMessagePresences
            .AsNoTracking()
            .Where(row => row.CaseId == caseId)
            .Select(row => new
            {
                row.TargetId,
                row.MessageEventId,
                row.MessageTimestampUtc
            })
            .ToListAsync(ct);

        var stats = new Dictionary<Guid, PresenceStats>();
        foreach (var group in rows.GroupBy(row => row.TargetId))
        {
            var eventCount = group
                .Select(item => item.MessageEventId)
                .Distinct()
                .Count();
            var lastSeenUtc = group.Max(item => item.MessageTimestampUtc);
            stats[group.Key] = new PresenceStats(eventCount, lastSeenUtc);
        }

        return stats;
    }

    private static async Task<List<TargetEventRow>> QueryTargetEventRowsAsync(
        WorkspaceDbContext db,
        Guid caseId,
        CancellationToken ct
    )
    {
        var rows = await (
            from presence in db.TargetMessagePresences.AsNoTracking()
            join message in db.MessageEvents.AsNoTracking()
                on new { presence.CaseId, presence.MessageEventId }
                equals new { message.CaseId, message.MessageEventId }
            where presence.CaseId == caseId
            select new
            {
                presence.TargetId,
                presence.MessageEventId,
                message.ThreadId,
                presence.MessageTimestampUtc
            }
        )
            .Distinct()
            .ToListAsync(ct);

        return rows
            .Select(row => new TargetEventRow(
                row.TargetId,
                row.MessageEventId,
                row.ThreadId,
                row.MessageTimestampUtc
            ))
            .ToList();
    }

    private static List<TargetPairStats> BuildTargetPairStats(IReadOnlyList<TargetEventRow> targetEventRows)
    {
        var map = new Dictionary<TargetPairKey, TargetPairAccumulator>();

        var eventGroups = targetEventRows
            .GroupBy(row => row.MessageEventId)
            .OrderBy(group => group.Key);
        foreach (var eventGroup in eventGroups)
        {
            var orderedTargets = eventGroup
                .Select(row => row.TargetId)
                .Distinct()
                .OrderBy(id => id)
                .ToArray();
            if (orderedTargets.Length < 2)
            {
                continue;
            }

            var lastSeenUtc = eventGroup.Max(row => row.MessageTimestampUtc);

            ForEachTargetPair(orderedTargets, (sourceId, targetId) =>
            {
                var key = new TargetPairKey(sourceId, targetId);
                if (!map.TryGetValue(key, out var accumulator))
                {
                    accumulator = new TargetPairAccumulator();
                    map[key] = accumulator;
                }

                accumulator.DistinctEventCount++;
                accumulator.LastSeenUtc = Max(accumulator.LastSeenUtc, lastSeenUtc);
            });
        }

        var threadGroups = targetEventRows
            .GroupBy(row => row.ThreadId)
            .OrderBy(group => group.Key);
        foreach (var threadGroup in threadGroups)
        {
            var orderedTargets = threadGroup
                .Select(row => row.TargetId)
                .Distinct()
                .OrderBy(id => id)
                .ToArray();
            if (orderedTargets.Length < 2)
            {
                continue;
            }

            ForEachTargetPair(orderedTargets, (sourceId, targetId) =>
            {
                var key = new TargetPairKey(sourceId, targetId);
                if (!map.TryGetValue(key, out var accumulator))
                {
                    accumulator = new TargetPairAccumulator();
                    map[key] = accumulator;
                }

                accumulator.DistinctThreadCount++;
            });
        }

        return map
            .Select(item => new TargetPairStats(
                item.Key.SourceTargetId,
                item.Key.TargetTargetId,
                item.Value.DistinctThreadCount,
                item.Value.DistinctEventCount,
                item.Value.LastSeenUtc
            ))
            .OrderBy(item => item.SourceTargetId)
            .ThenBy(item => item.TargetTargetId)
            .ToList();
    }

    private static async Task<List<IdentifierLinkRow>> QueryIdentifierLinksAsync(
        WorkspaceDbContext db,
        Guid caseId,
        CancellationToken ct
    )
    {
        var rows = await db.TargetIdentifierLinks
            .AsNoTracking()
            .Where(link => link.CaseId == caseId)
            .Select(link => new
            {
                link.TargetId,
                link.IdentifierId,
                IdentifierType = link.Identifier == null ? null : link.Identifier.Type,
                ValueRaw = link.Identifier == null ? null : link.Identifier.ValueRaw,
                ValueNormalized = link.Identifier == null ? null : link.Identifier.ValueNormalized
            })
            .ToListAsync(ct);

        return rows
            .Select(row => new IdentifierLinkRow(
                row.TargetId,
                row.IdentifierId,
                ParseIdentifierType(row.IdentifierType),
                row.ValueRaw ?? string.Empty,
                row.ValueNormalized ?? string.Empty
            ))
            .OrderBy(row => row.TargetId)
            .ThenBy(row => row.IdentifierId)
            .ToList();
    }

    private static async Task<Dictionary<(Guid TargetId, Guid IdentifierId), PresenceStats>> QueryIdentifierPairStatsAsync(
        WorkspaceDbContext db,
        Guid caseId,
        CancellationToken ct
    )
    {
        var rows = await db.TargetMessagePresences
            .AsNoTracking()
            .Where(row => row.CaseId == caseId)
            .Select(row => new
            {
                row.TargetId,
                IdentifierId = row.MatchedIdentifierId,
                row.MessageEventId,
                row.MessageTimestampUtc
            })
            .ToListAsync(ct);

        var stats = new Dictionary<(Guid TargetId, Guid IdentifierId), PresenceStats>();
        foreach (var group in rows.GroupBy(row => (row.TargetId, row.IdentifierId)))
        {
            var eventCount = group
                .Select(item => item.MessageEventId)
                .Distinct()
                .Count();
            var lastSeenUtc = group.Max(item => item.MessageTimestampUtc);
            stats[group.Key] = new PresenceStats(eventCount, lastSeenUtc);
        }

        return stats;
    }

    private static async Task<Dictionary<Guid, PresenceStats>> QueryIdentifierTotalsAsync(
        WorkspaceDbContext db,
        Guid caseId,
        CancellationToken ct
    )
    {
        var rows = await db.TargetMessagePresences
            .AsNoTracking()
            .Where(row => row.CaseId == caseId)
            .Select(row => new
            {
                IdentifierId = row.MatchedIdentifierId,
                row.MessageEventId,
                row.MessageTimestampUtc
            })
            .ToListAsync(ct);

        var stats = new Dictionary<Guid, PresenceStats>();
        foreach (var group in rows.GroupBy(row => row.IdentifierId))
        {
            var eventCount = group
                .Select(item => item.MessageEventId)
                .Distinct()
                .Count();
            var lastSeenUtc = group.Max(item => item.MessageTimestampUtc);
            stats[group.Key] = new PresenceStats(eventCount, lastSeenUtc);
        }

        return stats;
    }

    private static Dictionary<Guid, string> BuildTargetNodes(
        IReadOnlyList<TargetRow> targets,
        IReadOnlyDictionary<Guid, PresenceStats> targetPresenceStats,
        IDictionary<string, NodeAccumulator> nodeAccumulators,
        bool groupByGlobalPerson
    )
    {
        var targetNodeByTargetId = new Dictionary<Guid, string>();

        foreach (var target in targets)
        {
            var nodeId = ResolveTargetNodeId(target, groupByGlobalPerson);
            targetNodeByTargetId[target.TargetId] = nodeId;

            if (!nodeAccumulators.TryGetValue(nodeId, out var node))
            {
                node = NodeAccumulator.ForTarget(target, groupByGlobalPerson);
                nodeAccumulators[nodeId] = node;
            }

            node.AddTarget(target.TargetId);
            if (targetPresenceStats.TryGetValue(target.TargetId, out var stats))
            {
                node.AddMessageStats(stats.EventCount, stats.LastSeenUtc);
            }
        }

        return targetNodeByTargetId;
    }

    private static void AddIdentifierNodesAndEdges(
        IReadOnlyList<IdentifierLinkRow> identifierLinks,
        IReadOnlyDictionary<(Guid TargetId, Guid IdentifierId), PresenceStats> identifierPairStats,
        IReadOnlyDictionary<Guid, PresenceStats> identifierTotals,
        IReadOnlyDictionary<Guid, string> targetNodeByTargetId,
        IDictionary<string, NodeAccumulator> nodeAccumulators,
        IDictionary<EdgeKey, EdgeAccumulator> edgeAccumulators
    )
    {
        foreach (var link in identifierLinks)
        {
            if (!targetNodeByTargetId.TryGetValue(link.TargetId, out var targetNodeId))
            {
                continue;
            }

            var identifierNodeId = BuildIdentifierNodeId(link.IdentifierId);
            if (!nodeAccumulators.TryGetValue(identifierNodeId, out var identifierNode))
            {
                identifierNode = NodeAccumulator.ForIdentifier(link, identifierNodeId);
                nodeAccumulators[identifierNodeId] = identifierNode;
            }

            identifierNode.AddTarget(link.TargetId);
            if (identifierTotals.TryGetValue(link.IdentifierId, out var totalStats))
            {
                identifierNode.OverrideMessageStats(totalStats.EventCount, totalStats.LastSeenUtc);
            }

            var targetNode = nodeAccumulators[targetNodeId];
            targetNode.AddIdentifier(link.IdentifierId);

            var pairStats = identifierPairStats.TryGetValue((link.TargetId, link.IdentifierId), out var stats)
                ? stats
                : new PresenceStats(0, null);
            var edgeWeight = pairStats.EventCount > 0 ? pairStats.EventCount : 1;

            var edgeKey = EdgeKey.Create(targetNodeId, identifierNodeId, AssociationGraphEdgeKind.TargetIdentifier);
            if (!edgeAccumulators.TryGetValue(edgeKey, out var edge))
            {
                edge = EdgeAccumulator.Create(edgeKey);
                edgeAccumulators[edgeKey] = edge;
            }

            edge.Accumulate(
                weight: edgeWeight,
                distinctThreadCount: 0,
                distinctEventCount: pairStats.EventCount,
                pairStats.LastSeenUtc
            );
        }
    }

    private static void AddTargetTargetEdges(
        IReadOnlyList<TargetPairStats> targetPairStats,
        int minEdgeWeight,
        IReadOnlyDictionary<Guid, string> targetNodeByTargetId,
        IDictionary<EdgeKey, EdgeAccumulator> edgeAccumulators
    )
    {
        foreach (var stats in targetPairStats)
        {
            if (!targetNodeByTargetId.TryGetValue(stats.SourceTargetId, out var sourceNodeId)
                || !targetNodeByTargetId.TryGetValue(stats.TargetTargetId, out var targetNodeId))
            {
                continue;
            }

            if (string.Equals(sourceNodeId, targetNodeId, StringComparison.Ordinal))
            {
                continue;
            }

            var weight = stats.DistinctThreadCount > 0
                ? stats.DistinctThreadCount
                : stats.DistinctEventCount;
            if (weight < minEdgeWeight)
            {
                continue;
            }

            var edgeKey = EdgeKey.Create(sourceNodeId, targetNodeId, AssociationGraphEdgeKind.TargetTarget);
            if (!edgeAccumulators.TryGetValue(edgeKey, out var edge))
            {
                edge = EdgeAccumulator.Create(edgeKey);
                edgeAccumulators[edgeKey] = edge;
            }

            edge.Accumulate(
                weight,
                stats.DistinctThreadCount,
                stats.DistinctEventCount,
                stats.LastSeenUtc
            );
        }
    }

    private static void PopulateConnectionCounts(
        IDictionary<string, NodeAccumulator> nodeAccumulators,
        IEnumerable<EdgeAccumulator> edges
    )
    {
        foreach (var edge in edges)
        {
            if (nodeAccumulators.TryGetValue(edge.SourceNodeId, out var source))
            {
                source.AddConnection(edge.TargetNodeId);
            }

            if (nodeAccumulators.TryGetValue(edge.TargetNodeId, out var target))
            {
                target.AddConnection(edge.SourceNodeId);
            }
        }
    }

    private static string ResolveTargetNodeId(TargetRow target, bool groupByGlobalPerson)
    {
        if (groupByGlobalPerson && target.GlobalEntityId.HasValue)
        {
            return BuildGlobalPersonNodeId(target.GlobalEntityId.Value);
        }

        return BuildTargetNodeId(target.TargetId);
    }

    private static string BuildTargetNodeId(Guid targetId)
    {
        return $"target:{targetId:D}";
    }

    private static string BuildIdentifierNodeId(Guid identifierId)
    {
        return $"identifier:{identifierId:D}";
    }

    private static string BuildGlobalPersonNodeId(Guid globalEntityId)
    {
        return $"person:{globalEntityId:D}";
    }

    private static TargetIdentifierType ParseIdentifierType(string? value)
    {
        return Enum.TryParse<TargetIdentifierType>(value, ignoreCase: true, out var parsed)
            ? parsed
            : TargetIdentifierType.Other;
    }

    private static void ForEachTargetPair(
        Guid[] orderedTargetIds,
        Action<Guid, Guid> onPair
    )
    {
        for (var i = 0; i < orderedTargetIds.Length - 1; i++)
        {
            for (var j = i + 1; j < orderedTargetIds.Length; j++)
            {
                onPair(orderedTargetIds[i], orderedTargetIds[j]);
            }
        }
    }

    private static DateTimeOffset? Max(DateTimeOffset? current, DateTimeOffset? candidate)
    {
        if (!current.HasValue)
        {
            return candidate;
        }

        if (!candidate.HasValue)
        {
            return current;
        }

        return candidate.Value >= current.Value
            ? candidate
            : current;
    }

    private sealed record TargetRow(
        Guid TargetId,
        string DisplayName,
        Guid? GlobalEntityId,
        string? GlobalDisplayName
    );

    private sealed record IdentifierLinkRow(
        Guid TargetId,
        Guid IdentifierId,
        TargetIdentifierType IdentifierType,
        string ValueRaw,
        string ValueNormalized
    );

    private sealed record TargetEventRow(
        Guid TargetId,
        Guid MessageEventId,
        Guid ThreadId,
        DateTimeOffset? MessageTimestampUtc
    );

    private sealed record PresenceStats(int EventCount, DateTimeOffset? LastSeenUtc);

    private sealed record TargetPairStats(
        Guid SourceTargetId,
        Guid TargetTargetId,
        int DistinctThreadCount,
        int DistinctEventCount,
        DateTimeOffset? LastSeenUtc
    );

    private sealed record TargetPairKey(Guid SourceTargetId, Guid TargetTargetId);

    private sealed class TargetPairAccumulator
    {
        public int DistinctThreadCount { get; set; }

        public int DistinctEventCount { get; set; }

        public DateTimeOffset? LastSeenUtc { get; set; }
    }

    private sealed class NodeAccumulator
    {
        private readonly HashSet<Guid> _contributingTargetIds = [];
        private readonly HashSet<Guid> _contributingIdentifierIds = [];
        private readonly HashSet<string> _connectedNodeIds = [];

        private NodeAccumulator(
            string nodeId,
            AssociationGraphNodeKind kind,
            string label,
            Guid? targetId,
            Guid? identifierId,
            Guid? globalEntityId,
            TargetIdentifierType? identifierType,
            string? identifierValueDisplay,
            string? identifierValueNormalized
        )
        {
            NodeId = nodeId;
            Kind = kind;
            Label = label;
            TargetId = targetId;
            IdentifierId = identifierId;
            GlobalEntityId = globalEntityId;
            IdentifierType = identifierType;
            IdentifierValueDisplay = identifierValueDisplay;
            IdentifierValueNormalized = identifierValueNormalized;
        }

        public string NodeId { get; }

        public AssociationGraphNodeKind Kind { get; }

        public string Label { get; private set; }

        public Guid? TargetId { get; }

        public Guid? IdentifierId { get; }

        public Guid? GlobalEntityId { get; }

        public TargetIdentifierType? IdentifierType { get; }

        public string? IdentifierValueDisplay { get; }

        public string? IdentifierValueNormalized { get; }

        public int MessageEventCount { get; private set; }

        public DateTimeOffset? LastSeenUtc { get; private set; }

        public static NodeAccumulator ForTarget(TargetRow target, bool groupedByGlobalPerson)
        {
            if (groupedByGlobalPerson && target.GlobalEntityId.HasValue)
            {
                var label = string.IsNullOrWhiteSpace(target.GlobalDisplayName)
                    ? $"Global Person {target.GlobalEntityId.Value:D}"
                    : target.GlobalDisplayName.Trim();

                return new NodeAccumulator(
                    nodeId: BuildGlobalPersonNodeId(target.GlobalEntityId.Value),
                    kind: AssociationGraphNodeKind.GlobalPerson,
                    label: label,
                    targetId: null,
                    identifierId: null,
                    globalEntityId: target.GlobalEntityId,
                    identifierType: null,
                    identifierValueDisplay: null,
                    identifierValueNormalized: null
                );
            }

            return new NodeAccumulator(
                nodeId: BuildTargetNodeId(target.TargetId),
                kind: AssociationGraphNodeKind.Target,
                label: target.DisplayName,
                targetId: target.TargetId,
                identifierId: null,
                globalEntityId: target.GlobalEntityId,
                identifierType: null,
                identifierValueDisplay: null,
                identifierValueNormalized: null
            );
        }

        public static NodeAccumulator ForIdentifier(IdentifierLinkRow link, string nodeId)
        {
            return new NodeAccumulator(
                nodeId,
                AssociationGraphNodeKind.Identifier,
                $"{link.IdentifierType}: {link.ValueRaw}",
                targetId: null,
                identifierId: link.IdentifierId,
                globalEntityId: null,
                identifierType: link.IdentifierType,
                identifierValueDisplay: link.ValueRaw,
                identifierValueNormalized: link.ValueNormalized
            );
        }

        public void AddTarget(Guid targetId)
        {
            _contributingTargetIds.Add(targetId);
        }

        public void AddIdentifier(Guid identifierId)
        {
            _contributingIdentifierIds.Add(identifierId);
        }

        public void AddConnection(string otherNodeId)
        {
            _connectedNodeIds.Add(otherNodeId);
        }

        public void AddMessageStats(int eventCount, DateTimeOffset? lastSeenUtc)
        {
            MessageEventCount += Math.Max(0, eventCount);
            LastSeenUtc = Max(LastSeenUtc, lastSeenUtc);
        }

        public void OverrideMessageStats(int eventCount, DateTimeOffset? lastSeenUtc)
        {
            MessageEventCount = Math.Max(0, eventCount);
            LastSeenUtc = lastSeenUtc;
        }

        public AssociationGraphNode ToNode()
        {
            return new AssociationGraphNode(
                NodeId,
                Kind,
                Label,
                TargetId,
                IdentifierId,
                GlobalEntityId,
                IdentifierType,
                IdentifierValueDisplay,
                IdentifierValueNormalized,
                MessageEventCount,
                ConnectionCount: _connectedNodeIds.Count,
                LinkedIdentifierCount: _contributingIdentifierIds.Count,
                ContributingTargetCount: _contributingTargetIds.Count,
                LastSeenUtc,
                ContributingTargetIds: _contributingTargetIds
                    .OrderBy(id => id)
                    .ToArray(),
                ContributingIdentifierIds: _contributingIdentifierIds
                    .OrderBy(id => id)
                    .ToArray()
            );
        }
    }

    private readonly record struct EdgeKey(
        string SourceNodeId,
        string TargetNodeId,
        AssociationGraphEdgeKind Kind
    )
    {
        public static EdgeKey Create(
            string firstNodeId,
            string secondNodeId,
            AssociationGraphEdgeKind kind
        )
        {
            return string.CompareOrdinal(firstNodeId, secondNodeId) <= 0
                ? new EdgeKey(firstNodeId, secondNodeId, kind)
                : new EdgeKey(secondNodeId, firstNodeId, kind);
        }
    }

    private sealed class EdgeAccumulator
    {
        private EdgeAccumulator(
            string sourceNodeId,
            string targetNodeId,
            AssociationGraphEdgeKind kind
        )
        {
            SourceNodeId = sourceNodeId;
            TargetNodeId = targetNodeId;
            Kind = kind;
            EdgeId = $"{kind}:{sourceNodeId}|{targetNodeId}";
        }

        public string EdgeId { get; }

        public string SourceNodeId { get; }

        public string TargetNodeId { get; }

        public AssociationGraphEdgeKind Kind { get; }

        public int Weight { get; private set; }

        public int DistinctThreadCount { get; private set; }

        public int DistinctEventCount { get; private set; }

        public DateTimeOffset? LastSeenUtc { get; private set; }

        public static EdgeAccumulator Create(EdgeKey key)
        {
            return new EdgeAccumulator(key.SourceNodeId, key.TargetNodeId, key.Kind);
        }

        public void Accumulate(
            int weight,
            int distinctThreadCount,
            int distinctEventCount,
            DateTimeOffset? lastSeenUtc
        )
        {
            Weight += Math.Max(0, weight);
            DistinctThreadCount += Math.Max(0, distinctThreadCount);
            DistinctEventCount += Math.Max(0, distinctEventCount);
            LastSeenUtc = Max(LastSeenUtc, lastSeenUtc);
        }

        public AssociationGraphEdge ToEdge()
        {
            return new AssociationGraphEdge(
                EdgeId,
                SourceNodeId,
                TargetNodeId,
                Kind,
                Weight,
                DistinctThreadCount,
                DistinctEventCount,
                LastSeenUtc
            );
        }
    }
}
