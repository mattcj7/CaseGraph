using System.Windows;
using System.Windows.Media;
using Wpf.Ui.Appearance;

namespace CaseGraph.App.Services;

public sealed class ThemeService : IThemeService
{
    public bool IsDarkTheme { get; private set; }

    public ThemeService()
    {
        IsDarkTheme = ApplicationThemeManager.GetAppTheme() == ApplicationTheme.Dark;
        ApplyPalette(IsDarkTheme);
    }

    public void SetTheme(bool isDarkTheme)
    {
        IsDarkTheme = isDarkTheme;

        ApplicationThemeManager.Apply(isDarkTheme ? ApplicationTheme.Dark : ApplicationTheme.Light);
        ApplyPalette(isDarkTheme);
    }

    private static void ApplyPalette(bool isDarkTheme)
    {
        if (isDarkTheme)
        {
            SetBrush("ShellBackgroundBrush", "#FF101317");
            SetBrush("PanelBackgroundBrush", "#FF171B20");
            SetBrush("PanelBorderBrush", "#FF2A3138");
            SetBrush("ContentBackgroundBrush", "#FF12161B");
            SetBrush("DrawerBackgroundBrush", "#FF171C22");
            SetBrush("PrimaryTextBrush", "#FFF3F6FB");
            SetBrush("SecondaryTextBrush", "#FFAAB4C1");
            SetBrush("AccentBrush", "#FF5EA8FF");
            SetBrush("NavigationHoverBrush", "#FF25303A");
            SetBrush("NavigationSelectedBrush", "#FF1D3B57");
            return;
        }

        SetBrush("ShellBackgroundBrush", "#FFF3F5F8");
        SetBrush("PanelBackgroundBrush", "#FFFFFFFF");
        SetBrush("PanelBorderBrush", "#FFE1E7EF");
        SetBrush("ContentBackgroundBrush", "#FFFFFFFF");
        SetBrush("DrawerBackgroundBrush", "#FFF8FAFD");
        SetBrush("PrimaryTextBrush", "#FF101820");
        SetBrush("SecondaryTextBrush", "#FF4A5868");
        SetBrush("AccentBrush", "#FF1E68D6");
        SetBrush("NavigationHoverBrush", "#FFEDF3FC");
        SetBrush("NavigationSelectedBrush", "#FFDCEBFF");
    }

    private static void SetBrush(string key, string hex)
    {
        Application.Current.Resources[key] = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
    }
}
