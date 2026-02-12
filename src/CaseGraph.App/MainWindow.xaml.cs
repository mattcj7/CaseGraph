using CaseGraph.App.ViewModels;
using Wpf.Ui.Controls;

namespace CaseGraph.App;

public partial class MainWindow : FluentWindow
{
    public MainWindow(MainWindowViewModel viewModel)
    {
        DataContext = viewModel;
        InitializeComponent();
    }
}
