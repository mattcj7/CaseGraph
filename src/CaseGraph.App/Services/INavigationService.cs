using CaseGraph.App.Models;
using System.Collections.Generic;
using System.Windows;

namespace CaseGraph.App.Services;

public interface INavigationService
{
    IReadOnlyList<NavigationItem> GetNavigationItems();

    FrameworkElement CreateView(NavigationPage page);
}
