using CaseGraph.App.Services;
using CaseGraph.App.ViewModels;
using System;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using Wpf.Ui.Controls;

namespace CaseGraph.App;

public partial class MainWindow : FluentWindow
{
    public const double WindowControlButtonWidth = 40d;
    public static Thickness WindowControlButtonSpacing { get; } = new(8d, 0d, 0d, 0d);
    public static double WindowControlStripWidth { get; } = CalculateWindowControlStripWidth(
        WindowControlButtonWidth,
        buttonCount: 3,
        horizontalSpacing: WindowControlButtonSpacing.Left
    );

    private readonly MainWindowViewModel _viewModel;
    private UIElement? _activeTitleBarElement;
    private Point _titleBarMouseDownPoint;
    private double _restoreHorizontalRatio = 0.5d;
    private double _restorePointerYOffset;
    private bool _isTitleBarPressed;

    public MainWindow(MainWindowViewModel viewModel)
    {
        _viewModel = viewModel;
        DataContext = _viewModel;
        InitializeComponent();
        UpdateMaximizeRestoreButton();
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        try
        {
            await _viewModel.InitializeAsync();
        }
        catch (Exception ex)
        {
            if (Application.Current is App app)
            {
                await app.HandleFatalExceptionAsync(
                    "CaseGraph Main Window Error",
                    "Main window initialization failed.",
                    ex
                );
            }
            else
            {
                var report = UiExceptionReporter.LogFatalException(
                    "MainWindow initialization failed.",
                    ex
                );
                UiExceptionReporter.ShowCrashDialog(
                    "CaseGraph Main Window Error",
                    "Main window initialization failed.",
                    report
                );
                Application.Current.Shutdown(-1);
            }
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        _viewModel.Dispose();
        base.OnClosed(e);
    }

    protected override void OnStateChanged(EventArgs e)
    {
        base.OnStateChanged(e);
        UpdateMaximizeRestoreButton();
    }

    private void OnMinimizeButtonClick(object sender, RoutedEventArgs e)
    {
        SystemCommands.MinimizeWindow(this);
    }

    private void OnMaximizeRestoreButtonClick(object sender, RoutedEventArgs e)
    {
        ToggleMaximizeRestore();
    }

    private void OnCloseButtonClick(object sender, RoutedEventArgs e)
    {
        SystemCommands.CloseWindow(this);
    }

    private void OnTitleBarMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left)
        {
            return;
        }

        if (e.ClickCount == 2)
        {
            ClearTitleBarDragState();
            if (CanToggleMaximizeRestore(ResizeMode))
            {
                ToggleMaximizeRestore();
                e.Handled = true;
            }

            return;
        }

        _isTitleBarPressed = true;
        _titleBarMouseDownPoint = e.GetPosition(this);
        _restoreHorizontalRatio = GetRestoreHorizontalRatio(_titleBarMouseDownPoint.X, ActualWidth);
        _restorePointerYOffset = _titleBarMouseDownPoint.Y;
        _activeTitleBarElement = sender as UIElement;
        _activeTitleBarElement?.CaptureMouse();
        e.Handled = true;
    }

    private void OnTitleBarMouseMove(object sender, MouseEventArgs e)
    {
        if (!_isTitleBarPressed)
        {
            return;
        }

        if (e.LeftButton != MouseButtonState.Pressed)
        {
            ClearTitleBarDragState();
            return;
        }

        var currentPoint = e.GetPosition(this);
        if (!HasExceededDragThreshold(_titleBarMouseDownPoint, currentPoint))
        {
            return;
        }

        ClearTitleBarDragState();
        if (WindowState == WindowState.Maximized)
        {
            BeginDragFromMaximized(currentPoint);
        }
        else
        {
            BeginWindowDrag();
        }

        e.Handled = true;
    }

    private void OnTitleBarMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        ClearTitleBarDragState();
    }

    private void OnTitleBarLostMouseCapture(object sender, MouseEventArgs e)
    {
        ClearTitleBarDragState();
    }

    private void ToggleMaximizeRestore()
    {
        if (!CanToggleMaximizeRestore(ResizeMode))
        {
            return;
        }

        WindowState = GetToggledWindowState(WindowState);
    }

    private void UpdateMaximizeRestoreButton()
    {
        MaximizeRestoreGlyph.Text = GetMaximizeRestoreGlyph(WindowState);
        MaximizeRestoreButton.ToolTip = GetMaximizeRestoreToolTip(WindowState);
    }

    private void BeginDragFromMaximized(Point pointerPosition)
    {
        var restoreBounds = RestoreBounds;
        var restoreWidth = restoreBounds.Width > 0 ? restoreBounds.Width : ActualWidth;
        var screenPoint = PointToScreen(pointerPosition);
        var dpi = VisualTreeHelper.GetDpi(this);
        var screenX = screenPoint.X / dpi.DpiScaleX;
        var screenY = screenPoint.Y / dpi.DpiScaleY;
        var titleBarHeight = ShellTitleBar.ActualHeight > 0
            ? ShellTitleBar.ActualHeight
            : _restorePointerYOffset;

        WindowState = WindowState.Normal;
        Left = CalculateRestoredLeft(screenX, restoreWidth, _restoreHorizontalRatio);
        Top = screenY - Math.Min(_restorePointerYOffset, titleBarHeight);
        BeginWindowDrag();
    }

    private void BeginWindowDrag()
    {
        try
        {
            DragMove();
        }
        catch (InvalidOperationException)
        {
        }
    }

    private void ClearTitleBarDragState()
    {
        _isTitleBarPressed = false;
        if (_activeTitleBarElement?.IsMouseCaptured == true)
        {
            _activeTitleBarElement.ReleaseMouseCapture();
        }

        _activeTitleBarElement = null;
    }

    private static bool CanToggleMaximizeRestore(ResizeMode resizeMode)
    {
        return resizeMode is ResizeMode.CanResize or ResizeMode.CanResizeWithGrip;
    }

    private static WindowState GetToggledWindowState(WindowState windowState)
    {
        return windowState == WindowState.Maximized
            ? WindowState.Normal
            : WindowState.Maximized;
    }

    private static bool HasExceededDragThreshold(Point origin, Point current)
    {
        return Math.Abs(current.X - origin.X) >= SystemParameters.MinimumHorizontalDragDistance
            || Math.Abs(current.Y - origin.Y) >= SystemParameters.MinimumVerticalDragDistance;
    }

    private static double GetRestoreHorizontalRatio(double pointerX, double windowWidth)
    {
        if (windowWidth <= 0)
        {
            return 0.5d;
        }

        return Math.Clamp(pointerX / windowWidth, 0d, 1d);
    }

    private static double CalculateRestoredLeft(
        double screenX,
        double restoreWidth,
        double horizontalRatio
    )
    {
        return screenX - (restoreWidth * Math.Clamp(horizontalRatio, 0d, 1d));
    }

    private static string GetMaximizeRestoreGlyph(WindowState windowState)
    {
        return windowState == WindowState.Maximized ? "❐" : "□";
    }

    private static string GetMaximizeRestoreToolTip(WindowState windowState)
    {
        return windowState == WindowState.Maximized ? "Restore" : "Maximize";
    }

    private static double CalculateWindowControlStripWidth(
        double buttonWidth,
        int buttonCount,
        double horizontalSpacing
    )
    {
        if (buttonCount <= 0)
        {
            return 0d;
        }

        return (buttonWidth * buttonCount)
            + (Math.Max(0, buttonCount - 1) * Math.Max(0d, horizontalSpacing));
    }
}
