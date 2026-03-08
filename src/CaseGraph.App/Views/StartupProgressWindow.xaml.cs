using CaseGraph.App.ViewModels;
using System.Windows;
using System.Windows.Threading;

namespace CaseGraph.App.Views;

public partial class StartupProgressWindow : Window
{
    private readonly StartupProgressViewModel _viewModel;
    private readonly DispatcherTimer _elapsedTimer;

    public StartupProgressWindow(StartupProgressViewModel viewModel)
    {
        _viewModel = viewModel;
        InitializeComponent();
        DataContext = _viewModel;

        _elapsedTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(250)
        };
        _elapsedTimer.Tick += OnElapsedTimerTick;
    }

    protected override void OnContentRendered(EventArgs e)
    {
        base.OnContentRendered(e);
        _elapsedTimer.Start();
    }

    protected override void OnClosed(EventArgs e)
    {
        _elapsedTimer.Stop();
        _elapsedTimer.Tick -= OnElapsedTimerTick;
        _viewModel.Dispose();
        base.OnClosed(e);
    }

    private void OnElapsedTimerTick(object? sender, EventArgs e)
    {
        _viewModel.RefreshElapsed();
    }
}
