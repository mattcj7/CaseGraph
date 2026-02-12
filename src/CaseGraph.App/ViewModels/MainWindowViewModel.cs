using CaseGraph.App.Models;
using CaseGraph.App.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System.Collections.Generic;

namespace CaseGraph.App.ViewModels;

public partial class MainWindowViewModel : ObservableObject
{
    private readonly INavigationService _navigationService;
    private readonly IThemeService _themeService;

    public IReadOnlyList<NavigationItem> NavigationItems { get; }

    public IReadOnlyList<string> CaseOptions { get; } =
        new[] { "Case Alpha", "Case Bravo", "Case Charlie" };

    [ObservableProperty]
    private NavigationItem? selectedNavigationItem;

    [ObservableProperty]
    private object? currentPage;

    [ObservableProperty]
    private string? selectedCase;

    [ObservableProperty]
    private string globalSearchQuery = string.Empty;

    [ObservableProperty]
    private bool isDarkTheme;

    [ObservableProperty]
    private bool isEvidenceDrawerOpen;

    public IRelayCommand ToggleThemeCommand { get; }

    public IRelayCommand ToggleEvidenceDrawerCommand { get; }

    public MainWindowViewModel(INavigationService navigationService, IThemeService themeService)
    {
        _navigationService = navigationService;
        _themeService = themeService;

        NavigationItems = _navigationService.GetNavigationItems();
        SelectedCase = CaseOptions[0];
        IsDarkTheme = _themeService.IsDarkTheme;
        IsEvidenceDrawerOpen = false;

        ToggleThemeCommand = new RelayCommand(() => IsDarkTheme = !IsDarkTheme);
        ToggleEvidenceDrawerCommand = new RelayCommand(() => IsEvidenceDrawerOpen = !IsEvidenceDrawerOpen);

        SelectedNavigationItem = NavigationItems[0];
    }

    partial void OnSelectedNavigationItemChanged(NavigationItem? value)
    {
        if (value is null)
        {
            return;
        }

        CurrentPage = _navigationService.CreatePage(value.Page);
    }

    partial void OnIsDarkThemeChanged(bool value)
    {
        _themeService.SetTheme(value);
    }
}
