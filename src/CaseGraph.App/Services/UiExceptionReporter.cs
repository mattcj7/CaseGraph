using CaseGraph.App.ViewModels;
using CaseGraph.App.Views.Dialogs;
using CaseGraph.Core.Diagnostics;
using System.Text;
using System.Windows;

namespace CaseGraph.App.Services;

internal static class UiExceptionReporter
{
    public static FatalErrorReport LogFatalException(
        string context,
        Exception ex,
        IDiagnosticsService? diagnosticsService = null
    )
    {
        var correlationId = Guid.NewGuid().ToString("N");
        AppFileLogger.LogFatal(BuildLogContext(context, correlationId), ex);
        var diagnosticsText = BuildDiagnosticsText(
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
        IDiagnosticsService? diagnosticsService = null
    )
    {
        var correlationId = Guid.NewGuid().ToString("N");
        var details =
            $"{BuildLogContext(context, correlationId)} ExceptionObject={exceptionObject ?? "(null)"}";
        AppFileLogger.Log($"[FATAL] {details}");
        var diagnosticsText = BuildDiagnosticsText(
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

    private static string BuildLogContext(string context, string correlationId)
    {
        var caseId = TryGetActiveCaseId();
        var caseText = caseId?.ToString("D") ?? "(none)";
        var view = TryGetActiveView() ?? "(none)";
        var action = TryGetActiveAction() ?? "(none)";
        return $"{context} CorrelationId={correlationId} CaseId={caseText} View={view} Action={action}";
    }

    private static Guid? TryGetActiveCaseId()
    {
        return Application.Current?.MainWindow?.DataContext is MainWindowViewModel vm
            ? vm.CurrentCaseInfo?.CaseId
            : null;
    }

    private static string? TryGetActiveView()
    {
        return Application.Current?.MainWindow?.DataContext is MainWindowViewModel vm
            ? vm.SelectedNavigationItem?.Title
            : null;
    }

    private static string? TryGetActiveAction()
    {
        return Application.Current?.MainWindow?.DataContext is MainWindowViewModel vm
            ? vm.OperationText
            : null;
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
