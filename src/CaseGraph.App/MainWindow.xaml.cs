using CaseGraph.App.Services;
using CaseGraph.App.ViewModels;
using System;
using System.Windows;
using Wpf.Ui.Controls;

namespace CaseGraph.App;

public partial class MainWindow : FluentWindow
{
    private readonly MainWindowViewModel _viewModel;

    public MainWindow(MainWindowViewModel viewModel)
    {
        _viewModel = viewModel;
        DataContext = _viewModel;
        InitializeComponent();
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        try
        {
            await _viewModel.InitializeAsync();
        }
        catch (Exception ex)
        {
            var correlationId = UiExceptionReporter.LogException(
                "MainWindow initialization failed.",
                ex
            );
            UiExceptionReporter.ShowErrorDialog("CaseGraph Main Window Error", ex, correlationId);
            Application.Current.Shutdown(-1);
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        _viewModel.Dispose();
        base.OnClosed(e);
    }
}
