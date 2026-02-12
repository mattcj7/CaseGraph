using CaseGraph.App.ViewModels;
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
        await _viewModel.InitializeAsync();
    }
}
