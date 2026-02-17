using CaseGraph.App.ViewModels;
using CaseGraph.Core.Diagnostics;
using System.Windows;

namespace CaseGraph.App.Services;

internal static class UiExceptionReporter
{
    public static string LogException(string context, Exception ex)
    {
        var correlationId = Guid.NewGuid().ToString("N");
        AppFileLogger.LogException(BuildLogContext(context, correlationId), ex);
        return correlationId;
    }

    public static string LogExceptionObject(string context, object? exceptionObject)
    {
        var correlationId = Guid.NewGuid().ToString("N");
        AppFileLogger.Log(
            $"{BuildLogContext(context, correlationId)} ExceptionObject={exceptionObject ?? "(null)"}"
        );
        return correlationId;
    }

    public static void ShowErrorDialog(string title, Exception? ex, string correlationId)
    {
        var details = ex?.ToString() ?? "Unexpected fatal error.";
        var message =
            $"{details}{Environment.NewLine}{Environment.NewLine}Correlation ID: {correlationId}";
        MessageBox.Show(
            message,
            title,
            MessageBoxButton.OK,
            MessageBoxImage.Error
        );
    }

    private static string BuildLogContext(string context, string correlationId)
    {
        var caseId = TryGetActiveCaseId();
        var caseText = caseId?.ToString("D") ?? "(none)";
        return $"{context} CorrelationId={correlationId} CaseId={caseText}";
    }

    private static Guid? TryGetActiveCaseId()
    {
        return Application.Current?.MainWindow?.DataContext is MainWindowViewModel vm
            ? vm.CurrentCaseInfo?.CaseId
            : null;
    }
}
