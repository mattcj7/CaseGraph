using CaseGraph.App.ViewModels;
using CaseGraph.Core.Models;
using Microsoft.Msagl.Drawing;
using Microsoft.Msagl.Layout.MDS;
using Microsoft.Msagl.Miscellaneous;
using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using MsaglColor = Microsoft.Msagl.Drawing.Color;

namespace CaseGraph.App.Views.Pages;

public partial class AssociationGraphView : UserControl
{
    private const double MinimumNodeWidth = 88;
    private const double MinimumNodeHeight = 40;
    private const double GraphMargin = 36;
    private const double ZoomFactor = 1.12;

    private MainWindowViewModel? _viewModel;
    private bool _isPanning;
    private Point _panStart;
    private double _panOriginX;
    private double _panOriginY;

    public AssociationGraphView()
    {
        InitializeComponent();

        DataContextChanged += OnDataContextChanged;
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;

        GraphViewport.MouseWheel += OnGraphViewportMouseWheel;
        GraphViewport.MouseRightButtonDown += OnGraphViewportMouseRightButtonDown;
        GraphViewport.MouseRightButtonUp += OnGraphViewportMouseRightButtonUp;
        GraphViewport.MouseMove += OnGraphViewportMouseMove;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_viewModel is null)
        {
            AttachViewModel(DataContext as MainWindowViewModel);
        }
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        DetachViewModel();
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        AttachViewModel(e.NewValue as MainWindowViewModel);
    }

    private void AttachViewModel(MainWindowViewModel? viewModel)
    {
        if (ReferenceEquals(_viewModel, viewModel))
        {
            return;
        }

        DetachViewModel();
        _viewModel = viewModel;
        if (_viewModel is null)
        {
            RenderGraph(null);
            return;
        }

        _viewModel.AssociationGraphRenderRequested += OnAssociationGraphRenderRequested;
        _viewModel.PropertyChanged += OnViewModelPropertyChanged;
        _viewModel.RegisterAssociationGraphSnapshotExporter(ExportSnapshotAsync);
        _viewModel.TryEnsureAssociationGraphLoaded();
        RenderGraph(_viewModel.AssociationGraphResult);
    }

    private void DetachViewModel()
    {
        if (_viewModel is null)
        {
            return;
        }

        _viewModel.AssociationGraphRenderRequested -= OnAssociationGraphRenderRequested;
        _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
        _viewModel.ClearAssociationGraphSnapshotExporter(ExportSnapshotAsync);
        _viewModel = null;
    }

    private void OnAssociationGraphRenderRequested(object? sender, EventArgs e)
    {
        RenderGraph(_viewModel?.AssociationGraphResult);
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_viewModel is null)
        {
            return;
        }

        if (string.Equals(e.PropertyName, nameof(MainWindowViewModel.CurrentCaseInfo), StringComparison.Ordinal))
        {
            _viewModel.TryEnsureAssociationGraphLoaded();
        }
    }

    private void RenderGraph(AssociationGraphResult? graphResult)
    {
        GraphCanvas.Children.Clear();
        GraphCanvas.Width = Math.Max(GraphViewport.ActualWidth, 1);
        GraphCanvas.Height = Math.Max(GraphViewport.ActualHeight, 1);

        if (graphResult is null || graphResult.Nodes.Count == 0)
        {
            ResetPanAndZoom();
            return;
        }

        var nodeMap = graphResult.Nodes.ToDictionary(node => node.NodeId, StringComparer.Ordinal);
        var msaglGraph = BuildMsaglLayoutGraph(graphResult);
        var positionedNodes = msaglGraph.Nodes
            .Where(node => nodeMap.ContainsKey(node.Id))
            .ToList();
        if (positionedNodes.Count == 0)
        {
            ResetPanAndZoom();
            return;
        }

        var minX = double.PositiveInfinity;
        var maxX = double.NegativeInfinity;
        var minY = double.PositiveInfinity;
        var maxY = double.NegativeInfinity;

        foreach (var node in positionedNodes)
        {
            var width = Math.Max(node.Width, MinimumNodeWidth);
            var height = Math.Max(node.Height, MinimumNodeHeight);
            minX = Math.Min(minX, node.Pos.X - (width / 2d));
            maxX = Math.Max(maxX, node.Pos.X + (width / 2d));
            minY = Math.Min(minY, node.Pos.Y - (height / 2d));
            maxY = Math.Max(maxY, node.Pos.Y + (height / 2d));
        }

        if (double.IsInfinity(minX) || double.IsInfinity(maxX) || double.IsInfinity(minY) || double.IsInfinity(maxY))
        {
            ResetPanAndZoom();
            return;
        }

        var contentWidth = Math.Max((maxX - minX) + (GraphMargin * 2d), 1);
        var contentHeight = Math.Max((maxY - minY) + (GraphMargin * 2d), 1);
        GraphCanvas.Width = contentWidth;
        GraphCanvas.Height = contentHeight;

        var toCanvasX = new Func<double, double>(x => (x - minX) + GraphMargin);
        var toCanvasY = new Func<double, double>(y => (maxY - y) + GraphMargin);

        foreach (var edge in msaglGraph.Edges)
        {
            if (edge.SourceNode is null || edge.TargetNode is null)
            {
                continue;
            }

            var x1 = toCanvasX(edge.SourceNode.Pos.X);
            var y1 = toCanvasY(edge.SourceNode.Pos.Y);
            var x2 = toCanvasX(edge.TargetNode.Pos.X);
            var y2 = toCanvasY(edge.TargetNode.Pos.Y);

            var edgeId = edge.UserData as string;
            var line = new Line
            {
                X1 = x1,
                Y1 = y1,
                X2 = x2,
                Y2 = y2,
                Stroke = Brushes.DarkSlateGray,
                StrokeThickness = 2,
                Tag = edgeId,
                SnapsToDevicePixels = true
            };
            line.MouseEnter += OnEdgeMouseEnter;
            line.MouseLeave += OnEdgeMouseLeave;
            GraphCanvas.Children.Add(line);

            var label = new TextBlock
            {
                Text = edge.LabelText,
                Foreground = Brushes.DimGray,
                Background = Brushes.WhiteSmoke,
                Padding = new Thickness(3, 0, 3, 0),
                FontSize = 11
            };
            Canvas.SetLeft(label, (x1 + x2) / 2d);
            Canvas.SetTop(label, (y1 + y2) / 2d);
            GraphCanvas.Children.Add(label);
        }

        var nodeSearchQuery = _viewModel?.AssociationGraphNodeSearchQuery?.Trim() ?? string.Empty;
        foreach (var drawingNode in positionedNodes)
        {
            if (!nodeMap.TryGetValue(drawingNode.Id, out var node))
            {
                continue;
            }

            var width = Math.Max(drawingNode.Width, MinimumNodeWidth);
            var height = Math.Max(drawingNode.Height, MinimumNodeHeight);
            var centerX = toCanvasX(drawingNode.Pos.X);
            var centerY = toCanvasY(drawingNode.Pos.Y);

            var highlighted = nodeSearchQuery.Length > 0
                && node.Label.Contains(nodeSearchQuery, StringComparison.OrdinalIgnoreCase);
            var background = new SolidColorBrush(ToMediaColor(ResolveFillColor(node, highlighted)));
            var border = new Border
            {
                Width = width,
                Height = height,
                Background = background,
                BorderBrush = Brushes.Gray,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(4),
                Tag = node.NodeId
            };
            border.MouseLeftButtonUp += OnNodeMouseLeftButtonUp;

            var textBlock = new TextBlock
            {
                Text = node.Label,
                TextWrapping = TextWrapping.Wrap,
                TextAlignment = TextAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(4, 2, 4, 2),
                Foreground = Brushes.Black
            };
            border.Child = textBlock;

            Canvas.SetLeft(border, centerX - (width / 2d));
            Canvas.SetTop(border, centerY - (height / 2d));
            GraphCanvas.Children.Add(border);
        }

        ResetPanAndZoom();
    }

    private static Graph BuildMsaglLayoutGraph(AssociationGraphResult graphResult)
    {
        var graph = new Graph
        {
            LayoutAlgorithmSettings = new MdsLayoutSettings()
        };

        foreach (var node in graphResult.Nodes)
        {
            var drawingNode = graph.AddNode(node.NodeId);
            drawingNode.LabelText = node.Label;
            drawingNode.Attr.Shape = Microsoft.Msagl.Drawing.Shape.Box;
            drawingNode.Attr.Color = MsaglColor.Gray;
            drawingNode.Attr.FillColor = ResolveFillColor(node, highlighted: false);
            drawingNode.Attr.LabelMargin = 6;
            drawingNode.UserData = node.NodeId;
        }

        foreach (var edge in graphResult.Edges)
        {
            var drawingEdge = graph.AddEdge(edge.SourceNodeId, edge.TargetNodeId);
            drawingEdge.Attr.ArrowheadAtTarget = ArrowStyle.None;
            drawingEdge.Attr.Color = MsaglColor.DarkSlateGray;
            drawingEdge.Attr.LineWidth = 1.2;
            drawingEdge.LabelText = edge.Weight.ToString();
            drawingEdge.UserData = edge.EdgeId;
        }

        graph.CreateGeometryGraph();
        LayoutHelpers.CalculateLayout(
            graph.GeometryGraph,
            graph.LayoutAlgorithmSettings,
            null,
            "AssociationGraph"
        );
        return graph;
    }

    private static MsaglColor ResolveFillColor(AssociationGraphNode node, bool highlighted)
    {
        if (highlighted)
        {
            return MsaglColor.Gold;
        }

        return node.Kind switch
        {
            AssociationGraphNodeKind.Target => MsaglColor.LightSteelBlue,
            AssociationGraphNodeKind.Identifier => MsaglColor.Bisque,
            AssociationGraphNodeKind.GlobalPerson => MsaglColor.LightGreen,
            _ => MsaglColor.LightGray
        };
    }

    private static System.Windows.Media.Color ToMediaColor(MsaglColor color)
    {
        return System.Windows.Media.Color.FromArgb(color.A, color.R, color.G, color.B);
    }

    private Task ExportSnapshotAsync(string outputPath)
    {
        var width = Math.Max((int)Math.Ceiling(GraphViewport.ActualWidth), 1);
        var height = Math.Max((int)Math.Ceiling(GraphViewport.ActualHeight), 1);
        var bitmap = new RenderTargetBitmap(
            width,
            height,
            96,
            96,
            PixelFormats.Pbgra32
        );
        bitmap.Render(GraphViewport);

        var directory = System.IO.Path.GetDirectoryName(outputPath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var fileStream = File.Open(outputPath, FileMode.Create, FileAccess.Write, FileShare.None);
        encoder.Save(fileStream);
        return Task.CompletedTask;
    }

    private void OnNodeMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_viewModel is null)
        {
            return;
        }

        var nodeId = (sender as FrameworkElement)?.Tag as string;
        _viewModel.SelectAssociationGraphNode(nodeId);
        e.Handled = true;
    }

    private void OnEdgeMouseEnter(object sender, MouseEventArgs e)
    {
        if (_viewModel is null)
        {
            return;
        }

        var edgeId = (sender as FrameworkElement)?.Tag as string;
        _viewModel.SetAssociationGraphHoveredEdge(edgeId);
    }

    private void OnEdgeMouseLeave(object sender, MouseEventArgs e)
    {
        _viewModel?.SetAssociationGraphHoveredEdge(null);
    }

    private void OnGraphViewportMouseWheel(object sender, MouseWheelEventArgs e)
    {
        var factor = e.Delta > 0 ? ZoomFactor : (1d / ZoomFactor);
        var nextScale = Math.Clamp(GraphScaleTransform.ScaleX * factor, 0.2, 4.5);
        GraphScaleTransform.ScaleX = nextScale;
        GraphScaleTransform.ScaleY = nextScale;
        e.Handled = true;
    }

    private void OnGraphViewportMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        _isPanning = true;
        _panStart = e.GetPosition(GraphViewport);
        _panOriginX = GraphTranslateTransform.X;
        _panOriginY = GraphTranslateTransform.Y;
        GraphViewport.CaptureMouse();
        e.Handled = true;
    }

    private void OnGraphViewportMouseRightButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!_isPanning)
        {
            return;
        }

        _isPanning = false;
        GraphViewport.ReleaseMouseCapture();
        e.Handled = true;
    }

    private void OnGraphViewportMouseMove(object sender, MouseEventArgs e)
    {
        if (!_isPanning)
        {
            return;
        }

        var current = e.GetPosition(GraphViewport);
        var delta = current - _panStart;
        GraphTranslateTransform.X = _panOriginX + delta.X;
        GraphTranslateTransform.Y = _panOriginY + delta.Y;
    }

    private void ResetPanAndZoom()
    {
        GraphScaleTransform.ScaleX = 1;
        GraphScaleTransform.ScaleY = 1;
        GraphTranslateTransform.X = 0;
        GraphTranslateTransform.Y = 0;
    }
}
