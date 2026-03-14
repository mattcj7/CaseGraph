using CaseGraph.App.ViewModels;
using CaseGraph.Infrastructure.Organizations;
using System.Windows.Controls;
using System.Windows.Input;

namespace CaseGraph.App.Views.Pages;

public partial class OrganizationProfileView : UserControl
{
    public OrganizationProfileView()
    {
        InitializeComponent();
    }

    private async void OnMembershipsDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (DataContext is not OrganizationProfileViewModel viewModel)
        {
            return;
        }

        if ((sender as DataGrid)?.SelectedItem is not OrganizationProfileViewModel.OrganizationMembershipProfileItem item)
        {
            return;
        }

        if (!viewModel.OpenMembershipPersonCommand.CanExecute(item))
        {
            return;
        }

        await viewModel.OpenMembershipPersonCommand.ExecuteAsync(item);
    }

    private async void OnChildrenDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (DataContext is not OrganizationProfileViewModel viewModel)
        {
            return;
        }

        if ((sender as ListBox)?.SelectedItem is not OrganizationSummaryDto item)
        {
            return;
        }

        if (!viewModel.OpenChildOrganizationCommand.CanExecute(item))
        {
            return;
        }

        await viewModel.OpenChildOrganizationCommand.ExecuteAsync(item);
    }
}
