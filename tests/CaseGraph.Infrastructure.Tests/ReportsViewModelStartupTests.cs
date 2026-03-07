using CaseGraph.App.Services;
using CaseGraph.App.ViewModels;
using CaseGraph.Core.Abstractions;
using CaseGraph.Core.Models;
using Microsoft.Extensions.DependencyInjection;

namespace CaseGraph.Infrastructure.Tests;

public sealed class ReportsViewModelStartupTests
{
    [Fact]
    public void Constructor_DoesNotThrow_DuringDefaultInitialization()
    {
        var exception = Record.Exception(static () => _ = CreateViewModel());

        Assert.Null(exception);
    }

    [Fact]
    public void DI_Resolution_DoesNotThrow_DuringStartup()
    {
        var services = new ServiceCollection();
        services.AddSingleton<ITargetRegistryService, FakeTargetRegistryService>();
        services.AddSingleton<ICaseQueryService, FakeCaseQueryService>();
        services.AddSingleton<IUserInteractionService, FakeUserInteractionService>();
        services.AddSingleton<IJobQueueService, FakeJobQueueService>();
        services.AddSingleton<IJobQueryService, FakeJobQueryService>();
        services.AddSingleton<ReportsViewModel>();

        using var provider = services.BuildServiceProvider();

        var exception = Record.Exception(() => provider.GetRequiredService<ReportsViewModel>());

        Assert.Null(exception);
    }

    private static ReportsViewModel CreateViewModel()
    {
        return new ReportsViewModel(
            new FakeTargetRegistryService(),
            new FakeCaseQueryService(),
            new FakeUserInteractionService(),
            new FakeJobQueueService(),
            new FakeJobQueryService()
        );
    }

    private sealed class FakeTargetRegistryService : ITargetRegistryService
    {
        public Task<IReadOnlyList<TargetSummary>> GetTargetsAsync(Guid caseId, string? search, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<TargetSummary>>([]);

        public Task<TargetDetails?> GetTargetDetailsAsync(Guid caseId, Guid targetId, CancellationToken ct)
            => Task.FromResult<TargetDetails?>(null);

        public Task<IReadOnlyList<GlobalPersonSummary>> SearchGlobalPersonsAsync(string? search, int take, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<GlobalPersonSummary>>([]);

        public Task<TargetSummary> CreateTargetAsync(CreateTargetRequest request, CancellationToken ct)
            => throw new NotSupportedException();

        public Task<TargetSummary> UpdateTargetAsync(UpdateTargetRequest request, CancellationToken ct)
            => throw new NotSupportedException();

        public Task<TargetGlobalPersonInfo> CreateAndLinkGlobalPersonAsync(CreateGlobalPersonForTargetRequest request, CancellationToken ct)
            => throw new NotSupportedException();

        public Task<TargetGlobalPersonInfo> LinkTargetToGlobalPersonAsync(LinkTargetToGlobalPersonRequest request, CancellationToken ct)
            => throw new NotSupportedException();

        public Task UnlinkTargetFromGlobalPersonAsync(Guid caseId, Guid targetId, CancellationToken ct)
            => throw new NotSupportedException();

        public Task<TargetAliasInfo> AddAliasAsync(AddTargetAliasRequest request, CancellationToken ct)
            => throw new NotSupportedException();

        public Task RemoveAliasAsync(Guid caseId, Guid aliasId, CancellationToken ct)
            => throw new NotSupportedException();

        public Task<TargetIdentifierMutationResult> AddIdentifierAsync(AddTargetIdentifierRequest request, CancellationToken ct)
            => throw new NotSupportedException();

        public Task<TargetIdentifierMutationResult> UpdateIdentifierAsync(UpdateTargetIdentifierRequest request, CancellationToken ct)
            => throw new NotSupportedException();

        public Task RemoveIdentifierAsync(RemoveTargetIdentifierRequest request, CancellationToken ct)
            => throw new NotSupportedException();

        public Task<MessageParticipantLinkResult> LinkMessageParticipantAsync(LinkMessageParticipantRequest request, CancellationToken ct)
            => throw new NotSupportedException();
    }

    private sealed class FakeCaseQueryService : ICaseQueryService
    {
        public Task<IReadOnlyList<CaseInfo>> GetRecentCasesAsync(CancellationToken ct)
            => Task.FromResult<IReadOnlyList<CaseInfo>>([]);

        public Task<CaseInfo?> GetCaseAsync(Guid caseId, CancellationToken ct)
            => Task.FromResult<CaseInfo?>(null);

        public Task<IReadOnlyList<EvidenceItem>> GetEvidenceForCaseAsync(Guid caseId, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<EvidenceItem>>([]);
    }

    private sealed class FakeUserInteractionService : IUserInteractionService
    {
        public string? PromptForCaseName() => null;

        public IReadOnlyList<string> PickEvidenceFiles() => [];

        public string? PickDebugBundleOutputPath(string defaultFileName) => null;

        public string? PickReportOutputPath(string defaultFileName) => null;

        public void CopyToClipboard(string value)
        {
        }
    }

    private sealed class FakeJobQueueService : IJobQueueService
    {
        public IObservable<JobInfo> JobUpdates { get; } = new EmptyJobObservable();

        public Task<Guid> EnqueueAsync(JobEnqueueRequest request, CancellationToken ct)
            => throw new NotSupportedException();

        public Task CancelAsync(Guid jobId, CancellationToken ct)
            => throw new NotSupportedException();

        public Task<IReadOnlyList<JobInfo>> GetRecentAsync(Guid? caseId, int take, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<JobInfo>>([]);
    }

    private sealed class FakeJobQueryService : IJobQueryService
    {
        public Task<JobInfo?> GetLatestJobForEvidenceAsync(Guid caseId, Guid evidenceItemId, string jobType, CancellationToken ct)
            => Task.FromResult<JobInfo?>(null);

        public Task<IReadOnlyList<JobInfo>> GetRecentJobsAsync(Guid? caseId, int take, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<JobInfo>>([]);
    }

    private sealed class EmptyJobObservable : IObservable<JobInfo>
    {
        public IDisposable Subscribe(IObserver<JobInfo> observer) => EmptySubscription.Instance;
    }

    private sealed class EmptySubscription : IDisposable
    {
        public static EmptySubscription Instance { get; } = new();

        public void Dispose()
        {
        }
    }
}
