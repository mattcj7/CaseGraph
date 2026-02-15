using CaseGraph.Core.Models;
using System.Windows;
using System.Windows.Controls;

namespace CaseGraph.App.Views.Dialogs;

public sealed class TargetPickerDialog : Window
{
    private readonly IReadOnlyList<TargetSummary> _allTargets;
    private readonly TextBox _searchTextBox;
    private readonly ListBox _targetsListBox;

    public Guid? SelectedTargetId => (_targetsListBox.SelectedItem as TargetSummary)?.TargetId;

    public TargetPickerDialog(string title, string prompt, IReadOnlyList<TargetSummary> targets)
    {
        _allTargets = targets;

        Title = title;
        Width = 520;
        Height = 560;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ResizeMode = ResizeMode.CanResizeWithGrip;
        MinWidth = 480;
        MinHeight = 460;

        var root = new Grid
        {
            Margin = new Thickness(16)
        };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var promptText = new TextBlock
        {
            Text = prompt,
            Margin = new Thickness(0, 0, 0, 8),
            TextWrapping = TextWrapping.Wrap
        };
        Grid.SetRow(promptText, 0);
        root.Children.Add(promptText);

        _searchTextBox = new TextBox
        {
            Margin = new Thickness(0, 0, 0, 8),
            MinWidth = 420
        };
        _searchTextBox.TextChanged += (_, _) => ApplyFilter();
        Grid.SetRow(_searchTextBox, 1);
        root.Children.Add(_searchTextBox);

        _targetsListBox = new ListBox
        {
            DisplayMemberPath = nameof(TargetSummary.DisplayName)
        };
        Grid.SetRow(_targetsListBox, 2);
        root.Children.Add(_targetsListBox);

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 12, 0, 0)
        };

        var cancelButton = new Button
        {
            Content = "Cancel",
            IsCancel = true,
            MinWidth = 90,
            Margin = new Thickness(0, 0, 8, 0)
        };
        buttons.Children.Add(cancelButton);

        var linkButton = new Button
        {
            Content = "Link",
            IsDefault = true,
            MinWidth = 90
        };
        linkButton.Click += (_, _) =>
        {
            if (SelectedTargetId is null)
            {
                MessageBox.Show(
                    this,
                    "Select a target to continue.",
                    "Validation",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning
                );
                return;
            }

            DialogResult = true;
            Close();
        };
        buttons.Children.Add(linkButton);

        Grid.SetRow(buttons, 3);
        root.Children.Add(buttons);

        Content = root;
        ApplyFilter();
    }

    private void ApplyFilter()
    {
        var query = _searchTextBox.Text.Trim();
        var filtered = string.IsNullOrWhiteSpace(query)
            ? _allTargets
            : _allTargets.Where(target =>
                target.DisplayName.Contains(query, StringComparison.OrdinalIgnoreCase)
                || (!string.IsNullOrWhiteSpace(target.PrimaryAlias)
                    && target.PrimaryAlias.Contains(query, StringComparison.OrdinalIgnoreCase)))
            .ToList();

        _targetsListBox.ItemsSource = filtered;
        if (_targetsListBox.Items.Count > 0 && _targetsListBox.SelectedItem is null)
        {
            _targetsListBox.SelectedIndex = 0;
        }
    }
}
