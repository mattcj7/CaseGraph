using CaseGraph.Core.Abstractions;
using CaseGraph.Core.Diagnostics;
using CaseGraph.App.DependencyInjection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using System;
using System.IO;
using System.Windows;
using Wpf.Ui.Appearance;

namespace CaseGraph.App;

public partial class App : Application
{
    private IHost? _host;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        var workspaceRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "CaseGraphOffline"
        );
        var workspaceDbPath = Path.Combine(workspaceRoot, "workspace.db");

        AppFileLogger.Log($"Startup begin. Args: {string.Join(' ', e.Args)}");
        AppFileLogger.Log($"Workspace root: {workspaceRoot}");
        AppFileLogger.Log($"Workspace DB path: {workspaceDbPath}");

        try
        {
            AppFileLogger.Log("Building host.");
            _host = Host.CreateDefaultBuilder(e.Args)
                .ConfigureServices((_, services) => services.AddCaseGraphAppServices())
                .Build();

            AppFileLogger.Log("Running workspace DB initializer.");
            _host.Services
                .GetRequiredService<IWorkspaceDbInitializer>()
                .InitializeAsync(CancellationToken.None)
                .GetAwaiter()
                .GetResult();

            AppFileLogger.Log("Starting host.");
            _host.Start();

            ApplicationThemeManager.Apply(ApplicationTheme.Light);

            AppFileLogger.Log("Showing main window.");
            var mainWindow = _host.Services.GetRequiredService<MainWindow>();
            mainWindow.Show();
            AppFileLogger.Log("Startup complete.");
        }
        catch (Exception ex)
        {
            AppFileLogger.LogException("Startup failure.", ex);

            MessageBox.Show(
                ex.ToString(),
                "CaseGraph Startup Error",
                MessageBoxButton.OK,
                MessageBoxImage.Error
            );

            Environment.ExitCode = -1;

            if (_host is not null)
            {
                try
                {
                    _host.StopAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
                }
                catch (Exception stopException)
                {
                    AppFileLogger.LogException(
                        "Host stop failure during startup exception handling.",
                        stopException
                    );
                }

                _host.Dispose();
                _host = null;
            }

            Shutdown(-1);
        }
    }

    protected override async void OnExit(ExitEventArgs e)
    {
        if (_host is not null)
        {
            await _host.StopAsync(TimeSpan.FromSeconds(5));
            _host.Dispose();
        }

        base.OnExit(e);
    }
}
