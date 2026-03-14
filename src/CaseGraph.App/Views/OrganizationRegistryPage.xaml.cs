using CaseGraph.App.ViewModels;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace CaseGraph.App.Views;

public partial class OrganizationRegistryPage : UserControl
{
    public OrganizationRegistryPage()
    {
        InitializeComponent();
    }

    private async void OnOrganizationsDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: OrganizationRegistryViewModel viewModel })
        {
            return;
        }

        if ((sender as ListBox)?.SelectedItem is null)
        {
            return;
        }

        if (!viewModel.OpenSelectedOrganizationProfileCommand.CanExecute(null))
        {
            return;
        }

        await viewModel.OpenSelectedOrganizationProfileCommand.ExecuteAsync(null);
    }
}
