using CaseGraph.App.Services;
using CaseGraph.Core.Abstractions;
using CaseGraph.Core.Diagnostics;
using CaseGraph.Infrastructure.Timeline;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System.Collections.ObjectModel;

namespace CaseGraph.App.ViewModels;

public partial class TimelineViewModel : ObservableObject, IDisposable
{
    private readonly TimelineQueryService _timelineQueryService;
    private readonly ITargetRegistryService _targetRegistryService;
    private readonly IUserInteractionService _userInteractionService;

    private CancellationTokenSource? _loadCts;
    private bool _isActive;
    private bool _isDisposed;

    public TimelineViewModel(
        TimelineQueryService timelineQueryService,
        ITargetRegistryService targetRegistryService,
        IUserInteractionService userInteractionService
    )
    {
        _timelineQueryService = timelineQueryService;
        _targetRegistryService = targetRegistryService;
        _userInteractionService = userInteractionService;

        TargetFilters.Add(TimelineTargetFilterOption.AllTargets);
        GlobalPersonFilters.Add(TimelineGlobalPersonFilterOption.AllGlobalPersons);
        SelectedTargetFilter = TimelineTargetFilterOption.AllTargets;
        SelectedGlobalPersonFilter = TimelineGlobalPersonFilterOption.AllGlobalPersons;

        SearchCommand = new AsyncRelayCommand(SearchAsync);
        ClearFiltersCommand = new AsyncRelayCommand(ClearFiltersAsync);
        NextPageCommand = new AsyncRelayCommand(LoadNextPageAsync, () => CanGoNext);
        PreviousPageCommand = new AsyncRelayCommand(LoadPreviousPageAsync, () => CanGoPrevious);
        ViewSourceCommand = new RelayCommand<TimelineRowDto?>(ViewSource);
        CopyCitationCommand = new RelayCommand<TimelineRowDto?>(CopyCitation);
    }

    public ObservableCollection<TimelineRowDto> Rows { get; } = new();

    public ObservableCollection<TimelineTargetFilterOption> TargetFilters { get; } = new();

    public ObservableCollection<TimelineGlobalPersonFilterOption> GlobalPersonFilters { get; } = new();

    public IReadOnlyList<string> DirectionFilters { get; } =
    [
        "Any",
        "Incoming",
        "Outgoing",
        "Unknown"
    ];

    public Action<TimelineRowDto>? ViewSourceRequested { get; set; }

    public int PageSize => 200;

    public bool HasRows => Rows.Count > 0;

    public bool CanGoPrevious => !IsLoading && CurrentPage > 1;

    public bool CanGoNext => !IsLoading && CurrentPage * PageSize < TotalCount;

    public string PageSummaryText
    {
        get
        {
            if (TotalCount <= 0)
            {
                return "No timeline results.";
            }

            var start = ((CurrentPage - 1) * PageSize) + 1;
            var end = Math.Min(CurrentPage * PageSize, TotalCount);
            return $"Showing {start:0}-{end:0} of {TotalCount:0}";
        }
    }

    public IAsyncRelayCommand SearchCommand { get; }

    public IAsyncRelayCommand ClearFiltersCommand { get; }

    public IAsyncRelayCommand NextPageCommand { get; }

    public IAsyncRelayCommand PreviousPageCommand { get; }

    public IRelayCommand<TimelineRowDto?> ViewSourceCommand { get; }

    public IRelayCommand<TimelineRowDto?> CopyCitationCommand { get; }

    [ObservableProperty]
    private Guid? currentCaseId;

    [ObservableProperty]
    private string queryText = string.Empty;

    [ObservableProperty]
    private TimelineTargetFilterOption? selectedTargetFilter;

    [ObservableProperty]
    private TimelineGlobalPersonFilterOption? selectedGlobalPersonFilter;

    [ObservableProperty]
    private string selectedDirection = "Any";

    [ObservableProperty]
    private DateTime? fromDateLocal;

    [ObservableProperty]
    private DateTime? toDateLocal;

    [ObservableProperty]
    private TimelineRowDto? selectedRow;

    [ObservableProperty]
    private bool isLoading;

    [ObservableProperty]
    private string statusText = "Open a case to load the timeline.";

    [ObservableProperty]
    private int currentPage = 1;

    [ObservableProperty]
    private int totalCount;

    public async Task SetCurrentCaseAsync(Guid? caseId, CancellationToken ct)
    {
        CurrentCaseId = caseId;
        CancelLoad();

        if (!caseId.HasValue)
        {
            ClearRows();
            TotalCount = 0;
            CurrentPage = 1;
            StatusText = "Open a case to load the timeline.";
            ResetFiltersToDefaults();
            return;
        }

        await RefreshFilterOptionsAsync(ct);
        if (_isActive)
        {
            await LoadPageAsync(0, ct);
        }
        else
        {
            ClearRows();
            TotalCount = 0;
            CurrentPage = 1;
            StatusText = "Timeline ready. Open the Timeline page to load results.";
        }
    }

    public async Task ActivateAsync(CancellationToken ct)
    {
        _isActive = true;

        if (!CurrentCaseId.HasValue)
        {
            ClearRows();
            TotalCount = 0;
            CurrentPage = 1;
            StatusText = "Open a case to load the timeline.";
            return;
        }

        await RefreshFilterOptionsAsync(ct);
        await LoadPageAsync(Math.Max(CurrentPage - 1, 0), ct);
    }

    public void Deactivate()
    {
        _isActive = false;
    }

    private async Task SearchAsync()
    {
        await LoadPageAsync(0, CancellationToken.None);
    }

    private async Task ClearFiltersAsync()
    {
        ResetFiltersToDefaults();
        if (CurrentCaseId.HasValue && _isActive)
        {
            await LoadPageAsync(0, CancellationToken.None);
        }
        else
        {
            ClearRows();
            TotalCount = 0;
            CurrentPage = 1;
            StatusText = "Filters cleared.";
        }
    }

    private async Task LoadNextPageAsync()
    {
        if (!CanGoNext)
        {
            return;
        }

        await LoadPageAsync(CurrentPage, CancellationToken.None);
    }

    private async Task LoadPreviousPageAsync()
    {
        if (!CanGoPrevious)
        {
            return;
        }

        await LoadPageAsync(CurrentPage - 2, CancellationToken.None);
    }

    private async Task LoadPageAsync(int zeroBasedPageIndex, CancellationToken outerCt)
    {
        if (!CurrentCaseId.HasValue)
        {
            ClearRows();
            TotalCount = 0;
            CurrentPage = 1;
            StatusText = "Open a case to load the timeline.";
            return;
        }

        var loadCts = BeginLoad(outerCt);
        var correlationId = AppFileLogger.NewCorrelationId();
        IsLoading = true;
        StatusText = "Loading timeline...";

        try
        {
            var page = await _timelineQueryService.SearchAsync(
                new TimelineQueryRequest(
                    CaseId: CurrentCaseId.Value,
                    QueryText: NullIfWhiteSpace(QueryText),
                    TargetId: SelectedTargetFilter?.TargetId,
                    GlobalEntityId: SelectedGlobalPersonFilter?.GlobalEntityId,
                    Direction: NormalizeDirectionFilter(SelectedDirection),
                    FromUtc: ConvertLocalDateToStartUtc(FromDateLocal),
                    ToUtc: ConvertLocalDateToInclusiveEndUtc(ToDateLocal),
                    Take: PageSize,
                    Skip: zeroBasedPageIndex * PageSize,
                    CorrelationId: correlationId
                ),
                loadCts.Token
            );

            if (!ReferenceEquals(_loadCts, loadCts))
            {
                return;
            }

            CurrentPage = zeroBasedPageIndex + 1;
            TotalCount = page.TotalCount;
            SetRows(page.Rows);
            StatusText = page.TotalCount == 0
                ? "No timeline events match the current filters."
                : $"Loaded {page.Rows.Count:0} timeline row(s).";
        }
        catch (OperationCanceledException) when (loadCts.IsCancellationRequested)
        {
            if (!ReferenceEquals(_loadCts, loadCts))
            {
                return;
            }

            StatusText = "Timeline load canceled.";
        }
        finally
        {
            EndLoad(loadCts);
        }
    }

    private async Task RefreshFilterOptionsAsync(CancellationToken ct)
    {
        if (!CurrentCaseId.HasValue)
        {
            ResetFiltersToDefaults();
            return;
        }

        var selectedTargetId = SelectedTargetFilter?.TargetId;
        var selectedGlobalEntityId = SelectedGlobalPersonFilter?.GlobalEntityId;
        var targets = await _targetRegistryService.GetTargetsAsync(CurrentCaseId.Value, search: null, ct);

        TargetFilters.Clear();
        TargetFilters.Add(TimelineTargetFilterOption.AllTargets);
        foreach (var target in targets)
        {
            TargetFilters.Add(new TimelineTargetFilterOption(target.TargetId, target.DisplayName));
        }

        GlobalPersonFilters.Clear();
        GlobalPersonFilters.Add(TimelineGlobalPersonFilterOption.AllGlobalPersons);
        foreach (var globalPerson in targets
                     .Where(target => target.GlobalEntityId.HasValue)
                     .GroupBy(target => target.GlobalEntityId!.Value)
                     .Select(group =>
                     {
                         var display = group
                             .Select(target => target.GlobalDisplayName)
                             .FirstOrDefault(name => !string.IsNullOrWhiteSpace(name))
                             ?? $"Global Person {group.Key:D}";
                         return new TimelineGlobalPersonFilterOption(group.Key, display);
                     })
                     .OrderBy(option => option.DisplayName, StringComparer.OrdinalIgnoreCase))
        {
            GlobalPersonFilters.Add(globalPerson);
        }

        SelectedTargetFilter = selectedTargetId.HasValue
            ? TargetFilters.FirstOrDefault(option => option.TargetId == selectedTargetId.Value)
            : TimelineTargetFilterOption.AllTargets;
        if (SelectedTargetFilter is null)
        {
            SelectedTargetFilter = TimelineTargetFilterOption.AllTargets;
        }

        SelectedGlobalPersonFilter = selectedGlobalEntityId.HasValue
            ? GlobalPersonFilters.FirstOrDefault(
                option => option.GlobalEntityId == selectedGlobalEntityId.Value
            )
            : TimelineGlobalPersonFilterOption.AllGlobalPersons;
        if (SelectedGlobalPersonFilter is null)
        {
            SelectedGlobalPersonFilter = TimelineGlobalPersonFilterOption.AllGlobalPersons;
        }
    }

    private CancellationTokenSource BeginLoad(CancellationToken outerCt)
    {
        CancelLoad();

        var loadCts = CancellationTokenSource.CreateLinkedTokenSource(outerCt);
        _loadCts = loadCts;
        return loadCts;
    }

    private void EndLoad(CancellationTokenSource loadCts)
    {
        if (!ReferenceEquals(_loadCts, loadCts))
        {
            loadCts.Dispose();
            return;
        }

        IsLoading = false;
        _loadCts.Dispose();
        _loadCts = null;
    }

    private void CancelLoad()
    {
        var loadCts = _loadCts;
        _loadCts = null;
        loadCts?.Cancel();
        loadCts?.Dispose();
        IsLoading = false;
    }

    private void SetRows(IReadOnlyList<TimelineRowDto> rows)
    {
        var selectedMessageId = SelectedRow?.MessageEventId;

        Rows.Clear();
        foreach (var row in rows)
        {
            Rows.Add(row);
        }

        SelectedRow = selectedMessageId.HasValue
            ? Rows.FirstOrDefault(row => row.MessageEventId == selectedMessageId.Value)
            : Rows.FirstOrDefault();
        OnPropertyChanged(nameof(HasRows));
        OnPropertyChanged(nameof(PageSummaryText));
    }

    private void ResetFiltersToDefaults()
    {
        QueryText = string.Empty;
        SelectedDirection = "Any";
        FromDateLocal = null;
        ToDateLocal = null;

        TargetFilters.Clear();
        TargetFilters.Add(TimelineTargetFilterOption.AllTargets);
        SelectedTargetFilter = TimelineTargetFilterOption.AllTargets;

        GlobalPersonFilters.Clear();
        GlobalPersonFilters.Add(TimelineGlobalPersonFilterOption.AllGlobalPersons);
        SelectedGlobalPersonFilter = TimelineGlobalPersonFilterOption.AllGlobalPersons;
    }

    private void ClearRows()
    {
        Rows.Clear();
        SelectedRow = null;
        OnPropertyChanged(nameof(HasRows));
        OnPropertyChanged(nameof(PageSummaryText));
    }

    private void ViewSource(TimelineRowDto? row)
    {
        var selected = row ?? SelectedRow;
        if (selected is null)
        {
            return;
        }

        StatusText = $"Opening source for {selected.SourceLocator}...";
        ViewSourceRequested?.Invoke(selected);
    }

    private void CopyCitation(TimelineRowDto? row)
    {
        var selected = row ?? SelectedRow;
        if (selected is null)
        {
            return;
        }

        _userInteractionService.CopyToClipboard(selected.Citation);
        StatusText = "Citation copied.";
    }

    partial void OnIsLoadingChanged(bool value)
    {
        NextPageCommand.NotifyCanExecuteChanged();
        PreviousPageCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(CanGoNext));
        OnPropertyChanged(nameof(CanGoPrevious));
    }

    partial void OnCurrentPageChanged(int value)
    {
        NextPageCommand.NotifyCanExecuteChanged();
        PreviousPageCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(CanGoNext));
        OnPropertyChanged(nameof(CanGoPrevious));
        OnPropertyChanged(nameof(PageSummaryText));
    }

    partial void OnTotalCountChanged(int value)
    {
        NextPageCommand.NotifyCanExecuteChanged();
        PreviousPageCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(CanGoNext));
        OnPropertyChanged(nameof(CanGoPrevious));
        OnPropertyChanged(nameof(PageSummaryText));
    }

    private static string? NullIfWhiteSpace(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

    private static string? NormalizeDirectionFilter(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        return string.Equals(value.Trim(), "Any", StringComparison.OrdinalIgnoreCase)
            ? null
            : value.Trim();
    }

    private static DateTimeOffset? ConvertLocalDateToStartUtc(DateTime? localDate)
    {
        if (!localDate.HasValue)
        {
            return null;
        }

        var localUnspecified = DateTime.SpecifyKind(localDate.Value.Date, DateTimeKind.Unspecified);
        var offset = TimeZoneInfo.Local.GetUtcOffset(localUnspecified);
        return new DateTimeOffset(localUnspecified, offset).ToUniversalTime();
    }

    private static DateTimeOffset? ConvertLocalDateToInclusiveEndUtc(DateTime? localDate)
    {
        if (!localDate.HasValue)
        {
            return null;
        }

        var localUnspecified = DateTime.SpecifyKind(
            localDate.Value.Date.AddDays(1).AddTicks(-1),
            DateTimeKind.Unspecified
        );
        var offset = TimeZoneInfo.Local.GetUtcOffset(localUnspecified);
        return new DateTimeOffset(localUnspecified, offset).ToUniversalTime();
    }

    public void Dispose()
    {
        if (_isDisposed)
        {
            return;
        }

        _isDisposed = true;
        CancelLoad();
    }

    public sealed record TimelineTargetFilterOption(Guid? TargetId, string DisplayName)
    {
        public static TimelineTargetFilterOption AllTargets { get; } = new(
            TargetId: null,
            DisplayName: "Any target"
        );
    }

    public sealed record TimelineGlobalPersonFilterOption(Guid? GlobalEntityId, string DisplayName)
    {
        public static TimelineGlobalPersonFilterOption AllGlobalPersons { get; } = new(
            GlobalEntityId: null,
            DisplayName: "Any global person"
        );
    }
}
