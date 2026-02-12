using System.Windows.Controls;

namespace CaseGraph.App.Views.Pages;

public partial class PlaceholderView : UserControl
{
    public string PageTitle { get; }

    public string Description { get; }

    public PlaceholderView()
        : this("Placeholder", "Placeholder description")
    {
    }

    public PlaceholderView(string pageTitle, string description)
    {
        PageTitle = pageTitle;
        Description = description;

        InitializeComponent();
        DataContext = this;
    }
}
