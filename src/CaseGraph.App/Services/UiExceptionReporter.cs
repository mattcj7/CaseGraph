using CaseGraph.App.Views.Dialogs;
using CaseGraph.Core.Diagnostics;
using System.Text;
using System.Windows;
using System.Windows.Threading;

namespace CaseGraph.App.Services;

internal static class UiExceptionReporter
{
    public static FatalErrorReport LogFatalException(
        string context,
        Exception ex,
        IDiagnosticsService? diagnosticsService = null,
        IAppSessionState? appSessionState = null
    )
    {
        var correlationId = ResolveCorrelationId();
        var fields = BuildStructuredContextSafe(correlationId, appSessionState);
        AppFileLogger.LogEvent(
            eventName: "FatalException",
            level: "FATAL",
            message: context,
            ex: ex,
            fields: fields
        );
        var diagnosticsText = BuildDiagnosticsTextSafe(
            context,
            correlationId,
            ex,
            diagnosticsService
        );
        return new FatalErrorReport(correlationId, AppFileLogger.GetCurrentLogPath(), diagnosticsText);
    }

    public static FatalErrorReport LogHandledException(
        string context,
        Exception ex,
        IDiagnosticsService? diagnosticsService = null,
        IAppSessionState? appSessionState = null
    )
    {
        var correlationId = ResolveCorrelationId();
        var fields = BuildStructuredContextSafe(correlationId, appSessionState);
        AppFileLogger.LogEvent(
            eventName: "HandledException",
            level: "ERROR",
            message: context,
            ex: ex,
            fields: fields
        );
        var diagnosticsText = BuildDiagnosticsTextSafe(
            context,
            correlationId,
            ex,
            diagnosticsService
        );
        return new FatalErrorReport(correlationId, AppFileLogger.GetCurrentLogPath(), diagnosticsText);
    }

    public static FatalErrorReport LogFatalExceptionObject(
        string context,
        object? exceptionObject,
        IDiagnosticsService? diagnosticsService = null,
        IAppSessionState? appSessionState = null
    )
    {
        var correlationId = ResolveCorrelationId();
        var fields = BuildStructuredContextSafe(correlationId, appSessionState);
        fields["exceptionObject"] = exceptionObject?.ToString() ?? "(null)";
        AppFileLogger.LogEvent(
            eventName: "FatalExceptionObject",
            level: "FATAL",
            message: context,
            fields: fields
        );
        var diagnosticsText = BuildDiagnosticsTextSafe(
            context,
            correlationId,
            null,
            diagnosticsService
        );
        return new FatalErrorReport(correlationId, AppFileLogger.GetCurrentLogPath(), diagnosticsText);
    }

    public static void ShowCrashDialog(
        string title,
        string whatHappened,
        FatalErrorReport report,
        IDiagnosticsService? diagnosticsService = null
    )
    {
        if (Application.Current?.Dispatcher is Dispatcher dispatcher && !dispatcher.CheckAccess())
        {
            _ = dispatcher.BeginInvoke(
                DispatcherPriority.Normal,
                new Action(() => ShowCrashDialog(title, whatHappened, report, diagnosticsService))
            );
            return;
        }

        var owner = Application.Current?.MainWindow;
        var dialog = new CrashDialog(
            title,
            $"{whatHappened}{Environment.NewLine}CorrelationId: {report.CorrelationId}",
            report.LogPath,
            report.DiagnosticsText,
            copyDiagnostics: () => CopyDiagnostics(diagnosticsService, report.DiagnosticsText)
        );

        if (owner is not null && owner != dialog)
        {
            dialog.Owner = owner;
        }

        dialog.ShowDialog();
    }

    private static string ResolveCorrelationId()
    {
        var scoped = AppFileLogger.GetScopeValue("correlationId");
        return string.IsNullOrWhiteSpace(scoped)
            ? AppFileLogger.NewCorrelationId()
            : scoped;
    }

    private static Dictionary<string, object?> BuildStructuredContextSafe(
        string correlationId,
        IAppSessionState? appSessionState
    )
    {
        try
        {
            return UiExceptionReportContextBuilder.Build(correlationId, appSessionState);
        }
        catch (Exception ex)
        {
            AppFileLogger.LogEvent(
                eventName: "ExceptionContextBuildFailed",
                level: "WARN",
                message: "Failed to build structured exception context.",
                ex: ex,
                fields: new Dictionary<string, object?>
                {
                    ["correlationId"] = correlationId
                }
            );
            return new Dictionary<string, object?>
            {
                ["correlationId"] = correlationId
            };
        }
    }

    private static string BuildDiagnosticsTextSafe(
        string context,
        string correlationId,
        Exception? ex,
        IDiagnosticsService? diagnosticsService
    )
    {
        try
        {
            return BuildDiagnosticsText(
                context,
                correlationId,
                ex,
                diagnosticsService
            );
        }
        catch (Exception diagnosticsEx)
        {
            AppFileLogger.LogEvent(
                eventName: "ExceptionDiagnosticsBuildFailed",
                level: "WARN",
                message: "Failed building extended diagnostics text.",
                ex: diagnosticsEx,
                fields: new Dictionary<string, object?>
                {
                    ["correlationId"] = correlationId
                }
            );
            return BuildFallbackDiagnosticsText(context, correlationId, ex);
        }
    }

    private static string BuildDiagnosticsText(
        string context,
        string correlationId,
        Exception? ex,
        IDiagnosticsService? diagnosticsService
    )
    {
        if (diagnosticsService is not null)
        {
            return diagnosticsService.BuildDiagnosticsText(context, correlationId, ex);
        }

        return BuildFallbackDiagnosticsText(context, correlationId, ex);
    }

    private static string BuildFallbackDiagnosticsText(
        string context,
        string correlationId,
        Exception? ex
    )
    {
        var diagnostics = new StringBuilder();
        diagnostics.AppendLine($"Context: {context}");
        diagnostics.AppendLine($"CorrelationId: {correlationId}");
        diagnostics.AppendLine($"CurrentLogPath: {AppFileLogger.GetCurrentLogPath()}");
        diagnostics.AppendLine();
        diagnostics.AppendLine("Exception:");
        diagnostics.AppendLine(ex?.ToString() ?? "(none)");
        diagnostics.AppendLine();
        diagnostics.AppendLine("LastLogLines:");
        var lines = AppFileLogger.ReadLastLogLines(50);
        if (lines.Count == 0)
        {
            diagnostics.AppendLine("(none)");
        }
        else
        {
            foreach (var line in lines)
            {
                diagnostics.AppendLine(line);
            }
        }

        return diagnostics.ToString();
    }

    private static void CopyDiagnostics(IDiagnosticsService? diagnosticsService, string diagnosticsText)
    {
        try
        {
            if (diagnosticsService is not null)
            {
                diagnosticsService.CopyDiagnostics(diagnosticsText);
            }
            else if (!string.IsNullOrWhiteSpace(diagnosticsText))
            {
                Clipboard.SetText(diagnosticsText);
            }
        }
        catch
        {
        }
    }
}

internal sealed record FatalErrorReport(
    string CorrelationId,
    string LogPath,
    string DiagnosticsText
);
