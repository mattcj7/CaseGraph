using CaseGraph.App.Models;
using CaseGraph.App.Views.Pages;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;

namespace CaseGraph.App.Services;

public sealed class NavigationService : INavigationService
{
    private static readonly IReadOnlyDictionary<NavigationPage, (string Title, string Description)> Pages =
        new Dictionary<NavigationPage, (string Title, string Description)>
        {
            [NavigationPage.Dashboard] = ("Dashboard", "Case overview, readiness, and quick actions."),
            [NavigationPage.Import] = ("Import", "Queue-backed evidence import with progress, cancel, and activity feed."),
            [NavigationPage.Search] = ("Search", "Unified evidence search placeholder with future filter support."),
            [NavigationPage.Timeline] = ("Timeline", "Chronological event analysis placeholder view."),
            [NavigationPage.PeopleTargets] = ("People / Targets", "People and target profile placeholder workspace."),
            [NavigationPage.Locations] = ("Locations", "Geospatial analysis placeholder with map and proximity tools."),
            [NavigationPage.Associations] = ("Associations", "Association graph placeholder for relationship analysis."),
            [NavigationPage.Reports] = ("Reports", "Report and export orchestration placeholder."),
            [NavigationPage.ReviewQueue] = ("Review Queue", "Background job history for ingest and verification tasks."),
            [NavigationPage.Settings] = ("Settings", "Application settings placeholder and environment controls.")
        };

    public IReadOnlyList<NavigationItem> GetNavigationItems()
    {
        return Pages
            .Select(entry => new NavigationItem(entry.Key, entry.Value.Title, entry.Value.Description))
            .ToList();
    }

    public FrameworkElement CreateView(NavigationPage page)
    {
        if (!Pages.TryGetValue(page, out var content))
        {
            throw new ArgumentOutOfRangeException(nameof(page), page, "Unknown navigation page requested.");
        }

        if (page == NavigationPage.Import)
        {
            return new ImportView();
        }

        if (page == NavigationPage.ReviewQueue)
        {
            return new ReviewQueueView();
        }

        return new PlaceholderView(content.Title, content.Description);
    }
}
