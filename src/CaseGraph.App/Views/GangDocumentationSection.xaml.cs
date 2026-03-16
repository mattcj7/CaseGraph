using CaseGraph.App.ViewModels;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;

namespace CaseGraph.App.Views;

public partial class GangDocumentationSection : UserControl
{
    private GangDocumentationViewModel? _viewModel;

    public GangDocumentationSection()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        Unloaded += OnUnloaded;
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        DetachViewModel(e.OldValue as GangDocumentationViewModel);
        AttachViewModel(e.NewValue as GangDocumentationViewModel);
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        DetachViewModel(_viewModel);
    }

    private void AttachViewModel(GangDocumentationViewModel? viewModel)
    {
        if (viewModel is null)
        {
            return;
        }

        _viewModel = viewModel;
        _viewModel.FocusRequested += OnFocusRequested;
        _viewModel.PropertyChanged += OnViewModelPropertyChanged;
    }

    private void DetachViewModel(GangDocumentationViewModel? viewModel)
    {
        if (viewModel is null)
        {
            return;
        }

        viewModel.FocusRequested -= OnFocusRequested;
        viewModel.PropertyChanged -= OnViewModelPropertyChanged;
        if (ReferenceEquals(_viewModel, viewModel))
        {
            _viewModel = null;
        }
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(GangDocumentationViewModel.SelectedOrganizationId)
            or nameof(GangDocumentationViewModel.SelectedSubgroupOrganizationId)
            or nameof(GangDocumentationViewModel.SelectedRecord))
        {
            KeyboardFocusOrganizationSelection();
        }
    }

    private void OnFocusRequested(string focusTarget)
    {
        Dispatcher.BeginInvoke(() =>
        {
            switch (focusTarget)
            {
                case "Organization":
                    OrganizationComboBox.Focus();
                    break;
                case "AffiliationRole":
                    AffiliationRoleComboBox.Focus();
                    break;
                case "DocumentationStatus":
                    DocumentationStatusComboBox.Focus();
                    break;
                case "ApprovalStatus":
                    ApprovalStatusComboBox.Focus();
                    break;
                case "Summary":
                    SummaryTextBox.Focus();
                    SummaryTextBox.SelectAll();
                    break;
                case "CriterionType":
                    CriterionTypeComboBox.Focus();
                    break;
                case "CriterionBasisSummary":
                    CriterionBasisSummaryTextBox.Focus();
                    CriterionBasisSummaryTextBox.SelectAll();
                    break;
            }
        });
    }

    private void KeyboardFocusOrganizationSelection()
    {
        if (_viewModel is null)
        {
            return;
        }

        if (_viewModel.HasSelectedDocumentation && _viewModel.SelectedSubgroupOrganizationId.HasValue)
        {
            SubgroupComboBox.GetBindingExpression(Selector.SelectedValueProperty)?.UpdateTarget();
            return;
        }

        OrganizationComboBox.GetBindingExpression(Selector.SelectedValueProperty)?.UpdateTarget();
    }
}
