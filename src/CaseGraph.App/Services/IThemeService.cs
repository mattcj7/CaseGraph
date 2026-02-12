namespace CaseGraph.App.Services;

public interface IThemeService
{
    bool IsDarkTheme { get; }

    void SetTheme(bool isDarkTheme);
}
