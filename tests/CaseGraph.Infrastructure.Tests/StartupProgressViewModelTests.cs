using CaseGraph.App.Services;
using CaseGraph.App.ViewModels;
using CaseGraph.Core.Diagnostics;

namespace CaseGraph.Infrastructure.Tests;

public sealed class StartupProgressViewModelTests
{
    [Fact]
    public void Constructor_UsesCurrentReporterSnapshot()
    {
        var timeProvider = new ManualTimeProvider();
        var reporter = new StartupStageReporter(timeProvider);

        timeProvider.Advance(TimeSpan.FromSeconds(5));
        reporter.ReportStage(
            StartupStageKeys.BuildingHost,
            "Building host",
            "Loading services and diagnostics."
        );

        using var viewModel = new StartupProgressViewModel(reporter);

        Assert.Equal("Building host", viewModel.StageText);
        Assert.Equal("Loading services and diagnostics.", viewModel.DetailText);
        Assert.Equal("Elapsed 00:05", viewModel.ElapsedText);
        Assert.False(viewModel.IsFailure);
    }

    [Fact]
    public void RefreshElapsed_UsesLatestReporterElapsed()
    {
        var timeProvider = new ManualTimeProvider();
        var reporter = new StartupStageReporter(timeProvider);
        using var viewModel = new StartupProgressViewModel(reporter);

        timeProvider.Advance(TimeSpan.FromSeconds(74));
        viewModel.RefreshElapsed();

        Assert.Equal("Elapsed 01:14", viewModel.ElapsedText);
    }

    [Fact]
    public void FailureState_IncludesDiagnosticsPath()
    {
        var timeProvider = new ManualTimeProvider();
        var reporter = new StartupStageReporter(timeProvider);
        using var viewModel = new StartupProgressViewModel(reporter);

        reporter.ReportFailure(
            StartupStageKeys.StartupFailed,
            "Startup failed",
            "Workspace database migration failed.",
            @"C:\Temp\CaseGraph\Logs"
        );

        Assert.Equal("Startup failed", viewModel.StageText);
        Assert.Contains("Workspace database migration failed.", viewModel.DetailText);
        Assert.Contains(@"C:\Temp\CaseGraph\Logs", viewModel.DetailText);
        Assert.True(viewModel.IsFailure);
    }

    private sealed class ManualTimeProvider : TimeProvider
    {
        private DateTimeOffset _utcNow = new(2026, 3, 8, 12, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow()
        {
            return _utcNow;
        }

        public void Advance(TimeSpan delta)
        {
            _utcNow = _utcNow.Add(delta);
        }
    }
}
