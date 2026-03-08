using CaseGraph.Core.Diagnostics;

namespace CaseGraph.App.Services;

public sealed class StartupStageReporter : IStartupStageReporter
{
    private readonly object _sync = new();
    private readonly TimeProvider _timeProvider;
    private readonly DateTimeOffset _startedAtUtc;

    private State _state;

    public StartupStageReporter(TimeProvider? timeProvider = null)
    {
        _timeProvider = timeProvider ?? TimeProvider.System;
        _startedAtUtc = _timeProvider.GetUtcNow();
        _state = new State(
            StartupStageKeys.Starting,
            "Starting CaseGraph",
            "Preparing startup.",
            StartupStageStatus.Running,
            DiagnosticsPath: null
        );
    }

    public StartupStageSnapshot Current
    {
        get
        {
            lock (_sync)
            {
                return CreateSnapshot(_state);
            }
        }
    }

    public event EventHandler<StartupStageSnapshot>? Changed;

    public void ReportStage(string stageKey, string stageText, string? detailText = null)
    {
        Publish(stageKey, stageText, detailText, StartupStageStatus.Running, diagnosticsPath: null);
    }

    public void ReportCompleted(string stageKey, string stageText, string? detailText = null)
    {
        Publish(stageKey, stageText, detailText, StartupStageStatus.Completed, diagnosticsPath: null);
    }

    public void ReportFailure(
        string stageKey,
        string stageText,
        string detailText,
        string? diagnosticsPath = null
    )
    {
        Publish(stageKey, stageText, detailText, StartupStageStatus.Failed, diagnosticsPath);
    }

    private void Publish(
        string stageKey,
        string stageText,
        string? detailText,
        StartupStageStatus status,
        string? diagnosticsPath
    )
    {
        StartupStageSnapshot? snapshot = null;

        lock (_sync)
        {
            var nextState = new State(stageKey, stageText, detailText, status, diagnosticsPath);
            if (_state == nextState)
            {
                return;
            }

            _state = nextState;
            snapshot = CreateSnapshot(nextState);
        }

        Changed?.Invoke(this, snapshot);
    }

    private StartupStageSnapshot CreateSnapshot(State state)
    {
        var elapsed = _timeProvider.GetUtcNow() - _startedAtUtc;
        if (elapsed < TimeSpan.Zero)
        {
            elapsed = TimeSpan.Zero;
        }

        return new StartupStageSnapshot(
            state.StageKey,
            state.StageText,
            state.DetailText,
            elapsed,
            state.Status,
            state.DiagnosticsPath
        );
    }

    private sealed record State(
        string StageKey,
        string StageText,
        string? DetailText,
        StartupStageStatus Status,
        string? DiagnosticsPath
    );
}
