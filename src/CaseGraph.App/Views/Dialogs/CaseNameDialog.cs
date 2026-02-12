using System.Windows;
using System.Windows.Controls;

namespace CaseGraph.App.Views.Dialogs;

public sealed class CaseNameDialog : Window
{
    private readonly TextBox _nameTextBox;

    public string? CaseName => string.IsNullOrWhiteSpace(_nameTextBox.Text)
        ? null
        : _nameTextBox.Text.Trim();

    public CaseNameDialog()
    {
        Title = "New Case";
        Width = 420;
        Height = 190;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ResizeMode = ResizeMode.NoResize;

        var root = new Grid
        {
            Margin = new Thickness(16)
        };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var prompt = new TextBlock
        {
            Text = "Enter case name:",
            Margin = new Thickness(0, 0, 0, 8)
        };
        Grid.SetRow(prompt, 0);
        root.Children.Add(prompt);

        _nameTextBox = new TextBox
        {
            MinWidth = 340
        };
        Grid.SetRow(_nameTextBox, 1);
        root.Children.Add(_nameTextBox);

        var buttonPanel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 16, 0, 0)
        };

        var cancelButton = new Button
        {
            Content = "Cancel",
            IsCancel = true,
            MinWidth = 84,
            Margin = new Thickness(0, 0, 8, 0)
        };
        buttonPanel.Children.Add(cancelButton);

        var okButton = new Button
        {
            Content = "Create",
            IsDefault = true,
            MinWidth = 84
        };
        okButton.Click += (_, _) =>
        {
            if (CaseName is null)
            {
                MessageBox.Show(
                    this,
                    "Case name is required.",
                    "Validation",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning
                );
                return;
            }

            DialogResult = true;
            Close();
        };
        buttonPanel.Children.Add(okButton);

        Grid.SetRow(buttonPanel, 2);
        root.Children.Add(buttonPanel);

        Content = root;
    }
}
