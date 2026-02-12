namespace CaseGraph.App.Models;

public sealed record NavigationItem(
    NavigationPage Page,
    string Title,
    string Description
);
