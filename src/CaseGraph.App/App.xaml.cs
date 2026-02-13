using CaseGraph.Core.Abstractions;
using CaseGraph.App.DependencyInjection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using System;
using System.IO;
using System.Text;
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

        StartupFileLogger.Log($"Startup begin. Args: {string.Join(' ', e.Args)}");
        StartupFileLogger.Log($"Workspace root: {workspaceRoot}");
        StartupFileLogger.Log($"Workspace DB path: {workspaceDbPath}");

        try
        {
            StartupFileLogger.Log("Building host.");
            _host = Host.CreateDefaultBuilder(e.Args)
                .ConfigureServices((_, services) => services.AddCaseGraphAppServices())
                .Build();

            StartupFileLogger.Log("Running workspace DB initializer.");
            _host.Services
                .GetRequiredService<IWorkspaceDbInitializer>()
                .InitializeAsync(CancellationToken.None)
                .GetAwaiter()
                .GetResult();

            StartupFileLogger.Log("Starting host.");
            _host.Start();

            ApplicationThemeManager.Apply(ApplicationTheme.Light);

            StartupFileLogger.Log("Showing main window.");
            var mainWindow = _host.Services.GetRequiredService<MainWindow>();
            mainWindow.Show();
            StartupFileLogger.Log("Startup complete.");
        }
        catch (Exception ex)
        {
            StartupFileLogger.LogException("Startup failure.", ex);

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
                    StartupFileLogger.LogException(
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

internal static class StartupFileLogger
{
    private static readonly object Sync = new();

    public static void Log(string message)
    {
        try
        {
            var now = DateTimeOffset.Now;
            var logDirectory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "CaseGraphOffline",
                "logs"
            );
            Directory.CreateDirectory(logDirectory);

            var logPath = Path.Combine(logDirectory, $"app-{now:yyyyMMdd}.log");
            var line = $"{now:yyyy-MM-dd HH:mm:ss.fff zzz} {message}{Environment.NewLine}";

            lock (Sync)
            {
                File.AppendAllText(logPath, line, Encoding.UTF8);
            }
        }
        catch
        {
            // Never allow logging failures to crash startup.
        }
    }

    public static void LogException(string context, Exception ex)
    {
        Log($"{context} {ex}");
    }
}
