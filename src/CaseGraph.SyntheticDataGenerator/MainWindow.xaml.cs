using CaseGraph.SyntheticDataGenerator.ViewModels;
using System.Windows;

namespace CaseGraph.SyntheticDataGenerator;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        DataContext = new MainWindowViewModel();
    }
}
