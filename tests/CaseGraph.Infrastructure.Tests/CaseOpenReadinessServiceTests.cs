using CaseGraph.App.Services;

namespace CaseGraph.Infrastructure.Tests;

public sealed class CaseOpenReadinessServiceTests
{
    [Fact]
    public async Task EnsureReadyAsync_RunsCaseOpenReadinessAndReportsProgress()
    {
        var migrationService = new FakeWorkspaceMigrationService(workPerformed: true);
        var service = new CaseOpenReadinessService(migrationService);
        var caseId = Guid.NewGuid();
        var updates = new List<ReadinessProgress>();

        var result = await service.EnsureReadyAsync(
            caseId,
            new ListProgress<ReadinessProgress>(updates),
            CancellationToken.None
        );

        Assert.True(result.WorkPerformed);
        Assert.Equal(1, migrationService.RunCaseOpenReadinessCalls);
        Assert.NotEmpty(updates);
        Assert.All(updates, update => Assert.Equal(ReadinessPhase.CaseOpen, update.Phase));
        Assert.All(updates, update => Assert.Equal(caseId, update.CaseId));
    }

    [Fact]
    public async Task EnsureReadyAsync_WhenAlreadyCurrent_ReturnsNoWorkPerformed()
    {
        var migrationService = new FakeWorkspaceMigrationService(workPerformed: false);
        var service = new CaseOpenReadinessService(migrationService);

        var result = await service.EnsureReadyAsync(
            Guid.NewGuid(),
            progress: null,
            ct: CancellationToken.None
        );

        Assert.False(result.WorkPerformed);
        Assert.Equal("Case readiness already current.", result.Summary);
        Assert.Equal(1, migrationService.RunCaseOpenReadinessCalls);
    }

    private sealed class FakeWorkspaceMigrationService : IWorkspaceMigrationService
    {
        private readonly bool _workPerformed;

        public FakeWorkspaceMigrationService(bool workPerformed)
        {
            _workPerformed = workPerformed;
        }

        public int RunCaseOpenReadinessCalls { get; private set; }

        public Task EnsureMigratedAsync(CancellationToken ct)
        {
            return Task.CompletedTask;
        }

        public Task<bool> RunCaseOpenReadinessAsync(CancellationToken ct)
        {
            RunCaseOpenReadinessCalls++;
            return Task.FromResult(_workPerformed);
        }
    }

    private sealed class ListProgress<T> : IProgress<T>
    {
        private readonly ICollection<T> _items;

        public ListProgress(ICollection<T> items)
        {
            _items = items;
        }

        public void Report(T value)
        {
            _items.Add(value);
        }
    }
}
