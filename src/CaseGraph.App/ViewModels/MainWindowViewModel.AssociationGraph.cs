using CaseGraph.App.Models;
using CaseGraph.Core.Diagnostics;
using CaseGraph.Core.Models;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace CaseGraph.App.ViewModels;

public partial class MainWindowViewModel
{
    private AssociationGraphResult? _associationGraphResult;
    private AssociationGraphNode? _selectedAssociationGraphNode;
    private Func<string, Task>? _associationGraphSnapshotExporterAsync;
    private Guid? _associationGraphCaseId;
    private IAsyncRelayCommand? _rebuildAssociationGraphCommand;
    private IAsyncRelayCommand? _exportAssociationGraphSnapshotCommand;
    private IAsyncRelayCommand? _openAssociationGraphSelectedTargetCommand;
    private IAsyncRelayCommand? _searchAssociationGraphSelectionCommand;

    [ObservableProperty]
    private bool associationGraphIncludeIdentifiers = true;

    [ObservableProperty]
    private bool associationGraphGroupByGlobalPerson;

    [ObservableProperty]
    private int associationGraphMinEdgeWeight = 2;

    [ObservableProperty]
    private string associationGraphNodeSearchQuery = string.Empty;

    [ObservableProperty]
    private string associationGraphStatusText = "Open a case and click Rebuild Graph.";

    [ObservableProperty]
    private string associationGraphLastExportPath = string.Empty;

    [ObservableProperty]
    private string associationGraphSelectedNodeTitle = "No node selected.";

    [ObservableProperty]
    private string associationGraphSelectedNodeKind = "(none)";

    [ObservableProperty]
    private string associationGraphSelectedNodeSummary = "Select a node to inspect details.";

    [ObservableProperty]
    private string associationGraphSelectedNodeLastSeen = "(none)";

    [ObservableProperty]
    private string associationGraphEdgeHoverText = "Hover an edge to inspect weight and interpretation.";

    public event EventHandler? AssociationGraphRenderRequested;

    public AssociationGraphResult? AssociationGraphResult => _associationGraphResult;

    public bool HasAssociationGraphSelectedNode => _selectedAssociationGraphNode is not null;

    public bool CanOpenAssociationGraphSelectedTarget =>
        _selectedAssociationGraphNode?.Kind == AssociationGraphNodeKind.Target
        && _selectedAssociationGraphNode.TargetId.HasValue;

    public bool CanSearchAssociationGraphSelection => _selectedAssociationGraphNode is not null;

    public IAsyncRelayCommand RebuildAssociationGraphCommand => _rebuildAssociationGraphCommand
        ??= CreateSafeAsyncCommand("RebuildAssociationGraph", RebuildAssociationGraphAsync);

    public IAsyncRelayCommand ExportAssociationGraphSnapshotCommand => _exportAssociationGraphSnapshotCommand
        ??= CreateSafeAsyncCommand("ExportAssociationGraphSnapshot", ExportAssociationGraphSnapshotAsync);

    public IAsyncRelayCommand OpenAssociationGraphSelectedTargetCommand => _openAssociationGraphSelectedTargetCommand
        ??= CreateSafeAsyncCommand(
            "OpenAssociationGraphSelectedTarget",
            OpenAssociationGraphSelectedTargetAsync,
            () => CanOpenAssociationGraphSelectedTarget
        );

    public IAsyncRelayCommand SearchAssociationGraphSelectionCommand => _searchAssociationGraphSelectionCommand
        ??= CreateSafeAsyncCommand(
            "SearchAssociationGraphSelection",
            SearchAssociationGraphSelectionAsync,
            () => CanSearchAssociationGraphSelection
        );

    public void RegisterAssociationGraphSnapshotExporter(Func<string, Task> exporter)
    {
        _associationGraphSnapshotExporterAsync = exporter;
    }

    public void ClearAssociationGraphSnapshotExporter(Func<string, Task>? exporter = null)
    {
        if (exporter is null || ReferenceEquals(_associationGraphSnapshotExporterAsync, exporter))
        {
            _associationGraphSnapshotExporterAsync = null;
        }
    }

    public void SelectAssociationGraphNode(string? nodeId)
    {
        if (_associationGraphResult is null || string.IsNullOrWhiteSpace(nodeId))
        {
            UpdateAssociationGraphSelection(null);
            return;
        }

        var node = _associationGraphResult.Nodes.FirstOrDefault(
            item => string.Equals(item.NodeId, nodeId, StringComparison.Ordinal)
        );
        if (node is null)
        {
            return;
        }

        UpdateAssociationGraphSelection(node);
    }

    public void SetAssociationGraphHoveredEdge(string? edgeId)
    {
        if (_associationGraphResult is null || string.IsNullOrWhiteSpace(edgeId))
        {
            AssociationGraphEdgeHoverText = "Hover an edge to inspect weight and interpretation.";
            return;
        }

        var edge = _associationGraphResult.Edges.FirstOrDefault(
            item => string.Equals(item.EdgeId, edgeId, StringComparison.Ordinal)
        );
        if (edge is null)
        {
            AssociationGraphEdgeHoverText = "Hover an edge to inspect weight and interpretation.";
            return;
        }

        AssociationGraphEdgeHoverText =
            $"Weight={edge.Weight:0} | Shared threads={edge.DistinctThreadCount:0} | Co-occurrence events={edge.DistinctEventCount:0}";
    }

    public bool TryEnsureAssociationGraphLoaded()
    {
        if (CurrentCaseInfo is null)
        {
            ResetAssociationGraphStateOnCaseChanged(null);
            return false;
        }

        if (_associationGraphResult is not null
            && _associationGraphCaseId == CurrentCaseInfo.CaseId)
        {
            return false;
        }

        RebuildAssociationGraphCommand.Execute(null);
        return true;
    }

    private async Task RebuildAssociationGraphAsync()
    {
        if (CurrentCaseInfo is null)
        {
            ResetAssociationGraphStateOnCaseChanged(null);
            AssociationGraphStatusText = "Open a case before building the association graph.";
            return;
        }

        var options = new AssociationGraphBuildOptions(
            IncludeIdentifiers: AssociationGraphIncludeIdentifiers,
            GroupByGlobalPerson: AssociationGraphGroupByGlobalPerson,
            MinEdgeWeight: Math.Max(0, AssociationGraphMinEdgeWeight)
        );

        var rebuilt = await _associationGraphQueryService.BuildAsync(
            CurrentCaseInfo.CaseId,
            options,
            CancellationToken.None
        );

        _associationGraphResult = rebuilt;
        _associationGraphCaseId = CurrentCaseInfo.CaseId;
        AssociationGraphStatusText = $"Built {rebuilt.Nodes.Count:0} node(s) and {rebuilt.Edges.Count:0} edge(s).";
        AssociationGraphEdgeHoverText = "Hover an edge to inspect weight and interpretation.";

        var selectedNodeId = _selectedAssociationGraphNode?.NodeId;
        var nextNode = rebuilt.Nodes.Count == 0
            ? null
            : rebuilt.Nodes.FirstOrDefault(node =>
                selectedNodeId is not null
                && string.Equals(node.NodeId, selectedNodeId, StringComparison.Ordinal))
                ?? rebuilt.Nodes[0];
        UpdateAssociationGraphSelection(nextNode);
        NotifyAssociationGraphRenderRequested();
    }

    private async Task ExportAssociationGraphSnapshotAsync()
    {
        if (CurrentCaseInfo is null)
        {
            OperationText = "Open a case before exporting the graph snapshot.";
            return;
        }

        if (_associationGraphResult is null || _associationGraphResult.Nodes.Count == 0)
        {
            OperationText = "Build the graph before exporting a snapshot.";
            return;
        }

        if (_associationGraphSnapshotExporterAsync is null)
        {
            OperationText = "Graph viewport is not ready yet. Re-open the Association Graph page and try again.";
            return;
        }

        var exportPath = _associationGraphExportPathBuilder.BuildPath(CurrentCaseInfo.CaseId);
        try
        {
            await _associationGraphSnapshotExporterAsync(exportPath);
        }
        catch (Exception ex)
        {
            AppFileLogger.Log($"[AssociationGraph] Snapshot export failed path={exportPath} error={ex.Message}");
            throw;
        }

        AssociationGraphLastExportPath = exportPath;
        OperationText = $"Association graph snapshot exported: {exportPath}";
        AppFileLogger.Log($"[AssociationGraph] Snapshot exported path={exportPath}");
    }

    private async Task OpenAssociationGraphSelectedTargetAsync()
    {
        if (CurrentCaseInfo is null || _selectedAssociationGraphNode is null)
        {
            OperationText = "Select a target node to open Target details.";
            return;
        }

        if (_selectedAssociationGraphNode.Kind != AssociationGraphNodeKind.Target
            || !_selectedAssociationGraphNode.TargetId.HasValue)
        {
            OperationText = "Open Target is available only for Target nodes.";
            return;
        }

        await RefreshTargetsAsync(CancellationToken.None);
        var targetSummary = Targets.FirstOrDefault(
            target => target.TargetId == _selectedAssociationGraphNode.TargetId.Value
        );
        if (targetSummary is null)
        {
            OperationText = "Selected target is no longer available in this case.";
            return;
        }

        SelectedNavigationItem = NavigationItems.FirstOrDefault(item => item.Page == NavigationPage.PeopleTargets);
        SelectedTargetSummary = targetSummary;
        OperationText = $"Opened target: {targetSummary.DisplayName}.";
    }

    private async Task SearchAssociationGraphSelectionAsync()
    {
        if (CurrentCaseInfo is null || _selectedAssociationGraphNode is null)
        {
            OperationText = "Select a graph node before opening filtered search.";
            return;
        }

        switch (_selectedAssociationGraphNode.Kind)
        {
            case AssociationGraphNodeKind.Target when _selectedAssociationGraphNode.TargetId.HasValue:
                ApplySearchFiltersForTarget(
                    _selectedAssociationGraphNode.TargetId.Value,
                    identifierType: null,
                    query: null
                );
                break;

            case AssociationGraphNodeKind.Identifier:
                ApplySearchFiltersForAssociationGraphIdentifier(_selectedAssociationGraphNode);
                break;

            case AssociationGraphNodeKind.GlobalPerson when _selectedAssociationGraphNode.GlobalEntityId.HasValue:
                ApplySearchFiltersForAssociationGraphGlobalPerson(_selectedAssociationGraphNode.GlobalEntityId.Value);
                break;

            default:
                OperationText = "No supported search action for the selected node.";
                return;
        }

        SelectedNavigationItem = NavigationItems.FirstOrDefault(item => item.Page == NavigationPage.Search);
        await SearchMessagesAsync();
    }

    private void ApplySearchFiltersForAssociationGraphIdentifier(AssociationGraphNode node)
    {
        var inferredType = node.IdentifierType ?? TargetIdentifierType.Other;
        var value = node.IdentifierValueDisplay ?? node.Label;
        var query = BuildParticipantSearchQuery(inferredType, value);
        var singleTargetId = node.ContributingTargetIds.Count == 1
            ? node.ContributingTargetIds[0]
            : (Guid?)null;

        if (singleTargetId.HasValue)
        {
            ApplySearchFiltersForTarget(singleTargetId.Value, inferredType, query);
            return;
        }

        SelectedMessageSearchTargetFilter = SearchTargetFilterOption.AllTargets;
        SelectedMessageSearchGlobalPersonFilter = SearchGlobalPersonFilterOption.AllGlobalPersons;
        SelectedMessageSearchIdentifierType = inferredType.ToString();
        MessageSearchQuery = query;
        MessageSearchSenderFilter = string.Empty;
        MessageSearchRecipientFilter = string.Empty;
        SelectedMessageSearchPlatform = "All";
        SelectedMessageSearchDirection = "Any";
        MessageSearchFromDateLocal = null;
        MessageSearchToDateLocal = null;
    }

    private void ApplySearchFiltersForAssociationGraphGlobalPerson(Guid globalEntityId)
    {
        var globalOption = MessageSearchGlobalPersonFilters.FirstOrDefault(
            option => option.GlobalEntityId == globalEntityId
        );
        if (globalOption is null)
        {
            globalOption = new SearchGlobalPersonFilterOption(
                globalEntityId,
                $"Global Person {globalEntityId:D}"
            );
            MessageSearchGlobalPersonFilters.Add(globalOption);
        }

        SelectedMessageSearchTargetFilter = SearchTargetFilterOption.AllTargets;
        SelectedMessageSearchGlobalPersonFilter = globalOption;
        SelectedMessageSearchIdentifierType = "Any";
        MessageSearchQuery = string.Empty;
        MessageSearchSenderFilter = string.Empty;
        MessageSearchRecipientFilter = string.Empty;
        SelectedMessageSearchPlatform = "All";
        SelectedMessageSearchDirection = "Any";
        MessageSearchFromDateLocal = null;
        MessageSearchToDateLocal = null;
    }

    private void UpdateAssociationGraphSelection(AssociationGraphNode? node)
    {
        _selectedAssociationGraphNode = node;
        OnPropertyChanged(nameof(HasAssociationGraphSelectedNode));
        OnPropertyChanged(nameof(CanOpenAssociationGraphSelectedTarget));
        OnPropertyChanged(nameof(CanSearchAssociationGraphSelection));
        OpenAssociationGraphSelectedTargetCommand.NotifyCanExecuteChanged();
        SearchAssociationGraphSelectionCommand.NotifyCanExecuteChanged();

        if (node is null)
        {
            AssociationGraphSelectedNodeTitle = "No node selected.";
            AssociationGraphSelectedNodeKind = "(none)";
            AssociationGraphSelectedNodeSummary = "Select a node to inspect details.";
            AssociationGraphSelectedNodeLastSeen = "(none)";
            return;
        }

        AssociationGraphSelectedNodeTitle = node.Label;
        AssociationGraphSelectedNodeKind = node.Kind.ToString();
        AssociationGraphSelectedNodeSummary = node.Kind switch
        {
            AssociationGraphNodeKind.Target =>
                $"Connected nodes: {node.ConnectionCount:0} | Linked identifiers: {node.LinkedIdentifierCount:0} | Message events: {node.MessageEventCount:0}",
            AssociationGraphNodeKind.Identifier =>
                $"Connected nodes: {node.ConnectionCount:0} | Contributing targets: {node.ContributingTargetCount:0} | Message events: {node.MessageEventCount:0}",
            AssociationGraphNodeKind.GlobalPerson =>
                $"Connected nodes: {node.ConnectionCount:0} | Contributing targets: {node.ContributingTargetCount:0} | Message events: {node.MessageEventCount:0}",
            _ => $"Connected nodes: {node.ConnectionCount:0}"
        };
        AssociationGraphSelectedNodeLastSeen = node.LastSeenUtc.HasValue
            ? node.LastSeenUtc.Value.ToString("u")
            : "(none)";
    }

    private void NotifyAssociationGraphRenderRequested()
    {
        AssociationGraphRenderRequested?.Invoke(this, EventArgs.Empty);
    }

    private void ResetAssociationGraphStateOnCaseChanged(Guid? caseId)
    {
        _associationGraphCaseId = caseId;
        _associationGraphResult = null;
        AssociationGraphStatusText = caseId.HasValue
            ? "Case changed. Click Rebuild Graph to refresh."
            : "Open a case and click Rebuild Graph.";
        AssociationGraphLastExportPath = string.Empty;
        AssociationGraphEdgeHoverText = "Hover an edge to inspect weight and interpretation.";
        UpdateAssociationGraphSelection(null);
        NotifyAssociationGraphRenderRequested();
    }

    partial void OnAssociationGraphNodeSearchQueryChanged(string value)
    {
        NotifyAssociationGraphRenderRequested();
    }

    partial void OnAssociationGraphMinEdgeWeightChanged(int value)
    {
        if (value < 0)
        {
            AssociationGraphMinEdgeWeight = 0;
        }
    }
}
