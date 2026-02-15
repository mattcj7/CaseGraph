using CaseGraph.App.Models;
using CaseGraph.App.Services;
using CaseGraph.Core.Abstractions;
using CaseGraph.Core.Models;
using CaseGraph.Infrastructure.Persistence;
using CaseGraph.Infrastructure.Persistence.Entities;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.EntityFrameworkCore;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Windows;

namespace CaseGraph.App.ViewModels;

public partial class MainWindowViewModel : ObservableObject, IDisposable
{
    private const string EvidenceImportJobType = "EvidenceImport";
    private const string EvidenceVerifyJobType = "EvidenceVerify";
    private const string MessagesIngestJobType = "MessagesIngest";

    private readonly INavigationService _navigationService;
    private readonly IThemeService _themeService;
    private readonly ICaseWorkspaceService _caseWorkspaceService;
    private readonly IEvidenceVaultService _evidenceVaultService;
    private readonly IMessageSearchService _messageSearchService;
    private readonly IAuditLogService _auditLogService;
    private readonly IJobQueueService _jobQueueService;
    private readonly IDbContextFactory<WorkspaceDbContext> _dbContextFactory;
    private readonly IWorkspacePathProvider _workspacePathProvider;
    private readonly IUserInteractionService _userInteractionService;
    private readonly IDisposable _jobUpdateSubscription;
    private readonly SemaphoreSlim _jobCompletionRefreshGate = new(1, 1);

    private CancellationTokenSource? _operationCts;
    private CancellationTokenSource? _messageSearchCts;
    private CancellationTokenSource? _latestMessagesParseRefreshCts;
    private Guid? _activeJobId;
    private bool _isInitialized;
    private bool _isDisposed;

    public ObservableCollection<NavigationItem> NavigationItems { get; } = new();

    public ObservableCollection<CaseInfo> AvailableCases { get; } = new();

    public ObservableCollection<EvidenceItem> EvidenceItems { get; } = new();

    public ObservableCollection<AuditEvent> RecentAuditEvents { get; } = new();

    public ObservableCollection<JobInfo> RecentJobs { get; } = new();

    public ObservableCollection<MessageSearchHit> MessageSearchResults { get; } = new();

    public IReadOnlyList<string> MessageSearchPlatformFilters { get; } =
    [
        "All",
        "SMS",
        "iMessage",
        "WhatsApp",
        "Signal",
        "Instagram",
        "OTHER"
    ];

    [ObservableProperty]
    private NavigationItem? selectedNavigationItem;

    [ObservableProperty]
    private object? currentView;

    [ObservableProperty]
    private CaseInfo? selectedCase;

    [ObservableProperty]
    private CaseInfo? currentCaseInfo;

    [ObservableProperty]
    private string globalSearchQuery = string.Empty;

    [ObservableProperty]
    private bool isDarkTheme;

    [ObservableProperty]
    private bool isEvidenceDrawerOpen;

    [ObservableProperty]
    private EvidenceItem? selectedEvidenceItem;

    [ObservableProperty]
    private JobInfo? selectedJobHistoryItem;

    [ObservableProperty]
    private string operationText = "Ready.";

    [ObservableProperty]
    private double operationProgress;

    [ObservableProperty]
    private bool isOperationInProgress;

    [ObservableProperty]
    private string selectedEvidenceVerifyStatus = "No verification job yet.";

    [ObservableProperty]
    private string selectedEvidenceMessagesStatus = "No messages parse job yet.";

    [ObservableProperty]
    private JobInfo? latestMessagesParseJob;

    [ObservableProperty]
    private string messageSearchQuery = string.Empty;

    [ObservableProperty]
    private string selectedMessageSearchPlatform = "All";

    [ObservableProperty]
    private string messageSearchSenderFilter = string.Empty;

    [ObservableProperty]
    private string messageSearchRecipientFilter = string.Empty;

    [ObservableProperty]
    private MessageSearchHit? selectedMessageSearchResult;

    [ObservableProperty]
    private string messageSearchStatusText = "Enter a query to search messages.";

    [ObservableProperty]
    private bool isMessageSearchInProgress;

    public bool HasSelectedEvidenceItem => SelectedEvidenceItem is not null;

    public string CurrentCaseSummary => CurrentCaseInfo is null
        ? "No case is currently open."
        : $"Current case: {CurrentCaseInfo.Name} ({EvidenceItems.Count} evidence item(s))";

    public string SelectedStoredAbsolutePath => SelectedEvidenceItem is null
        ? string.Empty
        : BuildStoredAbsolutePath(SelectedEvidenceItem);

    public string OperationProgressPercentText
    {
        get
        {
            var percent = Math.Clamp(
                (int)Math.Round(OperationProgress * 100, MidpointRounding.AwayFromZero),
                0,
                100
            );
            return $"{percent:0}%";
        }
    }

    public bool CanCancelLatestMessagesParseJob => LatestMessagesParseJob is not null
        && !IsTerminalStatus(LatestMessagesParseJob.Status);

    public IRelayCommand ToggleThemeCommand { get; }

    public IRelayCommand ToggleEvidenceDrawerCommand { get; }

    public IAsyncRelayCommand CreateNewCaseCommand { get; }

    public IAsyncRelayCommand OpenSelectedCaseCommand { get; }

    public IAsyncRelayCommand RefreshCasesCommand { get; }

    public IAsyncRelayCommand ImportFilesCommand { get; }

    public IAsyncRelayCommand VerifySelectedEvidenceCommand { get; }

    public IAsyncRelayCommand ParseMessagesFromEvidenceCommand { get; }

    public IAsyncRelayCommand RefreshLatestMessagesParseJobCommand { get; }

    public IAsyncRelayCommand CancelLatestMessagesParseJobCommand { get; }

    public IAsyncRelayCommand SearchMessagesCommand { get; }

    public IRelayCommand CopyStoredPathCommand { get; }

    public IRelayCommand CopySha256Command { get; }

    public IRelayCommand CopyMessageCitationCommand { get; }

    public IRelayCommand CopyMessageStoredPathCommand { get; }

    public IAsyncRelayCommand CancelOperationCommand { get; }

    public MainWindowViewModel(
        INavigationService navigationService,
        IThemeService themeService,
        ICaseWorkspaceService caseWorkspaceService,
        IEvidenceVaultService evidenceVaultService,
        IMessageSearchService messageSearchService,
        IAuditLogService auditLogService,
        IJobQueueService jobQueueService,
        IDbContextFactory<WorkspaceDbContext> dbContextFactory,
        IWorkspacePathProvider workspacePathProvider,
        IUserInteractionService userInteractionService
    )
    {
        _navigationService = navigationService;
        _themeService = themeService;
        _caseWorkspaceService = caseWorkspaceService;
        _evidenceVaultService = evidenceVaultService;
        _messageSearchService = messageSearchService;
        _auditLogService = auditLogService;
        _jobQueueService = jobQueueService;
        _dbContextFactory = dbContextFactory;
        _workspacePathProvider = workspacePathProvider;
        _userInteractionService = userInteractionService;

        foreach (var item in _navigationService.GetNavigationItems())
        {
            NavigationItems.Add(item);
        }

        IsDarkTheme = _themeService.IsDarkTheme;
        IsEvidenceDrawerOpen = false;

        EvidenceItems.CollectionChanged += OnEvidenceItemsCollectionChanged;

        ToggleThemeCommand = new RelayCommand(() => IsDarkTheme = !IsDarkTheme);
        ToggleEvidenceDrawerCommand = new RelayCommand(() => IsEvidenceDrawerOpen = !IsEvidenceDrawerOpen);
        CreateNewCaseCommand = new AsyncRelayCommand(CreateNewCaseAsync);
        OpenSelectedCaseCommand = new AsyncRelayCommand(OpenSelectedCaseAsync);
        RefreshCasesCommand = new AsyncRelayCommand(() => RefreshCasesAsync(CancellationToken.None));
        ImportFilesCommand = new AsyncRelayCommand(ImportFilesAsync);
        VerifySelectedEvidenceCommand = new AsyncRelayCommand(VerifySelectedEvidenceAsync);
        ParseMessagesFromEvidenceCommand = new AsyncRelayCommand(ParseMessagesFromSelectedEvidenceAsync);
        RefreshLatestMessagesParseJobCommand = new AsyncRelayCommand(RefreshLatestMessagesParseJobManuallyAsync);
        CancelLatestMessagesParseJobCommand = new AsyncRelayCommand(CancelLatestMessagesParseJobAsync);
        SearchMessagesCommand = new AsyncRelayCommand(SearchMessagesAsync);
        CopyStoredPathCommand = new RelayCommand(CopyStoredPath);
        CopySha256Command = new RelayCommand(CopySha256);
        CopyMessageCitationCommand = new RelayCommand(CopyMessageCitation);
        CopyMessageStoredPathCommand = new RelayCommand(CopyMessageStoredPath);
        CancelOperationCommand = new AsyncRelayCommand(CancelCurrentOperationAsync);

        _jobUpdateSubscription = _jobQueueService.JobUpdates.Subscribe(new JobObserver(OnJobUpdated));

        SelectedNavigationItem = NavigationItems.FirstOrDefault();
    }

    public async Task InitializeAsync()
    {
        if (_isInitialized)
        {
            return;
        }

        _isInitialized = true;
        await RefreshCasesAsync(CancellationToken.None);
    }

    partial void OnSelectedNavigationItemChanged(NavigationItem? value)
    {
        if (value is null)
        {
            return;
        }

        CurrentView = _navigationService.CreateView(value.Page);
    }

    partial void OnSelectedEvidenceItemChanged(EvidenceItem? value)
    {
        OnPropertyChanged(nameof(HasSelectedEvidenceItem));
        OnPropertyChanged(nameof(SelectedStoredAbsolutePath));
        UpdateSelectedEvidenceVerifyStatus();
        ResetLatestMessagesParseJobTracking();

        if (value is not null)
        {
            IsEvidenceDrawerOpen = true;
        }
    }

    partial void OnCurrentCaseInfoChanged(CaseInfo? value)
    {
        OnPropertyChanged(nameof(CurrentCaseSummary));
        OnPropertyChanged(nameof(SelectedStoredAbsolutePath));
        UpdateSelectedEvidenceVerifyStatus();
        ResetLatestMessagesParseJobTracking();
    }

    partial void OnLatestMessagesParseJobChanged(JobInfo? value)
    {
        OnPropertyChanged(nameof(CanCancelLatestMessagesParseJob));
        UpdateSelectedEvidenceMessagesStatus();
    }

    partial void OnIsDarkThemeChanged(bool value)
    {
        _themeService.SetTheme(value);
    }

    private async Task CreateNewCaseAsync()
    {
        var caseName = _userInteractionService.PromptForCaseName();
        if (string.IsNullOrWhiteSpace(caseName))
        {
            return;
        }

        using var operation = BeginOperation($"Creating case \"{caseName}\"...");

        try
        {
            var createdCase = await _caseWorkspaceService.CreateCaseAsync(caseName, operation.Token);
            await RefreshCasesAsync(operation.Token);

            SelectedCase = AvailableCases.FirstOrDefault(c => c.CaseId == createdCase.CaseId) ?? createdCase;
            await OpenCaseInternalAsync(createdCase.CaseId, operation.Token);

            OperationProgress = 1.0;
            OperationText = $"Case \"{createdCase.Name}\" created.";
        }
        catch (OperationCanceledException)
        {
            OperationText = "Case creation canceled.";
        }
    }

    private async Task OpenSelectedCaseAsync()
    {
        if (SelectedCase is null)
        {
            OperationText = "Select a case to open.";
            return;
        }

        using var operation = BeginOperation($"Opening case \"{SelectedCase.Name}\"...");

        try
        {
            await OpenCaseInternalAsync(SelectedCase.CaseId, operation.Token);
            OperationProgress = 1.0;
            OperationText = $"Opened case \"{CurrentCaseInfo?.Name}\".";
        }
        catch (OperationCanceledException)
        {
            OperationText = "Open case canceled.";
        }
    }

    private async Task RefreshCasesAsync(CancellationToken ct)
    {
        var selectedCaseId = SelectedCase?.CaseId;
        var currentCaseId = CurrentCaseInfo?.CaseId;
        var cases = await _caseWorkspaceService.ListCasesAsync(ct);

        AvailableCases.Clear();
        foreach (var caseInfo in cases)
        {
            AvailableCases.Add(caseInfo);
        }

        SelectedCase = cases.FirstOrDefault(c => c.CaseId == selectedCaseId)
            ?? cases.FirstOrDefault(c => c.CaseId == currentCaseId)
            ?? cases.FirstOrDefault();
    }

    private async Task ImportFilesAsync()
    {
        if (CurrentCaseInfo is null)
        {
            OperationText = "Open a case before importing evidence.";
            return;
        }

        var files = _userInteractionService.PickEvidenceFiles();
        if (files.Count == 0)
        {
            return;
        }

        try
        {
            var payload = JsonSerializer.Serialize(new EvidenceImportPayload
            {
                SchemaVersion = 1,
                CaseId = CurrentCaseInfo.CaseId,
                Files = files.ToList()
            });

            var jobId = await _jobQueueService.EnqueueAsync(
                new JobEnqueueRequest(
                    EvidenceImportJobType,
                    CurrentCaseInfo.CaseId,
                    EvidenceItemId: null,
                    payload
                ),
                CancellationToken.None
            );

            OperationText = $"Queued import job {jobId:D}.";
            OperationProgress = 0;
            await RefreshRecentJobsAsync(CancellationToken.None);
        }
        catch (OperationCanceledException)
        {
            OperationText = "Import canceled.";
        }
    }

    private async Task VerifySelectedEvidenceAsync()
    {
        if (CurrentCaseInfo is null || SelectedEvidenceItem is null)
        {
            OperationText = "Select an evidence item to verify.";
            return;
        }

        try
        {
            var payload = JsonSerializer.Serialize(new EvidenceVerifyPayload
            {
                SchemaVersion = 1,
                CaseId = CurrentCaseInfo.CaseId,
                EvidenceItemId = SelectedEvidenceItem.EvidenceItemId
            });

            var jobId = await _jobQueueService.EnqueueAsync(
                new JobEnqueueRequest(
                    EvidenceVerifyJobType,
                    CurrentCaseInfo.CaseId,
                    SelectedEvidenceItem.EvidenceItemId,
                    payload
                ),
                CancellationToken.None
            );

            OperationText = $"Queued verify job {jobId:D}.";
            OperationProgress = 0;
            await RefreshRecentJobsAsync(CancellationToken.None);
        }
        catch (OperationCanceledException)
        {
            OperationText = "Verify canceled.";
        }
    }

    private async Task ParseMessagesFromSelectedEvidenceAsync()
    {
        if (CurrentCaseInfo is null || SelectedEvidenceItem is null)
        {
            OperationText = "Select an evidence item before parsing messages.";
            return;
        }

        try
        {
            var payload = JsonSerializer.Serialize(new MessagesIngestPayload
            {
                SchemaVersion = 1,
                CaseId = CurrentCaseInfo.CaseId,
                EvidenceItemId = SelectedEvidenceItem.EvidenceItemId
            });

            var jobId = await _jobQueueService.EnqueueAsync(
                new JobEnqueueRequest(
                    MessagesIngestJobType,
                    CurrentCaseInfo.CaseId,
                    SelectedEvidenceItem.EvidenceItemId,
                    payload
                ),
                CancellationToken.None
            );

            OperationText = $"Queued messages ingest job {jobId:D}.";
            OperationProgress = 0;
            await RefreshRecentJobsAsync(CancellationToken.None);
            await RefreshLatestMessagesParseJobAsync(CancellationToken.None);
        }
        catch (OperationCanceledException)
        {
            OperationText = "Messages ingest canceled.";
        }
    }

    private async Task RefreshRecentActivityAsync(CancellationToken ct)
    {
        if (CurrentCaseInfo is null)
        {
            RecentAuditEvents.Clear();
            return;
        }

        var events = await _auditLogService.GetRecentAsync(CurrentCaseInfo.CaseId, 20, ct);
        RecentAuditEvents.Clear();
        foreach (var auditEvent in events.OrderByDescending(e => e.TimestampUtc))
        {
            RecentAuditEvents.Add(auditEvent);
        }
    }

    private void CopyStoredPath()
    {
        if (SelectedEvidenceItem is null)
        {
            return;
        }

        _userInteractionService.CopyToClipboard(SelectedStoredAbsolutePath);
        OperationText = "Stored path copied to clipboard.";
    }

    private void CopySha256()
    {
        if (SelectedEvidenceItem is null)
        {
            return;
        }

        _userInteractionService.CopyToClipboard(SelectedEvidenceItem.Sha256Hex);
        OperationText = "SHA-256 copied to clipboard.";
    }

    private async Task SearchMessagesAsync()
    {
        if (CurrentCaseInfo is null)
        {
            MessageSearchStatusText = "Open a case before searching messages.";
            MessageSearchResults.Clear();
            SelectedMessageSearchResult = null;
            return;
        }

        var query = MessageSearchQuery?.Trim() ?? string.Empty;
        if (query.Length == 0)
        {
            MessageSearchStatusText = "Enter a query to search messages.";
            MessageSearchResults.Clear();
            SelectedMessageSearchResult = null;
            return;
        }

        var searchCts = BeginMessageSearch();
        IsMessageSearchInProgress = true;
        MessageSearchStatusText = "Searching messages...";

        try
        {
            var hits = await _messageSearchService.SearchAsync(
                CurrentCaseInfo.CaseId,
                query,
                string.Equals(SelectedMessageSearchPlatform, "All", StringComparison.OrdinalIgnoreCase)
                    ? null
                    : SelectedMessageSearchPlatform,
                NullIfWhiteSpace(MessageSearchSenderFilter),
                NullIfWhiteSpace(MessageSearchRecipientFilter),
                take: 250,
                skip: 0,
                searchCts.Token
            );

            if (!ReferenceEquals(_messageSearchCts, searchCts))
            {
                return;
            }

            var ordered = hits
                .OrderByDescending(hit => hit.TimestampUtc ?? DateTimeOffset.MinValue)
                .ToList();

            MessageSearchResults.Clear();
            foreach (var hit in ordered)
            {
                MessageSearchResults.Add(hit);
            }

            SelectedMessageSearchResult = MessageSearchResults.FirstOrDefault();
            MessageSearchStatusText = ordered.Count == 0
                ? "No message hits."
                : $"Found {ordered.Count} message hit(s).";
        }
        catch (OperationCanceledException) when (searchCts.IsCancellationRequested)
        {
            if (!ReferenceEquals(_messageSearchCts, searchCts))
            {
                return;
            }

            MessageSearchStatusText = "Search canceled.";
        }
        finally
        {
            EndMessageSearch(searchCts);
        }
    }

    private void CopyMessageCitation()
    {
        if (SelectedMessageSearchResult is null)
        {
            return;
        }

        var citation = $"{SelectedMessageSearchResult.EvidenceItemId:D} | {SelectedMessageSearchResult.SourceLocator} | {SelectedMessageSearchResult.MessageEventId:D}";
        _userInteractionService.CopyToClipboard(citation);
        MessageSearchStatusText = "Citation copied.";
    }

    private void CopyMessageStoredPath()
    {
        if (SelectedMessageSearchResult is null || CurrentCaseInfo is null)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(SelectedMessageSearchResult.StoredRelativePath))
        {
            MessageSearchStatusText = "Stored path not available.";
            return;
        }

        var absolutePath = Path.Combine(
            _workspacePathProvider.CasesRoot,
            CurrentCaseInfo.CaseId.ToString("D"),
            SelectedMessageSearchResult.StoredRelativePath.Replace('/', Path.DirectorySeparatorChar)
        );

        _userInteractionService.CopyToClipboard(absolutePath);
        MessageSearchStatusText = "Stored path copied.";
    }

    private async Task CancelCurrentOperationAsync()
    {
        if (_activeJobId.HasValue)
        {
            await _jobQueueService.CancelAsync(_activeJobId.Value, CancellationToken.None);
            return;
        }

        _operationCts?.Cancel();
    }

    private async Task OpenCaseInternalAsync(Guid caseId, CancellationToken ct)
    {
        var openedCase = await _caseWorkspaceService.OpenCaseAsync(caseId, ct);
        var loadedCase = await _caseWorkspaceService.LoadCaseAsync(caseId, ct);

        CurrentCaseInfo = openedCase;
        SetEvidenceItems(loadedCase.evidence);
        SelectedEvidenceItem = EvidenceItems.FirstOrDefault();
        IsEvidenceDrawerOpen = SelectedEvidenceItem is not null;

        await RefreshCasesAsync(ct);
        await RefreshRecentActivityAsync(ct);
        await RefreshRecentJobsAsync(ct);
        MessageSearchResults.Clear();
        SelectedMessageSearchResult = null;
        MessageSearchStatusText = "Enter a query to search messages.";
        SelectedCase = AvailableCases.FirstOrDefault(c => c.CaseId == openedCase.CaseId) ?? openedCase;
    }

    private async Task RefreshCurrentCaseAsync(CancellationToken ct)
    {
        if (CurrentCaseInfo is null)
        {
            return;
        }

        var loadedCase = await _caseWorkspaceService.LoadCaseAsync(CurrentCaseInfo.CaseId, ct);
        CurrentCaseInfo = loadedCase.caseInfo;
        SetEvidenceItems(loadedCase.evidence);
    }

    private void SetEvidenceItems(IEnumerable<EvidenceItem> evidence)
    {
        var selectedId = SelectedEvidenceItem?.EvidenceItemId;

        EvidenceItems.Clear();
        foreach (var evidenceItem in evidence.OrderByDescending(e => e.AddedAtUtc))
        {
            EvidenceItems.Add(evidenceItem);
        }

        SelectedEvidenceItem = selectedId is null
            ? EvidenceItems.FirstOrDefault()
            : EvidenceItems.FirstOrDefault(e => e.EvidenceItemId == selectedId.Value)
                ?? EvidenceItems.FirstOrDefault();
    }

    private async Task RefreshRecentJobsAsync(CancellationToken ct)
    {
        if (CurrentCaseInfo is null)
        {
            RecentJobs.Clear();
            SelectedJobHistoryItem = null;
            _activeJobId = null;
            return;
        }

        var jobs = await _jobQueueService.GetRecentAsync(CurrentCaseInfo.CaseId, 50, ct);
        SetRecentJobs(jobs.OrderByDescending(job => job.CreatedAtUtc));
        UpdateSelectedEvidenceVerifyStatus();
        await RefreshLatestMessagesParseJobAsync(ct);
        SyncStatusBarFromJobs();
    }

    private void SetRecentJobs(IEnumerable<JobInfo> jobs)
    {
        var selectedId = SelectedJobHistoryItem?.JobId;

        RecentJobs.Clear();
        foreach (var job in jobs)
        {
            RecentJobs.Add(job);
        }

        SelectedJobHistoryItem = selectedId is null
            ? RecentJobs.FirstOrDefault()
            : RecentJobs.FirstOrDefault(job => job.JobId == selectedId.Value)
                ?? RecentJobs.FirstOrDefault();
    }

    private void OnJobUpdated(JobInfo job)
    {
        if (Application.Current?.Dispatcher is null)
        {
            return;
        }

        _ = Application.Current.Dispatcher.InvokeAsync(() => ApplyJobUpdate(job));
    }

    private void ApplyJobUpdate(JobInfo job)
    {
        if (CurrentCaseInfo is null || job.CaseId != CurrentCaseInfo.CaseId)
        {
            return;
        }

        var existing = RecentJobs.FirstOrDefault(item => item.JobId == job.JobId);
        if (existing is null)
        {
            RecentJobs.Add(job);
        }
        else
        {
            var index = RecentJobs.IndexOf(existing);
            if (index >= 0)
            {
                RecentJobs[index] = job;
            }
        }

        var sorted = RecentJobs
            .OrderByDescending(item => item.CreatedAtUtc)
            .Take(50)
            .ToList();
        SetRecentJobs(sorted);

        SyncStatusBarFromJobs(job);
        UpdateSelectedEvidenceVerifyStatus();
        ApplyLatestMessagesParseJobUpdate(job);

        if (IsTerminalStatus(job.Status))
        {
            _ = RefreshAfterJobCompletionAsync(job);
        }
    }

    private async Task RefreshAfterJobCompletionAsync(JobInfo job)
    {
        if (CurrentCaseInfo is null || job.CaseId != CurrentCaseInfo.CaseId)
        {
            return;
        }

        await _jobCompletionRefreshGate.WaitAsync();
        try
        {
            if (CurrentCaseInfo is null || job.CaseId != CurrentCaseInfo.CaseId)
            {
                return;
            }

            if (job.JobType == EvidenceImportJobType && job.Status == JobStatus.Succeeded)
            {
                await RefreshCurrentCaseAsync(CancellationToken.None);
            }
            else if (job.JobType == MessagesIngestJobType)
            {
                await RefreshCurrentCaseAsync(CancellationToken.None);
            }

            await RefreshRecentActivityAsync(CancellationToken.None);
            await RefreshRecentJobsAsync(CancellationToken.None);
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            _jobCompletionRefreshGate.Release();
        }
    }

    private void SyncStatusBarFromJobs(JobInfo? latestJob = null)
    {
        var activeJob = RecentJobs
            .Where(job => !IsTerminalStatus(job.Status))
            .OrderByDescending(job => job.CreatedAtUtc)
            .FirstOrDefault();

        if (activeJob is not null)
        {
            _activeJobId = activeJob.JobId;
            IsOperationInProgress = true;
            OperationProgress = activeJob.Progress;
            OperationText = $"{activeJob.JobType}: {FormatJobStatusMessage(activeJob)}";
            return;
        }

        _activeJobId = null;
        IsOperationInProgress = false;

        if (latestJob is not null)
        {
            OperationProgress = latestJob.Progress;
            OperationText = $"{latestJob.JobType}: {FormatJobStatusMessage(latestJob)}";
        }
    }

    private void UpdateSelectedEvidenceVerifyStatus()
    {
        if (SelectedEvidenceItem is null)
        {
            SelectedEvidenceVerifyStatus = "No evidence selected.";
            return;
        }

        var latestVerify = RecentJobs
            .Where(job => job.JobType == EvidenceVerifyJobType)
            .Where(job => job.EvidenceItemId == SelectedEvidenceItem.EvidenceItemId)
            .OrderByDescending(job => job.CreatedAtUtc)
            .FirstOrDefault();

        if (latestVerify is null)
        {
            SelectedEvidenceVerifyStatus = "No verification job yet.";
            return;
        }

        SelectedEvidenceVerifyStatus = $"{latestVerify.Status}: {latestVerify.StatusMessage}";
    }

    private void UpdateSelectedEvidenceMessagesStatus()
    {
        if (SelectedEvidenceItem is null)
        {
            SelectedEvidenceMessagesStatus = "No evidence selected.";
            return;
        }

        if (LatestMessagesParseJob is null)
        {
            SelectedEvidenceMessagesStatus = "No messages parse job yet.";
            return;
        }

        SelectedEvidenceMessagesStatus = $"{LatestMessagesParseJob.Status}: {FormatJobStatusMessage(LatestMessagesParseJob)}";
    }

    private void ResetLatestMessagesParseJobTracking()
    {
        CancelLatestMessagesParseRefresh();
        LatestMessagesParseJob = null;
        UpdateSelectedEvidenceMessagesStatus();

        if (CurrentCaseInfo is null || SelectedEvidenceItem is null)
        {
            return;
        }

        QueueLatestMessagesParseJobRefresh();
    }

    private void QueueLatestMessagesParseJobRefresh()
    {
        CancelLatestMessagesParseRefresh();

        var refreshCts = new CancellationTokenSource();
        _latestMessagesParseRefreshCts = refreshCts;
        _ = RefreshLatestMessagesParseJobForSelectionAsync(refreshCts);
    }

    private async Task RefreshLatestMessagesParseJobForSelectionAsync(CancellationTokenSource refreshCts)
    {
        try
        {
            await RefreshLatestMessagesParseJobAsync(refreshCts.Token);
        }
        catch (OperationCanceledException) when (refreshCts.IsCancellationRequested)
        {
        }
        finally
        {
            if (ReferenceEquals(_latestMessagesParseRefreshCts, refreshCts))
            {
                _latestMessagesParseRefreshCts = null;
            }

            refreshCts.Dispose();
        }
    }

    private void CancelLatestMessagesParseRefresh()
    {
        var refreshCts = _latestMessagesParseRefreshCts;
        _latestMessagesParseRefreshCts = null;
        refreshCts?.Cancel();
        refreshCts?.Dispose();
    }

    private void ApplyLatestMessagesParseJobUpdate(JobInfo job)
    {
        if (job.JobType != MessagesIngestJobType || SelectedEvidenceItem is null)
        {
            return;
        }

        if (job.EvidenceItemId != SelectedEvidenceItem.EvidenceItemId)
        {
            return;
        }

        LatestMessagesParseJob = job;
    }

    private async Task RefreshLatestMessagesParseJobManuallyAsync()
    {
        CancelLatestMessagesParseRefresh();
        await RefreshLatestMessagesParseJobAsync(CancellationToken.None);
    }

    private async Task CancelLatestMessagesParseJobAsync()
    {
        if (LatestMessagesParseJob is null || IsTerminalStatus(LatestMessagesParseJob.Status))
        {
            return;
        }

        await _jobQueueService.CancelAsync(LatestMessagesParseJob.JobId, CancellationToken.None);
    }

    private async Task RefreshLatestMessagesParseJobAsync(CancellationToken ct)
    {
        if (CurrentCaseInfo is null || SelectedEvidenceItem is null)
        {
            LatestMessagesParseJob = null;
            return;
        }

        var caseId = CurrentCaseInfo.CaseId;
        var evidenceItemId = SelectedEvidenceItem.EvidenceItemId;
        var latestJob = await QueryLatestMessagesParseJobAsync(caseId, evidenceItemId, ct);

        if (ct.IsCancellationRequested)
        {
            return;
        }

        if (CurrentCaseInfo?.CaseId != caseId || SelectedEvidenceItem?.EvidenceItemId != evidenceItemId)
        {
            return;
        }

        LatestMessagesParseJob = latestJob;
    }

    private async Task<JobInfo?> QueryLatestMessagesParseJobAsync(
        Guid caseId,
        Guid evidenceItemId,
        CancellationToken ct
    )
    {
        await using var db = await _dbContextFactory.CreateDbContextAsync(ct);
        var latestRecord = await db.Jobs
            .AsNoTracking()
            .Where(job => job.CaseId == caseId)
            .Where(job => job.EvidenceItemId == evidenceItemId)
            .Where(job => job.JobType == MessagesIngestJobType)
            .OrderByDescending(job => job.CreatedAtUtc)
            .FirstOrDefaultAsync(ct);

        return latestRecord is null ? null : MapJobRecordToInfo(latestRecord);
    }

    private static bool IsTerminalStatus(JobStatus status)
    {
        return status is JobStatus.Succeeded or JobStatus.Failed or JobStatus.Canceled or JobStatus.Abandoned;
    }

    private OperationScope BeginOperation(string operationText)
    {
        _operationCts?.Cancel();
        _operationCts?.Dispose();
        _operationCts = new CancellationTokenSource();

        IsOperationInProgress = true;
        OperationProgress = 0;
        OperationText = operationText;

        return new OperationScope(this, _operationCts);
    }

    partial void OnOperationProgressChanged(double value)
    {
        OnPropertyChanged(nameof(OperationProgressPercentText));
    }

    private CancellationTokenSource BeginMessageSearch()
    {
        _messageSearchCts?.Cancel();
        _messageSearchCts?.Dispose();
        _messageSearchCts = new CancellationTokenSource();
        return _messageSearchCts;
    }

    private void EndMessageSearch(CancellationTokenSource searchCts)
    {
        if (!ReferenceEquals(_messageSearchCts, searchCts))
        {
            return;
        }

        IsMessageSearchInProgress = false;
        _messageSearchCts.Dispose();
        _messageSearchCts = null;
    }

    private void EndOperation(CancellationTokenSource cts)
    {
        if (!ReferenceEquals(_operationCts, cts))
        {
            return;
        }

        IsOperationInProgress = false;
        _operationCts.Dispose();
        _operationCts = null;
    }

    private void OnEvidenceItemsCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        OnPropertyChanged(nameof(CurrentCaseSummary));
    }

    private string BuildStoredAbsolutePath(EvidenceItem item)
    {
        var caseRootPath = Path.Combine(_workspacePathProvider.CasesRoot, item.CaseId.ToString("D"));
        return Path.Combine(caseRootPath, item.StoredRelativePath.Replace('/', Path.DirectorySeparatorChar));
    }

    private static string? NullIfWhiteSpace(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

    private static string FormatJobStatusMessage(JobInfo job)
    {
        if (IsTerminalStatus(job.Status))
        {
            return job.StatusMessage;
        }

        var percent = Math.Clamp(
            (int)Math.Round(job.Progress * 100, MidpointRounding.AwayFromZero),
            0,
            100
        );
        return $"{percent:0}% - {job.StatusMessage}";
    }

    private static JobInfo MapJobRecordToInfo(JobRecord record)
    {
        return new JobInfo
        {
            JobId = record.JobId,
            CreatedAtUtc = record.CreatedAtUtc,
            StartedAtUtc = record.StartedAtUtc,
            CompletedAtUtc = record.CompletedAtUtc,
            Status = Enum.TryParse<JobStatus>(record.Status, true, out var parsedStatus)
                ? parsedStatus
                : JobStatus.Failed,
            JobType = record.JobType,
            CaseId = record.CaseId,
            EvidenceItemId = record.EvidenceItemId,
            Progress = Math.Clamp(record.Progress, 0, 1),
            StatusMessage = record.StatusMessage,
            ErrorMessage = record.ErrorMessage,
            JsonPayload = record.JsonPayload,
            CorrelationId = record.CorrelationId,
            Operator = record.Operator
        };
    }

    public void Dispose()
    {
        if (_isDisposed)
        {
            return;
        }

        _isDisposed = true;
        CancelLatestMessagesParseRefresh();
        _operationCts?.Cancel();
        _operationCts?.Dispose();
        _operationCts = null;

        _messageSearchCts?.Cancel();
        _messageSearchCts?.Dispose();
        _messageSearchCts = null;

        _jobUpdateSubscription.Dispose();
        _jobCompletionRefreshGate.Dispose();
    }

    private sealed class OperationScope : IDisposable
    {
        private readonly MainWindowViewModel _owner;
        private readonly CancellationTokenSource _cts;

        public OperationScope(MainWindowViewModel owner, CancellationTokenSource cts)
        {
            _owner = owner;
            _cts = cts;
        }

        public CancellationToken Token => _cts.Token;

        public void Dispose()
        {
            _owner.EndOperation(_cts);
        }
    }

    private sealed class JobObserver : IObserver<JobInfo>
    {
        private readonly Action<JobInfo> _onNext;

        public JobObserver(Action<JobInfo> onNext)
        {
            _onNext = onNext;
        }

        public void OnCompleted()
        {
        }

        public void OnError(Exception error)
        {
        }

        public void OnNext(JobInfo value)
        {
            _onNext(value);
        }
    }

    private sealed class EvidenceImportPayload
    {
        public int SchemaVersion { get; set; }

        public Guid CaseId { get; set; }

        public List<string> Files { get; set; } = new();
    }

    private sealed class EvidenceVerifyPayload
    {
        public int SchemaVersion { get; set; }

        public Guid CaseId { get; set; }

        public Guid EvidenceItemId { get; set; }
    }

    private sealed class MessagesIngestPayload
    {
        public int SchemaVersion { get; set; }

        public Guid CaseId { get; set; }

        public Guid EvidenceItemId { get; set; }
    }
}
