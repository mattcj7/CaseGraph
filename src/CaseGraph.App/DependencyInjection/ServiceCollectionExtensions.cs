using CaseGraph.App.Services;
using CaseGraph.App.ViewModels;
using CaseGraph.Core.Abstractions;
using CaseGraph.Infrastructure.Persistence;
using CaseGraph.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using System.IO;

namespace CaseGraph.App.DependencyInjection;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddCaseGraphAppServices(this IServiceCollection services)
    {
        services.AddSingleton<IWorkspacePathProvider, DefaultWorkspacePathProvider>();
        services.AddDbContextFactory<WorkspaceDbContext>((provider, options) =>
        {
            var paths = provider.GetRequiredService<IWorkspacePathProvider>();
            Directory.CreateDirectory(paths.WorkspaceRoot);

            options.UseSqlite($"Data Source={paths.WorkspaceDbPath}");
        });

        services.AddSingleton<INavigationService, NavigationService>();
        services.AddSingleton<IThemeService, ThemeService>();
        services.AddSingleton<IUserInteractionService, UserInteractionService>();
        services.AddSingleton<IClock, SystemClock>();
        services.AddSingleton<IWorkspaceDatabaseInitializer, WorkspaceDatabaseInitializer>();
        services.AddSingleton<IAuditLogService, AuditLogService>();
        services.AddSingleton<ICaseWorkspaceService, CaseWorkspaceService>();
        services.AddSingleton<IEvidenceVaultService, EvidenceVaultService>();

        services.AddSingleton<MainWindowViewModel>();
        services.AddSingleton<MainWindow>();

        return services;
    }
}
