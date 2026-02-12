using CaseGraph.App.Models;
using CaseGraph.App.Views.Pages;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Controls;

namespace CaseGraph.App.Services;

public sealed class NavigationService : INavigationService
{
    private static readonly IReadOnlyDictionary<NavigationPage, (string Title, string Description)> Pages =
        new Dictionary<NavigationPage, (string Title, string Description)>
        {
            [NavigationPage.Dashboard] = ("Dashboard", "Case overview, readiness, and quick actions."),
            [NavigationPage.Import] = ("Import", "Ingestion entry point placeholder for future ticket workflows."),
            [NavigationPage.Search] = ("Search", "Unified evidence search placeholder with future filter support."),
            [NavigationPage.Timeline] = ("Timeline", "Chronological event analysis placeholder view."),
            [NavigationPage.PeopleTargets] = ("People / Targets", "People and target profile placeholder workspace."),
            [NavigationPage.Locations] = ("Locations", "Geospatial analysis placeholder with map and proximity tools."),
            [NavigationPage.Associations] = ("Associations", "Association graph placeholder for relationship analysis."),
            [NavigationPage.Reports] = ("Reports", "Report and export orchestration placeholder."),
            [NavigationPage.ReviewQueue] = ("Review Queue", "Triage queue placeholder for analyst review tasks."),
            [NavigationPage.Settings] = ("Settings", "Application settings placeholder and environment controls.")
        };

    public IReadOnlyList<NavigationItem> GetNavigationItems()
    {
        return Pages
            .Select(entry => new NavigationItem(entry.Key, entry.Value.Title, entry.Value.Description))
            .ToList();
    }

    public Page CreatePage(NavigationPage page)
    {
        if (!Pages.TryGetValue(page, out var content))
        {
            throw new ArgumentOutOfRangeException(nameof(page), page, "Unknown navigation page requested.");
        }

        return new PlaceholderPage(content.Title, content.Description);
    }
}
