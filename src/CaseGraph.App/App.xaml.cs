using CaseGraph.Core.Abstractions;
using CaseGraph.Core.Diagnostics;
using CaseGraph.App.DependencyInjection;
using CaseGraph.App.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using System;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using Wpf.Ui.Appearance;

namespace CaseGraph.App;

public partial class App : Application
{
    private IHost? _host;
    private bool _fatalShutdownRequested;

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
        RegisterGlobalExceptionHandlers();

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
            var correlationId = UiExceptionReporter.LogException("Startup failure.", ex);
            ShowFatalErrorAndShutdown("CaseGraph Startup Error", ex, correlationId);
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

    private void RegisterGlobalExceptionHandlers()
    {
        DispatcherUnhandledException -= OnDispatcherUnhandledException;
        DispatcherUnhandledException += OnDispatcherUnhandledException;

        AppDomain.CurrentDomain.UnhandledException -= OnAppDomainUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnAppDomainUnhandledException;

        TaskScheduler.UnobservedTaskException -= OnUnobservedTaskException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;
    }

    private void OnDispatcherUnhandledException(
        object sender,
        DispatcherUnhandledExceptionEventArgs e
    )
    {
        var correlationId = UiExceptionReporter.LogException("Unhandled UI exception.", e.Exception);
        e.Handled = true;
        ShowFatalErrorAndShutdown("CaseGraph UI Error", e.Exception, correlationId);
    }

    private void OnAppDomainUnhandledException(object? sender, UnhandledExceptionEventArgs e)
    {
        if (e.ExceptionObject is Exception ex)
        {
            var correlationId = UiExceptionReporter.LogException("Unhandled AppDomain exception.", ex);
            ShowFatalErrorAndShutdown("CaseGraph Fatal Error", ex, correlationId);
        }
        else
        {
            var correlationId = UiExceptionReporter.LogExceptionObject(
                "Unhandled AppDomain exception (non-Exception object).",
                e.ExceptionObject
            );
            ShowFatalErrorAndShutdown("CaseGraph Fatal Error", null, correlationId);
        }
    }

    private void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        var correlationId = UiExceptionReporter.LogException("Unobserved task exception.", e.Exception);
        e.SetObserved();
        ShowFatalErrorAndShutdown("CaseGraph Background Task Error", e.Exception, correlationId);
    }

    private void ShowFatalErrorAndShutdown(string title, Exception? ex, string correlationId)
    {
        if (_fatalShutdownRequested)
        {
            return;
        }

        _fatalShutdownRequested = true;

        try
        {
            UiExceptionReporter.ShowErrorDialog(title, ex, correlationId);
        }
        catch
        {
        }

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
                    "Host stop failure during fatal shutdown handling.",
                    stopException
                );
            }

            _host.Dispose();
            _host = null;
        }

        Shutdown(-1);
    }
}
