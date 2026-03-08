using CaseGraph.App.Services;
using CaseGraph.App.Views.Pages;
using CaseGraph.App.ViewModels;
using CaseGraph.Core.Abstractions;
using CaseGraph.Core.Models;
using CaseGraph.Infrastructure.Timeline;
using Microsoft.Extensions.DependencyInjection;
using System.Runtime.ExceptionServices;
using System.Windows;

namespace CaseGraph.Infrastructure.Tests;

public sealed class ReportsViewModelStartupTests
{
    [Fact]
    public void Reports_Constructor_DoesNotThrow_DuringDefaultInitialization()
    {
        var exception = Record.Exception(static () => _ = CreateReportsViewModel());

        Assert.Null(exception);
    }

    [Fact]
    public void Reports_DI_Resolution_DoesNotThrow_DuringStartup()
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

    [Fact]
    public void Timeline_Constructor_DoesNotThrow_DuringDefaultInitialization()
    {
        var exception = Record.Exception(static () => _ = CreateTimelineViewModel());

        Assert.Null(exception);
    }

    [Fact]
    public void Timeline_DI_Resolution_DoesNotThrow_DuringStartup()
    {
        var services = new ServiceCollection();
        services.AddSingleton<ITargetRegistryService, FakeTargetRegistryService>();
        services.AddSingleton<IUserInteractionService, FakeUserInteractionService>();
        services.AddSingleton<IWorkspaceDatabaseInitializer, FakeWorkspaceDatabaseInitializer>();
        services.AddSingleton<IWorkspacePathProvider, FakeWorkspacePathProvider>();
        services.AddSingleton<IAuditLogService, FakeAuditLogService>();
        services.AddSingleton<TimelineQueryService>();
        services.AddSingleton<TimelineViewModel>();

        using var provider = services.BuildServiceProvider();

        var exception = Record.Exception(() => provider.GetRequiredService<TimelineViewModel>());

        Assert.Null(exception);
    }

    [Fact]
    public async Task Reports_ActivateAsync_DoesNotThrow_AfterCaseSelection()
    {
        var viewModel = CreateReportsViewModel();
        var caseId = Guid.NewGuid();

        var exception = await Record.ExceptionAsync(async () =>
        {
            await viewModel.SetCurrentCaseAsync(caseId, CancellationToken.None);
            await viewModel.ActivateAsync(CancellationToken.None);
        });

        Assert.Null(exception);
    }

    [Fact]
    public async Task Timeline_ActivateAsync_DoesNotThrow_WithoutCurrentCase()
    {
        var viewModel = CreateTimelineViewModel();

        var exception = await Record.ExceptionAsync(
            () => viewModel.ActivateAsync(CancellationToken.None)
        );

        Assert.Null(exception);
    }

    [Fact]
    public void NavigationService_Creates_Reports_And_Timeline_Views()
    {
        var exception = Record.Exception(static () => RunOnStaThread(() =>
        {
            var app = Application.Current as CaseGraph.App.App;
            if (app is null)
            {
                app = new CaseGraph.App.App();
                app.InitializeComponent();
            }

            var navigationService = new NavigationService();
            var navigationItems = navigationService.GetNavigationItems();

            Assert.Contains(navigationItems, item => item.Page == CaseGraph.App.Models.NavigationPage.Reports);
            Assert.Contains(navigationItems, item => item.Page == CaseGraph.App.Models.NavigationPage.Timeline);
            Assert.IsType<ReportsView>(
                navigationService.CreateView(CaseGraph.App.Models.NavigationPage.Reports)
            );
            Assert.IsType<TimelineView>(
                navigationService.CreateView(CaseGraph.App.Models.NavigationPage.Timeline)
            );
        }));

        Assert.Null(exception);
    }

    private static ReportsViewModel CreateReportsViewModel()
    {
        return new ReportsViewModel(
            new FakeTargetRegistryService(),
            new FakeCaseQueryService(),
            new FakeUserInteractionService(),
            new FakeJobQueueService(),
            new FakeJobQueryService()
        );
    }

    private static TimelineViewModel CreateTimelineViewModel()
    {
        return new TimelineViewModel(
            new TimelineQueryService(
                new FakeWorkspaceDatabaseInitializer(),
                new FakeWorkspacePathProvider(),
                new FakeAuditLogService()
            ),
            new FakeTargetRegistryService(),
            new FakeUserInteractionService()
        );
    }

    private static void RunOnStaThread(Action action)
    {
        Exception? captured = null;

        var thread = new Thread(() =>
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                captured = ex;
            }
        });

        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();

        if (captured is not null)
        {
            ExceptionDispatchInfo.Capture(captured).Throw();
        }
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

    private sealed class FakeWorkspaceDatabaseInitializer : IWorkspaceDatabaseInitializer
    {
        public Task EnsureInitializedAsync(CancellationToken ct) => Task.CompletedTask;
    }

    private sealed class FakeWorkspacePathProvider : IWorkspacePathProvider
    {
        public string WorkspaceRoot => Environment.CurrentDirectory;

        public string WorkspaceDbPath => Path.Combine(Environment.CurrentDirectory, "casegraph-tests.db");

        public string CasesRoot => Environment.CurrentDirectory;
    }

    private sealed class FakeAuditLogService : IAuditLogService
    {
        public Task AddAsync(AuditEvent auditEvent, CancellationToken ct) => Task.CompletedTask;

        public Task<IReadOnlyList<AuditEvent>> GetRecentAsync(Guid? caseId, int take, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<AuditEvent>>([]);
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
