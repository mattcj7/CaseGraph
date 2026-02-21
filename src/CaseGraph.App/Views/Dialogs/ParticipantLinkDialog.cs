using CaseGraph.Core.Models;
using System.Windows;
using System.Windows.Controls;

namespace CaseGraph.App.Views.Dialogs;

public enum ParticipantLinkMode
{
    LinkToExistingTarget = 0,
    CreateTarget = 1
}

public sealed record ParticipantLinkSelection(
    ParticipantLinkMode Mode,
    TargetIdentifierType IdentifierType,
    Guid? TargetId,
    string? NewTargetDisplayName
);

public sealed class ParticipantLinkDialog : Window
{
    private readonly IReadOnlyList<TargetSummary> _allTargets;
    private readonly TargetIdentifierType? _inferredType;
    private readonly RadioButton _linkExistingTargetRadio;
    private readonly RadioButton _createTargetRadio;
    private readonly TextBox _targetSearchTextBox;
    private readonly ListBox _targetsListBox;
    private readonly TextBox _newTargetNameTextBox;
    private readonly ComboBox _identifierTypeComboBox;
    private readonly TextBlock _identifierTypeHintText;

    public ParticipantLinkSelection? Selection { get; private set; }

    public ParticipantLinkDialog(
        string title,
        string participantRaw,
        string participantNormalized,
        IReadOnlyList<TargetSummary> targets,
        TargetIdentifierType? inferredType,
        ParticipantLinkMode initialMode
    )
    {
        Title = title;
        Width = 620;
        Height = 640;
        MinWidth = 560;
        MinHeight = 560;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ResizeMode = ResizeMode.CanResizeWithGrip;

        _allTargets = targets;
        _inferredType = inferredType;

        var root = new Grid
        {
            Margin = new Thickness(16)
        };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var prompt = new TextBlock
        {
            Text = "Review participant value and select link action.",
            Margin = new Thickness(0, 0, 0, 8),
            TextWrapping = TextWrapping.Wrap
        };
        Grid.SetRow(prompt, 0);
        root.Children.Add(prompt);

        var valuesPanel = new StackPanel
        {
            Margin = new Thickness(0, 0, 0, 8)
        };
        valuesPanel.Children.Add(new TextBlock
        {
            Text = $"Raw: {participantRaw}",
            TextWrapping = TextWrapping.Wrap
        });
        valuesPanel.Children.Add(new TextBlock
        {
            Text = $"Normalized: {participantNormalized}",
            Margin = new Thickness(0, 4, 0, 0),
            TextWrapping = TextWrapping.Wrap
        });
        Grid.SetRow(valuesPanel, 1);
        root.Children.Add(valuesPanel);

        var identifierTypePanel = new StackPanel
        {
            Margin = new Thickness(0, 0, 0, 8)
        };
        identifierTypePanel.Children.Add(new TextBlock
        {
            Text = "Identifier Type",
            FontWeight = FontWeights.SemiBold
        });

        _identifierTypeComboBox = new ComboBox
        {
            Margin = new Thickness(0, 6, 0, 0),
            ItemsSource = new[]
            {
                TargetIdentifierType.Phone,
                TargetIdentifierType.Email,
                TargetIdentifierType.SocialHandle,
                TargetIdentifierType.Username,
                TargetIdentifierType.Other
            }
        };
        _identifierTypeHintText = new TextBlock
        {
            Margin = new Thickness(0, 6, 0, 0),
            TextWrapping = TextWrapping.Wrap
        };
        identifierTypePanel.Children.Add(_identifierTypeComboBox);
        identifierTypePanel.Children.Add(_identifierTypeHintText);
        Grid.SetRow(identifierTypePanel, 2);
        root.Children.Add(identifierTypePanel);

        var modePanel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(0, 0, 0, 8)
        };
        _linkExistingTargetRadio = new RadioButton
        {
            Content = "Link to existing target",
            Margin = new Thickness(0, 0, 16, 0),
            IsChecked = initialMode == ParticipantLinkMode.LinkToExistingTarget
        };
        _linkExistingTargetRadio.Checked += (_, _) => UpdateModeState();
        modePanel.Children.Add(_linkExistingTargetRadio);

        _createTargetRadio = new RadioButton
        {
            Content = "Create new target",
            IsChecked = initialMode == ParticipantLinkMode.CreateTarget
        };
        _createTargetRadio.Checked += (_, _) => UpdateModeState();
        modePanel.Children.Add(_createTargetRadio);
        Grid.SetRow(modePanel, 3);
        root.Children.Add(modePanel);

        var targetPickerPanel = new Grid
        {
            Margin = new Thickness(0, 0, 0, 8)
        };
        targetPickerPanel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        targetPickerPanel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        targetPickerPanel.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        targetPickerPanel.Children.Add(new TextBlock
        {
            Text = "Existing targets",
            FontWeight = FontWeights.SemiBold
        });

        _targetSearchTextBox = new TextBox
        {
            Margin = new Thickness(0, 6, 0, 8),
            ToolTip = "Filter targets by name or alias"
        };
        _targetSearchTextBox.TextChanged += (_, _) => ApplyTargetFilter();
        Grid.SetRow(_targetSearchTextBox, 1);
        targetPickerPanel.Children.Add(_targetSearchTextBox);

        _targetsListBox = new ListBox
        {
            DisplayMemberPath = nameof(TargetSummary.DisplayName)
        };
        Grid.SetRow(_targetsListBox, 2);
        targetPickerPanel.Children.Add(_targetsListBox);
        Grid.SetRow(targetPickerPanel, 4);
        root.Children.Add(targetPickerPanel);

        var createPanel = new StackPanel
        {
            Margin = new Thickness(0, 0, 0, 8)
        };
        createPanel.Children.Add(new TextBlock
        {
            Text = "New target display name",
            FontWeight = FontWeights.SemiBold
        });
        _newTargetNameTextBox = new TextBox
        {
            Margin = new Thickness(0, 6, 0, 0),
            Text = participantRaw
        };
        createPanel.Children.Add(_newTargetNameTextBox);
        Grid.SetRow(createPanel, 5);
        root.Children.Add(createPanel);

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 12, 0, 0)
        };
        buttons.Children.Add(new Button
        {
            Content = "Cancel",
            IsCancel = true,
            MinWidth = 90,
            Margin = new Thickness(0, 0, 8, 0)
        });

        var confirmButton = new Button
        {
            Content = "Continue",
            IsDefault = true,
            MinWidth = 110
        };
        confirmButton.Click += (_, _) => TryComplete();
        buttons.Children.Add(confirmButton);
        Grid.SetRow(buttons, 6);
        root.Children.Add(buttons);

        Content = root;
        ApplyIdentifierTypeState();
        ApplyTargetFilter();
        UpdateModeState();
    }

    private void ApplyIdentifierTypeState()
    {
        if (_inferredType.HasValue)
        {
            _identifierTypeComboBox.SelectedItem = _inferredType.Value;
            _identifierTypeComboBox.IsEnabled = false;
            _identifierTypeHintText.Text = $"Inferred as {_inferredType.Value}.";
            return;
        }

        _identifierTypeComboBox.IsEnabled = true;
        _identifierTypeHintText.Text =
            "Type could not be inferred. Choose Phone, Email, Handle, Username, or Other.";
    }

    private void ApplyTargetFilter()
    {
        var query = _targetSearchTextBox.Text.Trim();
        var filtered = string.IsNullOrWhiteSpace(query)
            ? _allTargets
            : _allTargets.Where(target =>
                target.DisplayName.Contains(query, StringComparison.OrdinalIgnoreCase)
                || (!string.IsNullOrWhiteSpace(target.PrimaryAlias)
                    && target.PrimaryAlias.Contains(query, StringComparison.OrdinalIgnoreCase)))
            .ToList();

        _targetsListBox.ItemsSource = filtered;
        if (_targetsListBox.SelectedItem is null && _targetsListBox.Items.Count > 0)
        {
            _targetsListBox.SelectedIndex = 0;
        }
    }

    private void UpdateModeState()
    {
        var linkingToExisting = _linkExistingTargetRadio.IsChecked == true;
        _targetSearchTextBox.IsEnabled = linkingToExisting;
        _targetsListBox.IsEnabled = linkingToExisting;
        _newTargetNameTextBox.IsEnabled = !linkingToExisting;
    }

    private void TryComplete()
    {
        TargetIdentifierType? selectedType = _inferredType;
        if (!selectedType.HasValue && _identifierTypeComboBox.SelectedItem is TargetIdentifierType explicitType)
        {
            selectedType = explicitType;
        }

        if (!selectedType.HasValue)
        {
            MessageBox.Show(
                this,
                "Select an identifier type to continue.",
                "Validation",
                MessageBoxButton.OK,
                MessageBoxImage.Warning
            );
            return;
        }

        var mode = _createTargetRadio.IsChecked == true
            ? ParticipantLinkMode.CreateTarget
            : ParticipantLinkMode.LinkToExistingTarget;

        if (mode == ParticipantLinkMode.LinkToExistingTarget)
        {
            if (_targetsListBox.SelectedItem is not TargetSummary target)
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

            Selection = new ParticipantLinkSelection(
                mode,
                selectedType.Value,
                target.TargetId,
                null
            );
            DialogResult = true;
            Close();
            return;
        }

        var newTargetName = _newTargetNameTextBox.Text.Trim();
        if (newTargetName.Length == 0)
        {
            MessageBox.Show(
                this,
                "Target name is required.",
                "Validation",
                MessageBoxButton.OK,
                MessageBoxImage.Warning
            );
            return;
        }

        Selection = new ParticipantLinkSelection(
            mode,
            selectedType.Value,
            null,
            newTargetName
        );
        DialogResult = true;
        Close();
    }
}
