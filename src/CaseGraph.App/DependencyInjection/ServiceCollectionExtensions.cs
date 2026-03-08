using CaseGraph.App.Services;
using CaseGraph.App.ViewModels;
using CaseGraph.Core.Abstractions;
using CaseGraph.Core.Diagnostics;
using CaseGraph.Infrastructure.IncidentWindow;
using CaseGraph.Infrastructure.Locations;
using CaseGraph.Infrastructure.Reports;
using CaseGraph.Infrastructure.Persistence;
using CaseGraph.Infrastructure.Services;
using CaseGraph.Infrastructure.Timeline;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using System.IO;

namespace CaseGraph.App.DependencyInjection;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddCaseGraphAppServices(this IServiceCollection services)
    {
        services.TryAddSingleton<IStartupStageReporter, StartupStageReporter>();
        services.AddSingleton<IWorkspacePathProvider, DefaultWorkspacePathProvider>();
        services.AddDbContextFactory<WorkspaceDbContext>((provider, options) =>
        {
            var paths = provider.GetRequiredService<IWorkspacePathProvider>();
            Directory.CreateDirectory(paths.WorkspaceRoot);
            var connectionStringBuilder = new SqliteConnectionStringBuilder
            {
                DataSource = paths.WorkspaceDbPath,
                Mode = SqliteOpenMode.ReadWriteCreate,
                DefaultTimeout = 5
            };
            options.UseSqlite(connectionStringBuilder.ConnectionString);
        });

        services.AddSingleton<INavigationService, NavigationService>();
        services.AddSingleton<IThemeService, ThemeService>();
        services.AddSingleton<IUserInteractionService, UserInteractionService>();
        services.AddSingleton<IAppRuntimePaths, AppRuntimePaths>();
        services.AddSingleton<IAppSessionState, AppSessionState>();
        services.AddSingleton<IRegistryValueStore, WindowsCurrentUserRegistryValueStore>();
        services.AddSingleton<ICrashDumpService, CrashDumpService>();
        services.AddSingleton<ISessionJournal>(provider =>
        {
            var runtimePaths = provider.GetRequiredService<IAppRuntimePaths>();
            return new SessionJournal(runtimePaths.SessionDirectory, maxLines: 500);
        });
        services.AddSingleton<SafeAsyncActionRunner>();
        services.AddSingleton<DebugBundleBuilder>();
        services.AddSingleton<IDiagnosticsService, DiagnosticsService>();
        services.AddSingleton<IWorkspaceMigrationService, WorkspaceMigrationService>();
        services.AddSingleton<IClock, SystemClock>();
        services.AddSingleton<WorkspaceDbRebuilder>();
        services.AddSingleton<WorkspaceDbInitializer>();
        services.AddSingleton<IWorkspaceDbInitializer>(provider => provider.GetRequiredService<WorkspaceDbInitializer>());
        services.AddSingleton<IWorkspaceDatabaseInitializer>(provider => provider.GetRequiredService<WorkspaceDbInitializer>());
        services.AddSingleton<IWorkspaceWriteGate, WorkspaceWriteGate>();
        services.AddSingleton<IAuditLogService, AuditLogService>();
        services.AddSingleton<IAuditQueryService, AuditQueryService>();
        services.AddSingleton<ICaseWorkspaceService, CaseWorkspaceService>();
        services.AddSingleton<ICaseQueryService, CaseQueryService>();
        services.AddSingleton<IEvidenceVaultService, EvidenceVaultService>();
        services.AddSingleton<IMessageSearchService, MessageSearchService>();
        services.AddSingleton<IMessageIngestService, MessageIngestService>();
        services.AddSingleton<LocationCsvParser>();
        services.AddSingleton<LocationJsonParser>();
        services.AddSingleton<LocationPlistParser>();
        services.AddSingleton<LocationsIngestJob>();
        services.AddSingleton<ITargetMessagePresenceIndexService, TargetMessagePresenceIndexService>();
        services.AddSingleton<ITargetRegistryService, TargetRegistryService>();
        services.AddSingleton<IAssociationGraphQueryService, AssociationGraphQueryService>();
        services.AddSingleton<IAssociationGraphExportPathBuilder, AssociationGraphExportPathBuilder>();
        services.AddSingleton<DossierBuilder>();
        services.AddSingleton<ReportExportService>();
        services.AddSingleton<TimelineQueryService>();
        services.AddSingleton<IncidentWindowQueryService>();
        services.AddSingleton<LocationQueryService>();
        services.AddSingleton<IJobQueryService, JobQueryService>();
        services.AddSingleton<JobQueueService>();
        services.AddSingleton<IJobQueueService>(provider => provider.GetRequiredService<JobQueueService>());
        services.AddHostedService<JobRunnerHostedService>();

        services.AddSingleton<TimelineViewModel>();
        services.AddSingleton<IncidentWindowViewModel>();
        services.AddSingleton<LocationsViewModel>();
        services.AddSingleton<ReportsViewModel>();
        services.AddSingleton<MainWindowViewModel>();
        services.AddSingleton<MainWindow>();

        return services;
    }
}
