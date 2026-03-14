using CaseGraph.App.ViewModels;
using System.Windows.Controls;
using System.Windows.Input;

namespace CaseGraph.App.Views.Pages;

public partial class PeopleTargetsView : UserControl
{
    public PeopleTargetsView()
    {
        InitializeComponent();
    }

    private async void OnTargetsDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (DataContext is not MainWindowViewModel viewModel)
        {
            return;
        }

        if ((sender as ListBox)?.SelectedItem is null)
        {
            return;
        }

        if (!viewModel.OpenSelectedTargetProfileCommand.CanExecute(null))
        {
            return;
        }

        await viewModel.OpenSelectedTargetProfileCommand.ExecuteAsync(null);
    }
}
