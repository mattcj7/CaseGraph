using System.Windows;
using System.Windows.Controls;

namespace CaseGraph.App.Views.Dialogs;

public sealed class TextInputDialog : Window
{
    private readonly TextBox _inputTextBox;

    public string? Value => string.IsNullOrWhiteSpace(_inputTextBox.Text)
        ? null
        : _inputTextBox.Text.Trim();

    public TextInputDialog(
        string title,
        string prompt,
        string confirmButtonText,
        string? initialValue = null
    )
    {
        Title = title;
        Width = 520;
        Height = 220;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ResizeMode = ResizeMode.NoResize;

        var root = new Grid
        {
            Margin = new Thickness(16)
        };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var promptText = new TextBlock
        {
            Text = prompt,
            Margin = new Thickness(0, 0, 0, 8),
            TextWrapping = TextWrapping.Wrap
        };
        Grid.SetRow(promptText, 0);
        root.Children.Add(promptText);

        _inputTextBox = new TextBox
        {
            MinWidth = 420,
            Text = initialValue ?? string.Empty
        };
        Grid.SetRow(_inputTextBox, 1);
        root.Children.Add(_inputTextBox);

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 16, 0, 0)
        };

        var cancelButton = new Button
        {
            Content = "Cancel",
            IsCancel = true,
            MinWidth = 90,
            Margin = new Thickness(0, 0, 8, 0)
        };
        buttons.Children.Add(cancelButton);

        var confirmButton = new Button
        {
            Content = confirmButtonText,
            IsDefault = true,
            MinWidth = 120
        };
        confirmButton.Click += (_, _) =>
        {
            if (Value is null)
            {
                MessageBox.Show(
                    this,
                    "A value is required.",
                    "Validation",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning
                );
                return;
            }

            DialogResult = true;
            Close();
        };
        buttons.Children.Add(confirmButton);

        Grid.SetRow(buttons, 2);
        root.Children.Add(buttons);

        Content = root;
    }
}
