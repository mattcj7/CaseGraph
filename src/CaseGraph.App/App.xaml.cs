using CaseGraph.App.DependencyInjection;
using CaseGraph.App.Services;
using CaseGraph.Core.Abstractions;
using CaseGraph.Core.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Threading;
using Wpf.Ui.Appearance;

namespace CaseGraph.App;

public partial class App : Application
{
    private const string SelfTestArgument = "--self-test";

    private IHost? _host;
    private ISessionJournal? _sessionJournal;
    private CancellationTokenRegistration _applicationStoppingRegistration;
    private CancellationTokenRegistration _applicationStoppedRegistration;
    private bool _fatalShutdownRequested;
    private bool _selfTestMode;
    private bool _cleanExitRecorded;
    private Mutex? _singleInstanceMutex;
    private bool _singleInstanceMutexHeld;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        _selfTestMode = e.Args.Any(
            arg => string.Equals(arg, SelfTestArgument, StringComparison.OrdinalIgnoreCase)
        );
        var hostArgs = e.Args
            .Where(arg => !string.Equals(arg, SelfTestArgument, StringComparison.OrdinalIgnoreCase))
            .ToArray();

        var workspaceRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "CaseGraphOffline"
        );
        var workspaceDbPath = Path.Combine(workspaceRoot, "workspace.db");

        AppFileLogger.Log($"Startup begin. Args: {string.Join(' ', e.Args)}");
        AppFileLogger.Log($"Workspace root: {workspaceRoot}");
        AppFileLogger.Log($"Workspace DB path: {workspaceDbPath}");

        if (!TryAcquireSingleInstanceGuard())
        {
            AppFileLogger.LogEvent(
                eventName: "SingleInstanceGuardRejected",
                level: "WARN",
                message: "Another instance is already running."
            );
            MessageBox.Show(
                "Another instance is already running.",
                "CaseGraph",
                MessageBoxButton.OK,
                MessageBoxImage.Information
            );
            Shutdown(-1);
            return;
        }

        RegisterGlobalExceptionHandlers();

        try
        {
            AppFileLogger.Log("Building host.");
            _host = Host.CreateDefaultBuilder(hostArgs)
                .ConfigureServices((_, services) => services.AddCaseGraphAppServices())
                .Build();
            InitializeSessionJournal(e.Args);

            var migrationSucceeded = await EnsureWorkspaceMigratedOrShowErrorAsync(
                "Startup workspace migration failed."
            );
            if (!migrationSucceeded)
            {
                return;
            }

            if (_selfTestMode)
            {
                var selfTestExitCode = await RunSelfTestAsync();
                await ShutdownAsync(selfTestExitCode);
                return;
            }

            AppFileLogger.Log("Starting host.");
            await _host.StartAsync(CancellationToken.None);
            RegisterHostLifetimeBreadcrumbs();

            ApplicationThemeManager.Apply(ApplicationTheme.Light);

            AppFileLogger.Log("Showing main window.");
            var mainWindow = _host.Services.GetRequiredService<MainWindow>();
            mainWindow.Show();
            AppFileLogger.Log("Startup complete.");
            _sessionJournal?.RecordStartupComplete();
        }
        catch (Exception ex)
        {
            await HandleFatalExceptionAsync(
                "CaseGraph Startup Error",
                "Startup failure.",
                ex
            );
        }
    }

    protected override async void OnExit(ExitEventArgs e)
    {
        RecordCleanExitIfNeeded("OnExit");
        UnregisterGlobalExceptionHandlers();
        UnregisterHostLifetimeBreadcrumbs();
        ReleaseSingleInstanceGuard();

        if (_host is not null)
        {
            try
            {
                await _host.StopAsync(TimeSpan.FromSeconds(5));
            }
            catch (Exception stopException)
            {
                AppFileLogger.LogException("Host stop failure during app exit.", stopException);
            }

            _host.Dispose();
            _host = null;
        }

        base.OnExit(e);
    }

    internal Task HandleFatalExceptionAsync(string title, string context, Exception ex)
    {
        return HandleFatalReportAsync(
            title,
            "The application encountered a fatal error and must close.",
            UiExceptionReporter.LogFatalException(
                context,
                ex,
                ResolveDiagnosticsService(),
                ResolveAppSessionState()
            )
        );
    }

    private async Task<bool> EnsureWorkspaceMigratedOrShowErrorAsync(string context)
    {
        if (_host is null)
        {
            throw new InvalidOperationException("Host was not built before migration check.");
        }

        while (true)
        {
            try
            {
                var migrationService = _host.Services.GetRequiredService<IWorkspaceMigrationService>();
                await migrationService.EnsureMigratedAsync(CancellationToken.None);
                return true;
            }
            catch (TimeoutException ex)
            {
                var snapshot = ResolveDiagnosticsService()?.GetSnapshot();
                var dbPath = snapshot?.WorkspaceDbPath ?? "(unknown)";
                var logsDirectory = snapshot?.LogsDirectory ?? AppFileLogger.GetLogDirectory();

                AppFileLogger.LogEvent(
                    eventName: "WorkspaceInitializationWatchdogTimeout",
                    level: "ERROR",
                    message: "Workspace initialization timed out; workspace.db may be locked by another process or another CaseGraph instance.",
                    ex: ex,
                    fields: new Dictionary<string, object?>
                    {
                        ["workspaceDbPath"] = dbPath,
                        ["logsDirectory"] = logsDirectory
                    }
                );
                AppFileLogger.Flush();

                if (_selfTestMode)
                {
                    await ShutdownAsync(-1);
                    return false;
                }

                var retryResult = MessageBox.Show(
                    "Workspace initialization timed out because the database appears to be locked."
                    + Environment.NewLine
                    + Environment.NewLine
                    + $"Workspace DB: {dbPath}"
                    + Environment.NewLine
                    + $"Logs: {logsDirectory}"
                    + Environment.NewLine
                    + Environment.NewLine
                    + "Likely cause: a running job or another CaseGraph instance holding a write lock."
                    + Environment.NewLine
                    + "Click Yes to retry now, or No to continue with the app open and retry later.",
                    "CaseGraph Workspace Busy",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Warning
                );

                if (retryResult == MessageBoxResult.Yes)
                {
                    continue;
                }

                AppFileLogger.LogEvent(
                    eventName: "WorkspaceInitializationDeferred",
                    level: "WARN",
                    message: "Workspace initialization deferred by user after timeout.",
                    fields: new Dictionary<string, object?>
                    {
                        ["workspaceDbPath"] = dbPath,
                        ["logsDirectory"] = logsDirectory
                    }
                );
                return true;
            }
            catch (Exception ex)
            {
                var report = UiExceptionReporter.LogFatalException(
                    context,
                    ex,
                    ResolveDiagnosticsService(),
                    ResolveAppSessionState()
                );

                AppFileLogger.Flush();
                if (!_selfTestMode)
                {
                    var snapshot = ResolveDiagnosticsService()?.GetSnapshot();
                    var dbPath = snapshot?.WorkspaceDbPath ?? "(unknown)";
                    UiExceptionReporter.ShowCrashDialog(
                        "CaseGraph Workspace Error",
                        $"Workspace database migration failed.{Environment.NewLine}Workspace DB: {dbPath}",
                        report,
                        ResolveDiagnosticsService()
                    );
                }

                await ShutdownAsync(-1);
                return false;
            }
        }
    }

    private async Task<int> RunSelfTestAsync()
    {
        if (_host is null)
        {
            AppFileLogger.Log("[SelfTest] Host unavailable.");
            return 1;
        }

        try
        {
            AppFileLogger.Log("[SelfTest] Starting host.");
            await _host.StartAsync(CancellationToken.None);

            var caseQueryService = _host.Services.GetRequiredService<ICaseQueryService>();
            var cases = await caseQueryService.GetRecentCasesAsync(CancellationToken.None);
            AppFileLogger.Log($"[SelfTest] Case query succeeded. CaseCount={cases.Count}");
            AppFileLogger.Log("[SelfTest] Success.");
            return 0;
        }
        catch (Exception ex)
        {
            var report = UiExceptionReporter.LogFatalException(
                "Self-test execution failed.",
                ex,
                ResolveDiagnosticsService(),
                ResolveAppSessionState()
            );
            AppFileLogger.Log(
                $"[SelfTest] Failure CorrelationId={report.CorrelationId} LogPath={report.LogPath}"
            );
            AppFileLogger.Flush();
            return 1;
        }
    }

    private void RegisterGlobalExceptionHandlers()
    {
        DispatcherUnhandledException -= OnDispatcherUnhandledException;
        DispatcherUnhandledException += OnDispatcherUnhandledException;

        AppDomain.CurrentDomain.UnhandledException -= OnAppDomainUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnAppDomainUnhandledException;

        TaskScheduler.UnobservedTaskException -= OnUnobservedTaskException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;

        AppDomain.CurrentDomain.ProcessExit -= OnProcessExit;
        AppDomain.CurrentDomain.ProcessExit += OnProcessExit;
    }

    private void UnregisterGlobalExceptionHandlers()
    {
        DispatcherUnhandledException -= OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException -= OnAppDomainUnhandledException;
        TaskScheduler.UnobservedTaskException -= OnUnobservedTaskException;
        AppDomain.CurrentDomain.ProcessExit -= OnProcessExit;
    }

    private async void OnDispatcherUnhandledException(
        object sender,
        DispatcherUnhandledExceptionEventArgs e
    )
    {
        e.Handled = true;
        try
        {
            AppFileLogger.LogEvent(
                eventName: "DispatcherUnhandledExceptionCaptured",
                level: "FATAL",
                message: "Unhandled UI-thread exception captured.",
                ex: e.Exception
            );
            await HandleFatalExceptionAsync(
                "CaseGraph UI Error",
                "Unhandled UI-thread exception.",
                e.Exception
            );
        }
        catch (Exception handlerEx)
        {
            AppFileLogger.LogEvent(
                eventName: "DispatcherUnhandledExceptionHandlerFailed",
                level: "WARN",
                message: "DispatcherUnhandledException handler failed.",
                ex: handlerEx,
                fields: new Dictionary<string, object?>
                {
                    ["originalException"] = e.Exception.ToString()
                }
            );
        }
    }

    private void OnAppDomainUnhandledException(object? sender, UnhandledExceptionEventArgs e)
    {
        try
        {
            if (e.ExceptionObject is Exception ex)
            {
                AppFileLogger.LogEvent(
                    eventName: "AppDomainUnhandledExceptionCaptured",
                    level: "FATAL",
                    message: "Unhandled AppDomain exception captured.",
                    ex: ex
                );

                HandleFatalExceptionAsync(
                    "CaseGraph Fatal Error",
                    "Unhandled AppDomain exception.",
                    ex
                )
                    .GetAwaiter()
                    .GetResult();
                return;
            }

            AppFileLogger.LogEvent(
                eventName: "AppDomainUnhandledNonExceptionCaptured",
                level: "FATAL",
                message: "Unhandled AppDomain exception object captured.",
                fields: new Dictionary<string, object?>
                {
                    ["exceptionObject"] = e.ExceptionObject?.ToString() ?? "(null)"
                }
            );

            var report = UiExceptionReporter.LogFatalExceptionObject(
                "Unhandled AppDomain exception (non-Exception object).",
                e.ExceptionObject,
                ResolveDiagnosticsService(),
                ResolveAppSessionState()
            );
            HandleFatalReportAsync(
                "CaseGraph Fatal Error",
                "The application encountered a fatal non-Exception error and must close.",
                report
            )
                .GetAwaiter()
                .GetResult();
        }
        catch (Exception handlerEx)
        {
            AppFileLogger.LogEvent(
                eventName: "AppDomainUnhandledExceptionHandlerFailed",
                level: "WARN",
                message: "AppDomain unhandled exception handler failed.",
                ex: handlerEx,
                fields: new Dictionary<string, object?>
                {
                    ["originalExceptionObject"] = e.ExceptionObject?.ToString() ?? "(null)"
                }
            );
        }
    }

    private void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        try
        {
            UnobservedTaskExceptionContainment.Handle(
                e,
                "Unobserved background task exception captured.",
                scheduleNotification: ScheduleUnobservedTaskExceptionNotification
            );
        }
        catch (Exception handlerEx)
        {
            AppFileLogger.LogEvent(
                eventName: "UnobservedTaskExceptionHandlerFailed",
                level: "WARN",
                message: "Unobserved task exception handler failed.",
                ex: handlerEx,
                fields: new Dictionary<string, object?>
                {
                    ["originalException"] = e.Exception?.ToString()
                }
            );
        }
    }

    private async Task HandleFatalReportAsync(
        string title,
        string whatHappened,
        FatalErrorReport report
    )
    {
        if (_fatalShutdownRequested)
        {
            return;
        }

        _fatalShutdownRequested = true;
        _sessionJournal?.WriteEvent(
            "FatalShutdownRequested",
            new Dictionary<string, object?>
            {
                ["title"] = title,
                ["correlationId"] = report.CorrelationId
            },
            report.CorrelationId
        );
        AppFileLogger.Flush();

        try
        {
            if (!_selfTestMode)
            {
                UiExceptionReporter.ShowCrashDialog(
                    title,
                    whatHappened,
                    report,
                    ResolveDiagnosticsService()
                );
            }
        }
        catch (Exception dialogEx)
        {
            AppFileLogger.LogException("Crash dialog display failed.", dialogEx);
        }

        await ShutdownAsync(-1);
    }

    private async Task ShutdownAsync(int exitCode)
    {
        Environment.ExitCode = exitCode;
        if (exitCode == 0)
        {
            RecordCleanExitIfNeeded("ShutdownAsync");
        }
        else
        {
            _sessionJournal?.WriteEvent(
                "ShutdownRequested",
                new Dictionary<string, object?>
                {
                    ["exitCode"] = exitCode
                }
            );
        }

        UnregisterGlobalExceptionHandlers();
        UnregisterHostLifetimeBreadcrumbs();
        ReleaseSingleInstanceGuard();

        if (_host is not null)
        {
            try
            {
                await _host.StopAsync(TimeSpan.FromSeconds(5));
            }
            catch (Exception stopException)
            {
                AppFileLogger.LogException(
                    "Host stop failure during shutdown.",
                    stopException
                );
            }

            _host.Dispose();
            _host = null;
        }

        Shutdown(exitCode);
    }

    private bool TryAcquireSingleInstanceGuard()
    {
        var mutexName = $"Local\\CaseGraphOffline_{Environment.UserName}";
        var mutex = new Mutex(initiallyOwned: false, mutexName);
        var acquired = false;

        try
        {
            acquired = mutex.WaitOne(0, false);
        }
        catch (AbandonedMutexException)
        {
            acquired = true;
        }

        if (!acquired)
        {
            mutex.Dispose();
            return false;
        }

        _singleInstanceMutex = mutex;
        _singleInstanceMutexHeld = true;
        return true;
    }

    private void ReleaseSingleInstanceGuard()
    {
        var mutex = _singleInstanceMutex;
        _singleInstanceMutex = null;

        if (mutex is null)
        {
            return;
        }

        try
        {
            if (_singleInstanceMutexHeld)
            {
                mutex.ReleaseMutex();
            }
        }
        catch (ApplicationException)
        {
        }
        finally
        {
            _singleInstanceMutexHeld = false;
            mutex.Dispose();
        }
    }

    private IDiagnosticsService? ResolveDiagnosticsService()
    {
        return _host?.Services.GetService<IDiagnosticsService>();
    }

    private IAppSessionState? ResolveAppSessionState()
    {
        return _host?.Services.GetService<IAppSessionState>();
    }

    private void InitializeSessionJournal(string[] args)
    {
        _sessionJournal = _host?.Services.GetService<ISessionJournal>();
        if (_sessionJournal is null)
        {
            return;
        }

        var start = _sessionJournal.StartNewSession();
        _sessionJournal.WriteEvent(
            "StartupBegin",
            new Dictionary<string, object?>
            {
                ["args"] = string.Join(' ', args),
                ["selfTest"] = _selfTestMode
            }
        );

        if (start.PreviousSessionEndedUnexpectedly)
        {
            AppFileLogger.LogEvent(
                eventName: "PreviousSessionEndedUnexpectedly",
                level: "WARN",
                message: "Previous session ended unexpectedly.",
                fields: new Dictionary<string, object?>
                {
                    ["sessionId"] = start.SessionId
                }
            );
        }
    }

    private void RegisterHostLifetimeBreadcrumbs()
    {
        if (_host is null || _sessionJournal is null)
        {
            return;
        }

        var lifetime = _host.Services.GetRequiredService<IHostApplicationLifetime>();
        _applicationStoppingRegistration = lifetime.ApplicationStopping.Register(() =>
        {
            _sessionJournal.WriteEvent("HostApplicationStopping");
        });
        _applicationStoppedRegistration = lifetime.ApplicationStopped.Register(() =>
        {
            _sessionJournal.WriteEvent("HostApplicationStopped");
        });
    }

    private void UnregisterHostLifetimeBreadcrumbs()
    {
        _applicationStoppingRegistration.Dispose();
        _applicationStoppingRegistration = default;
        _applicationStoppedRegistration.Dispose();
        _applicationStoppedRegistration = default;
    }

    private void OnProcessExit(object? sender, EventArgs e)
    {
        _sessionJournal?.WriteEvent("ProcessExit");
    }

    private void ScheduleUnobservedTaskExceptionNotification(AggregateException ex)
    {
        var dispatcher = Current?.Dispatcher;
        if (dispatcher is null || dispatcher.HasShutdownStarted || dispatcher.HasShutdownFinished)
        {
            return;
        }

        _ = dispatcher.BeginInvoke(
            DispatcherPriority.Background,
            new Action(() =>
            {
                AppFileLogger.LogEvent(
                    eventName: "UnobservedTaskExceptionUiNotification",
                    level: "WARN",
                    message: "Background task exception observed and contained.",
                    ex: ex
                );
            })
        );
    }

    private void RecordCleanExitIfNeeded(string reason)
    {
        if (_cleanExitRecorded || _fatalShutdownRequested || Environment.ExitCode != 0)
        {
            return;
        }

        _cleanExitRecorded = true;
        _sessionJournal?.MarkCleanExit(reason);
    }
}
