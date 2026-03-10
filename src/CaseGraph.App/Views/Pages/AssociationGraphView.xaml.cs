using CaseGraph.App.ViewModels;
using CaseGraph.Core.Diagnostics;
using CaseGraph.Core.Models;
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
    private const double GraphMargin = 36;
    private const double ZoomFactor = 1.12;

    private MainWindowViewModel? _viewModel;
    private bool _isPanning;
    private Point _panStart;
    private double _panOriginX;
    private double _panOriginY;
    private AssociationGraphRenderPipeline.AssociationGraphRenderPlan? _lastRenderPlan;

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

        var renderPlan = AssociationGraphRenderPipeline.BuildRenderPlan(graphResult);
        _lastRenderPlan = renderPlan;
        if (renderPlan.IsEmpty || renderPlan.Nodes.Count == 0)
        {
            RenderPlaceholder(renderPlan.Message);
            ResetPanAndZoom();
            return;
        }

        var minX = double.PositiveInfinity;
        var maxX = double.NegativeInfinity;
        var minY = double.PositiveInfinity;
        var maxY = double.NegativeInfinity;

        foreach (var placement in renderPlan.Placements.Values)
        {
            minX = Math.Min(minX, placement.CenterX - (placement.Width / 2d));
            maxX = Math.Max(maxX, placement.CenterX + (placement.Width / 2d));
            minY = Math.Min(minY, placement.CenterY - (placement.Height / 2d));
            maxY = Math.Max(maxY, placement.CenterY + (placement.Height / 2d));
        }

        if (double.IsInfinity(minX) || double.IsInfinity(maxX) || double.IsInfinity(minY) || double.IsInfinity(maxY))
        {
            _lastRenderPlan = renderPlan with
            {
                IsEmpty = true,
                Message = "Association graph could not be positioned safely."
            };
            RenderPlaceholder("Association graph could not be positioned safely.");
            ResetPanAndZoom();
            return;
        }

        var contentWidth = Math.Max((maxX - minX) + (GraphMargin * 2d), 1);
        var contentHeight = Math.Max((maxY - minY) + (GraphMargin * 2d), 1);
        GraphCanvas.Width = contentWidth;
        GraphCanvas.Height = contentHeight;

        var toCanvasX = new Func<double, double>(x => (x - minX) + GraphMargin);
        var toCanvasY = new Func<double, double>(y => (y - minY) + GraphMargin);

        foreach (var edge in renderPlan.Edges)
        {
            if (!renderPlan.Placements.TryGetValue(edge.SourceNodeId, out var sourcePlacement)
                || !renderPlan.Placements.TryGetValue(edge.TargetNodeId, out var targetPlacement))
            {
                continue;
            }

            var x1 = toCanvasX(sourcePlacement.CenterX);
            var y1 = toCanvasY(sourcePlacement.CenterY);
            var x2 = toCanvasX(targetPlacement.CenterX);
            var y2 = toCanvasY(targetPlacement.CenterY);

            var line = new Line
            {
                X1 = x1,
                Y1 = y1,
                X2 = x2,
                Y2 = y2,
                Stroke = Brushes.DarkSlateGray,
                StrokeThickness = 2,
                Tag = edge.EdgeId,
                SnapsToDevicePixels = true
            };
            line.MouseEnter += OnEdgeMouseEnter;
            line.MouseLeave += OnEdgeMouseLeave;
            GraphCanvas.Children.Add(line);

            var label = new TextBlock
            {
                Text = edge.Weight.ToString(),
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
        foreach (var node in renderPlan.Nodes)
        {
            if (!renderPlan.Placements.TryGetValue(node.NodeId, out var placement))
            {
                continue;
            }

            var centerX = toCanvasX(placement.CenterX);
            var centerY = toCanvasY(placement.CenterY);

            var highlighted = nodeSearchQuery.Length > 0
                && node.Label.Contains(nodeSearchQuery, StringComparison.OrdinalIgnoreCase);
            var background = new SolidColorBrush(ToMediaColor(ResolveFillColor(node, highlighted)));
            var border = new Border
            {
                Width = placement.Width,
                Height = placement.Height,
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

            Canvas.SetLeft(border, centerX - (placement.Width / 2d));
            Canvas.SetTop(border, centerY - (placement.Height / 2d));
            GraphCanvas.Children.Add(border);
        }

        ResetPanAndZoom();
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

    private void RenderPlaceholder(string message)
    {
        var text = new TextBlock
        {
            Text = string.IsNullOrWhiteSpace(message)
                ? "No association graph content is available."
                : message,
            Foreground = Brushes.DimGray,
            TextWrapping = TextWrapping.Wrap,
            TextAlignment = TextAlignment.Center,
            Width = Math.Max(GraphViewport.ActualWidth - 48, 240)
        };

        var border = new Border
        {
            Background = Brushes.Transparent,
            Child = text
        };

        Canvas.SetLeft(border, 24);
        Canvas.SetTop(border, 24);
        GraphCanvas.Children.Add(border);
    }

    private Task ExportSnapshotAsync(string outputPath)
    {
        var renderPlan = _lastRenderPlan;
        var exportInfo = AssociationGraphRenderPipeline.BuildSnapshotExportInfo(
            renderPlan,
            GraphViewport.ActualWidth,
            GraphViewport.ActualHeight,
            Math.Max(GraphCanvas.ActualWidth, GraphCanvas.Width),
            Math.Max(GraphCanvas.ActualHeight, GraphCanvas.Height)
        );
        var originalScaleX = GraphScaleTransform.ScaleX;
        var originalScaleY = GraphScaleTransform.ScaleY;
        var originalTranslateX = GraphTranslateTransform.X;
        var originalTranslateY = GraphTranslateTransform.Y;
        var originalCanvasWidth = Math.Max(GraphCanvas.Width, GraphCanvas.ActualWidth);
        var originalCanvasHeight = Math.Max(GraphCanvas.Height, GraphCanvas.ActualHeight);

        AppFileLogger.LogEvent(
            eventName: "AssociationGraphSnapshotExportPrepared",
            level: "INFO",
            message: "Association graph snapshot export prepared.",
            fields: new Dictionary<string, object?>
            {
                ["width"] = exportInfo.Width,
                ["height"] = exportInfo.Height,
                ["nodeCount"] = exportInfo.NodeCount,
                ["edgeCount"] = exportInfo.EdgeCount,
                ["layoutAlgorithm"] = exportInfo.LayoutAlgorithm,
                ["fallbackUsed"] = exportInfo.FallbackUsed,
                ["isEmpty"] = exportInfo.IsEmpty,
                ["preferredSurface"] = exportInfo.PreferredSurface,
                ["targetVisualName"] = nameof(GraphCanvas),
                ["targetVisualType"] = GraphCanvas.GetType().FullName,
                ["viewScaleX"] = originalScaleX,
                ["viewScaleY"] = originalScaleY,
                ["viewTranslateX"] = originalTranslateX,
                ["viewTranslateY"] = originalTranslateY
            }
        );

        RenderTargetBitmap bitmap;
        try
        {
            GraphScaleTransform.ScaleX = 1d;
            GraphScaleTransform.ScaleY = 1d;
            GraphTranslateTransform.X = 0d;
            GraphTranslateTransform.Y = 0d;
            EnsureExportSurfaceReady(exportInfo.Width, exportInfo.Height);

            bitmap = new RenderTargetBitmap(
                exportInfo.Width,
                exportInfo.Height,
                96,
                96,
                PixelFormats.Pbgra32
            );
            bitmap.Render(GraphCanvas);
        }
        catch (Exception ex)
        {
            AppFileLogger.LogEvent(
                eventName: "AssociationGraphSnapshotExportFailed",
                level: "ERROR",
                message: "Association graph snapshot export failed before PNG save.",
                ex: ex,
                fields: new Dictionary<string, object?>
                {
                    ["width"] = exportInfo.Width,
                    ["height"] = exportInfo.Height,
                    ["nodeCount"] = exportInfo.NodeCount,
                    ["edgeCount"] = exportInfo.EdgeCount,
                    ["layoutAlgorithm"] = exportInfo.LayoutAlgorithm,
                    ["fallbackUsed"] = exportInfo.FallbackUsed,
                    ["preferredSurface"] = exportInfo.PreferredSurface,
                    ["targetVisualName"] = nameof(GraphCanvas),
                    ["targetVisualType"] = GraphCanvas.GetType().FullName
                }
            );
            throw;
        }
        finally
        {
            GraphScaleTransform.ScaleX = originalScaleX;
            GraphScaleTransform.ScaleY = originalScaleY;
            GraphTranslateTransform.X = originalTranslateX;
            GraphTranslateTransform.Y = originalTranslateY;
            EnsureExportSurfaceReady(
                Math.Max((int)Math.Ceiling(originalCanvasWidth), 1),
                Math.Max((int)Math.Ceiling(originalCanvasHeight), 1)
            );
        }

        var directory = System.IO.Path.GetDirectoryName(outputPath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var fileStream = File.Open(outputPath, FileMode.Create, FileAccess.Write, FileShare.None);
        encoder.Save(fileStream);
        AppFileLogger.LogEvent(
            eventName: "AssociationGraphSnapshotExportCompleted",
            level: "INFO",
            message: "Association graph snapshot export completed.",
            fields: new Dictionary<string, object?>
            {
                ["path"] = outputPath,
                ["width"] = exportInfo.Width,
                ["height"] = exportInfo.Height,
                ["nodeCount"] = exportInfo.NodeCount,
                ["edgeCount"] = exportInfo.EdgeCount,
                ["layoutAlgorithm"] = exportInfo.LayoutAlgorithm,
                ["fallbackUsed"] = exportInfo.FallbackUsed,
                ["preferredSurface"] = exportInfo.PreferredSurface,
                ["targetVisualName"] = nameof(GraphCanvas),
                ["targetVisualType"] = GraphCanvas.GetType().FullName
            }
        );
        return Task.CompletedTask;
    }

    private void EnsureExportSurfaceReady(int width, int height)
    {
        var surfaceSize = new Size(Math.Max(width, 1), Math.Max(height, 1));
        GraphCanvas.Width = surfaceSize.Width;
        GraphCanvas.Height = surfaceSize.Height;
        GraphCanvas.Measure(surfaceSize);
        GraphCanvas.Arrange(new Rect(new Point(0, 0), surfaceSize));
        GraphCanvas.UpdateLayout();
        GraphViewport.UpdateLayout();
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
