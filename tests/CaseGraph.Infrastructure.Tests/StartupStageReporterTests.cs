using CaseGraph.App.Services;
using CaseGraph.Core.Diagnostics;

namespace CaseGraph.Infrastructure.Tests;

public sealed class StartupStageReporterTests
{
    [Fact]
    public void Current_ReflectsElapsedTimeFromTimeProvider()
    {
        var timeProvider = new ManualTimeProvider();
        var reporter = new StartupStageReporter(timeProvider);

        timeProvider.Advance(TimeSpan.FromSeconds(12));

        var snapshot = reporter.Current;

        Assert.Equal(StartupStageKeys.Starting, snapshot.StageKey);
        Assert.Equal(TimeSpan.FromSeconds(12), snapshot.Elapsed);
        Assert.Equal(StartupStageStatus.Running, snapshot.Status);
    }

    [Fact]
    public void ReportStage_PublishesUpdatedSnapshot()
    {
        var timeProvider = new ManualTimeProvider();
        var reporter = new StartupStageReporter(timeProvider);
        StartupStageSnapshot? changedSnapshot = null;
        reporter.Changed += (_, snapshot) => changedSnapshot = snapshot;

        timeProvider.Advance(TimeSpan.FromSeconds(7));
        reporter.ReportStage(
            StartupStageKeys.OpeningWorkspace,
            "Opening workspace",
            "Inspecting existing workspace database state."
        );

        Assert.NotNull(changedSnapshot);
        Assert.Equal(StartupStageKeys.OpeningWorkspace, changedSnapshot!.StageKey);
        Assert.Equal("Opening workspace", changedSnapshot.StageText);
        Assert.Equal("Inspecting existing workspace database state.", changedSnapshot.DetailText);
        Assert.Equal(TimeSpan.FromSeconds(7), changedSnapshot.Elapsed);
        Assert.Equal(StartupStageStatus.Running, changedSnapshot.Status);
    }

    [Fact]
    public void ReportFailure_CapturesFailureStateAndDiagnosticsPath()
    {
        var timeProvider = new ManualTimeProvider();
        var reporter = new StartupStageReporter(timeProvider);

        timeProvider.Advance(TimeSpan.FromSeconds(33));
        reporter.ReportFailure(
            StartupStageKeys.StartupFailed,
            "Startup failed",
            "Workspace database migration failed.",
            @"C:\Temp\CaseGraph\Logs"
        );

        var snapshot = reporter.Current;
        Assert.Equal(StartupStageKeys.StartupFailed, snapshot.StageKey);
        Assert.Equal(StartupStageStatus.Failed, snapshot.Status);
        Assert.Equal("Workspace database migration failed.", snapshot.DetailText);
        Assert.Equal(@"C:\Temp\CaseGraph\Logs", snapshot.DiagnosticsPath);
        Assert.Equal(TimeSpan.FromSeconds(33), snapshot.Elapsed);
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
