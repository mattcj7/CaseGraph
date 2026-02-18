using System.Windows;
using System.Windows.Controls;

namespace CaseGraph.App.Views.Dialogs;

public sealed class CrashDialog : Window
{
    public CrashDialog(
        string title,
        string whatHappened,
        string logPath,
        string diagnosticsText,
        Action copyDiagnostics
    )
    {
        Title = title;
        Width = 760;
        Height = 520;
        MinWidth = 640;
        MinHeight = 420;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ResizeMode = ResizeMode.CanResizeWithGrip;

        var root = new Grid
        {
            Margin = new Thickness(16)
        };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var summaryHeader = new TextBlock
        {
            Text = "What happened",
            FontWeight = FontWeights.SemiBold
        };
        Grid.SetRow(summaryHeader, 0);
        root.Children.Add(summaryHeader);

        var summaryText = new TextBlock
        {
            Text = whatHappened,
            Margin = new Thickness(0, 6, 0, 0),
            TextWrapping = TextWrapping.Wrap
        };
        Grid.SetRow(summaryText, 1);
        root.Children.Add(summaryText);

        var logPathText = new TextBlock
        {
            Text = $"Log path: {logPath}",
            Margin = new Thickness(0, 12, 0, 0),
            TextWrapping = TextWrapping.Wrap
        };
        Grid.SetRow(logPathText, 2);
        root.Children.Add(logPathText);

        var detailsBox = new TextBox
        {
            Margin = new Thickness(0, 12, 0, 0),
            Text = diagnosticsText,
            IsReadOnly = true,
            TextWrapping = TextWrapping.Wrap,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            AcceptsReturn = true
        };
        Grid.SetRow(detailsBox, 3);
        root.Children.Add(detailsBox);

        var buttonPanel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 12, 0, 0)
        };

        var copyButton = new Button
        {
            Content = "Copy diagnostics",
            MinWidth = 140,
            Margin = new Thickness(0, 0, 8, 0)
        };
        copyButton.Click += (_, _) =>
        {
            copyDiagnostics();
        };
        buttonPanel.Children.Add(copyButton);

        var closeButton = new Button
        {
            Content = "Close",
            MinWidth = 100,
            IsDefault = true,
            IsCancel = true
        };
        closeButton.Click += (_, _) => Close();
        buttonPanel.Children.Add(closeButton);

        Grid.SetRow(buttonPanel, 4);
        root.Children.Add(buttonPanel);

        Content = root;
    }
}
