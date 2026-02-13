using CaseGraph.App.Models;
using CaseGraph.App.Services;
using CaseGraph.Core.Abstractions;
using CaseGraph.Core.Models;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.IO;
using System.Linq;

namespace CaseGraph.App.ViewModels;

public partial class MainWindowViewModel : ObservableObject
{
    private readonly INavigationService _navigationService;
    private readonly IThemeService _themeService;
    private readonly ICaseWorkspaceService _caseWorkspaceService;
    private readonly IEvidenceVaultService _evidenceVaultService;
    private readonly IAuditLogService _auditLogService;
    private readonly IWorkspacePathProvider _workspacePathProvider;
    private readonly IUserInteractionService _userInteractionService;

    private CancellationTokenSource? _operationCts;
    private bool _isInitialized;

    public ObservableCollection<NavigationItem> NavigationItems { get; } = new();

    public ObservableCollection<CaseInfo> AvailableCases { get; } = new();

    public ObservableCollection<EvidenceItem> EvidenceItems { get; } = new();

    public ObservableCollection<AuditEvent> RecentAuditEvents { get; } = new();

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
    private string operationText = "Ready.";

    [ObservableProperty]
    private double operationProgress;

    [ObservableProperty]
    private bool isOperationInProgress;

    public bool HasSelectedEvidenceItem => SelectedEvidenceItem is not null;

    public string CurrentCaseSummary => CurrentCaseInfo is null
        ? "No case is currently open."
        : $"Current case: {CurrentCaseInfo.Name} ({EvidenceItems.Count} evidence item(s))";

    public string SelectedStoredAbsolutePath => SelectedEvidenceItem is null
        ? string.Empty
        : BuildStoredAbsolutePath(SelectedEvidenceItem);

    public IRelayCommand ToggleThemeCommand { get; }

    public IRelayCommand ToggleEvidenceDrawerCommand { get; }

    public IAsyncRelayCommand CreateNewCaseCommand { get; }

    public IAsyncRelayCommand OpenSelectedCaseCommand { get; }

    public IAsyncRelayCommand RefreshCasesCommand { get; }

    public IAsyncRelayCommand ImportFilesCommand { get; }

    public IAsyncRelayCommand VerifySelectedEvidenceCommand { get; }

    public IRelayCommand CopyStoredPathCommand { get; }

    public IRelayCommand CopySha256Command { get; }

    public IRelayCommand CancelOperationCommand { get; }

    public MainWindowViewModel(
        INavigationService navigationService,
        IThemeService themeService,
        ICaseWorkspaceService caseWorkspaceService,
        IEvidenceVaultService evidenceVaultService,
        IAuditLogService auditLogService,
        IWorkspacePathProvider workspacePathProvider,
        IUserInteractionService userInteractionService
    )
    {
        _navigationService = navigationService;
        _themeService = themeService;
        _caseWorkspaceService = caseWorkspaceService;
        _evidenceVaultService = evidenceVaultService;
        _auditLogService = auditLogService;
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
        CopyStoredPathCommand = new RelayCommand(CopyStoredPath);
        CopySha256Command = new RelayCommand(CopySha256);
        CancelOperationCommand = new RelayCommand(CancelCurrentOperation);

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

        if (value is not null)
        {
            IsEvidenceDrawerOpen = true;
        }
    }

    partial void OnCurrentCaseInfoChanged(CaseInfo? value)
    {
        OnPropertyChanged(nameof(CurrentCaseSummary));
        OnPropertyChanged(nameof(SelectedStoredAbsolutePath));
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

        using var operation = BeginOperation($"Importing 0/{files.Count}...");

        try
        {
            for (var index = 0; index < files.Count; index++)
            {
                operation.Token.ThrowIfCancellationRequested();

                var filePath = files[index];
                OperationText = $"Importing {index + 1}/{files.Count}: {Path.GetFileName(filePath)}";

                var itemProgress = new Progress<double>(p =>
                {
                    OperationProgress = (index + p) / files.Count;
                });

                var importedItem = await _evidenceVaultService.ImportEvidenceFileAsync(
                    CurrentCaseInfo,
                    filePath,
                    itemProgress,
                    operation.Token
                );

                EvidenceItems.Add(importedItem);
                SelectedEvidenceItem = importedItem;
            }

            await RefreshCurrentCaseAsync(operation.Token);
            await RefreshRecentActivityAsync(operation.Token);
            OperationProgress = 1.0;
            OperationText = $"Imported {files.Count} file(s) successfully.";
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

        using var operation = BeginOperation($"Verifying {SelectedEvidenceItem.OriginalFileName}...");

        try
        {
            var progress = new Progress<double>(p => OperationProgress = p);
            var (ok, message) = await _evidenceVaultService.VerifyEvidenceAsync(
                CurrentCaseInfo,
                SelectedEvidenceItem,
                progress,
                operation.Token
            );

            await RefreshRecentActivityAsync(operation.Token);
            OperationProgress = 1.0;
            OperationText = ok ? $"Integrity OK. {message}" : $"Integrity FAILED. {message}";
        }
        catch (OperationCanceledException)
        {
            OperationText = "Verify canceled.";
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

    private void CancelCurrentOperation()
    {
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

    private OperationScope BeginOperation(string operationText)
    {
        CancelCurrentOperation();
        _operationCts?.Dispose();
        _operationCts = new CancellationTokenSource();

        IsOperationInProgress = true;
        OperationProgress = 0;
        OperationText = operationText;

        return new OperationScope(this, _operationCts);
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
}
