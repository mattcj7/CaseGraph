using CaseGraph.Core.Diagnostics;
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
            AppFileLogger.LogException("MainWindow initialization failed.", ex);
            System.Windows.MessageBox.Show(
                ex.ToString(),
                "CaseGraph Main Window Error",
                System.Windows.MessageBoxButton.OK,
                System.Windows.MessageBoxImage.Error
            );
            Application.Current.Shutdown(-1);
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        _viewModel.Dispose();
        base.OnClosed(e);
    }
}
