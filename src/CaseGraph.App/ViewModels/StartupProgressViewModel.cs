using CaseGraph.Core.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;

namespace CaseGraph.App.ViewModels;

public sealed partial class StartupProgressViewModel : ObservableObject, IDisposable
{
    private readonly IStartupStageReporter _startupStageReporter;

    [ObservableProperty]
    private string stageText = "Starting CaseGraph";

    [ObservableProperty]
    private string detailText = "Preparing startup.";

    [ObservableProperty]
    private string elapsedText = "Elapsed 00:00";

    [ObservableProperty]
    private bool isFailure;

    public StartupProgressViewModel(IStartupStageReporter startupStageReporter)
    {
        _startupStageReporter = startupStageReporter;
        _startupStageReporter.Changed += OnStartupStageChanged;
        ApplySnapshot(_startupStageReporter.Current);
    }

    public void RefreshElapsed()
    {
        ElapsedText = FormatElapsed(_startupStageReporter.Current.Elapsed);
    }

    public void Dispose()
    {
        _startupStageReporter.Changed -= OnStartupStageChanged;
    }

    private void OnStartupStageChanged(object? sender, StartupStageSnapshot snapshot)
    {
        ApplySnapshot(snapshot);
    }

    private void ApplySnapshot(StartupStageSnapshot snapshot)
    {
        StageText = snapshot.StageText;
        DetailText = BuildDetailText(snapshot);
        IsFailure = snapshot.Status == StartupStageStatus.Failed;
        ElapsedText = FormatElapsed(snapshot.Elapsed);
    }

    private static string BuildDetailText(StartupStageSnapshot snapshot)
    {
        if (
            snapshot.Status == StartupStageStatus.Failed
            && !string.IsNullOrWhiteSpace(snapshot.DiagnosticsPath)
        )
        {
            return $"{snapshot.DetailText}{Environment.NewLine}Logs: {snapshot.DiagnosticsPath}";
        }

        return snapshot.DetailText ?? string.Empty;
    }

    private static string FormatElapsed(TimeSpan elapsed)
    {
        var totalMinutes = (int)elapsed.TotalMinutes;
        return $"Elapsed {totalMinutes:00}:{elapsed.Seconds:00}";
    }
}
