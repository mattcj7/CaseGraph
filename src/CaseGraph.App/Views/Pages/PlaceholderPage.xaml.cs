using System.Windows.Controls;

namespace CaseGraph.App.Views.Pages;

public partial class PlaceholderPage : Page
{
    public string PageTitle { get; }

    public string Description { get; }

    public PlaceholderPage()
        : this("Placeholder", "Placeholder description")
    {
    }

    public PlaceholderPage(string pageTitle, string description)
    {
        PageTitle = pageTitle;
        Description = description;

        InitializeComponent();
        DataContext = this;
    }
}
