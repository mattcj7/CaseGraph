using CaseGraph.App.Services;
using CaseGraph.App.ViewModels;
using CaseGraph.Core.Abstractions;
using CaseGraph.Infrastructure.Services;
using Microsoft.Extensions.DependencyInjection;

namespace CaseGraph.App.DependencyInjection;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddCaseGraphAppServices(this IServiceCollection services)
    {
        services.AddSingleton<INavigationService, NavigationService>();
        services.AddSingleton<IThemeService, ThemeService>();
        services.AddSingleton<IClock, SystemClock>();

        services.AddSingleton<MainWindowViewModel>();
        services.AddSingleton<MainWindow>();

        return services;
    }
}
