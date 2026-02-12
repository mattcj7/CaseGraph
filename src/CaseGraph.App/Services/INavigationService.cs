using CaseGraph.App.Models;
using System.Collections.Generic;
using System.Windows.Controls;

namespace CaseGraph.App.Services;

public interface INavigationService
{
    IReadOnlyList<NavigationItem> GetNavigationItems();

    Page CreatePage(NavigationPage page);
}
