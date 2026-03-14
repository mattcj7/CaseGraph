using CaseGraph.App.ViewModels;
using System.Windows.Controls;
using System.Windows.Input;

namespace CaseGraph.App.Views.Pages;

public partial class PersonProfileView : UserControl
{
    public PersonProfileView()
    {
        InitializeComponent();
    }

    private async void OnAffiliationsDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (DataContext is not PersonProfileViewModel viewModel)
        {
            return;
        }

        if ((sender as ListBox)?.SelectedItem is not PersonProfileViewModel.PersonAffiliationItem item)
        {
            return;
        }

        if (!viewModel.OpenAffiliationCommand.CanExecute(item))
        {
            return;
        }

        await viewModel.OpenAffiliationCommand.ExecuteAsync(item);
    }
}
