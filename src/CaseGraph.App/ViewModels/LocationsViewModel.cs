using CaseGraph.App.Services;
using CaseGraph.Core.Abstractions;
using CaseGraph.Core.Diagnostics;
using CaseGraph.Infrastructure.Locations;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System.Collections.ObjectModel;
using System.Globalization;

namespace CaseGraph.App.ViewModels;

public partial class LocationsViewModel : ObservableObject, IDisposable
{
    private readonly LocationQueryService _locationQueryService;
    private readonly ITargetRegistryService _targetRegistryService;
    private readonly IUserInteractionService _userInteractionService;

    private CancellationTokenSource? _loadCts;
    private bool _isActive;
    private bool _isDisposed;

    public LocationsViewModel(
        LocationQueryService locationQueryService,
        ITargetRegistryService targetRegistryService,
        IUserInteractionService userInteractionService
    )
    {
        _locationQueryService = locationQueryService;
        _targetRegistryService = targetRegistryService;
        _userInteractionService = userInteractionService;

        SubjectFilters.Add(LocationSubjectFilterOption.AllSubjects);
        SelectedSubjectFilter = LocationSubjectFilterOption.AllSubjects;
        SelectedSourceType = SourceTypeFilters[0];

        SearchCommand = new AsyncRelayCommand(SearchAsync);
        ClearFiltersCommand = new AsyncRelayCommand(ClearFiltersAsync);
        NextPageCommand = new AsyncRelayCommand(LoadNextPageAsync, () => CanGoNext);
        PreviousPageCommand = new AsyncRelayCommand(LoadPreviousPageAsync, () => CanGoPrevious);
        ViewSourceCommand = new RelayCommand<LocationRowDto?>(ViewSource);
        CopyCitationCommand = new RelayCommand<LocationRowDto?>(CopyCitation);
    }

    public ObservableCollection<LocationRowDto> Rows { get; } = new();

    public ObservableCollection<LocationSubjectFilterOption> SubjectFilters { get; } = new();

    public IReadOnlyList<string> SourceTypeFilters { get; } =
    [
        "Any",
        "CSV",
        "JSON",
        "PLIST"
    ];

    public Action<LocationRowDto>? ViewSourceRequested { get; set; }

    public int PageSize => 250;

    public bool HasRows => Rows.Count > 0;

    public bool CanGoPrevious => !IsLoading && CurrentPage > 1;

    public bool CanGoNext => !IsLoading && CurrentPage * PageSize < TotalCount;

    public string PageSummaryText
    {
        get
        {
            if (TotalCount <= 0)
            {
                return "No location results.";
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

    public IRelayCommand<LocationRowDto?> ViewSourceCommand { get; }

    public IRelayCommand<LocationRowDto?> CopyCitationCommand { get; }

    [ObservableProperty]
    private Guid? currentCaseId;

    [ObservableProperty]
    private LocationSubjectFilterOption? selectedSubjectFilter;

    [ObservableProperty]
    private DateTime? fromDateLocal;

    [ObservableProperty]
    private DateTime? toDateLocal;

    [ObservableProperty]
    private string minAccuracyText = string.Empty;

    [ObservableProperty]
    private string maxAccuracyText = string.Empty;

    [ObservableProperty]
    private string selectedSourceType = "Any";

    [ObservableProperty]
    private LocationRowDto? selectedRow;

    [ObservableProperty]
    private bool isLoading;

    [ObservableProperty]
    private string statusText = "Open a case to load locations.";

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
            StatusText = "Open a case to load locations.";
            ResetFiltersToDefaults();
            return;
        }

        await RefreshSubjectFiltersAsync(ct);
        if (_isActive)
        {
            await LoadPageAsync(0, ct);
        }
        else
        {
            ClearRows();
            TotalCount = 0;
            CurrentPage = 1;
            StatusText = "Locations ready. Open the Locations page to load results.";
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
            StatusText = "Open a case to load locations.";
            return;
        }

        await RefreshSubjectFiltersAsync(ct);
        await LoadPageAsync(Math.Max(CurrentPage - 1, 0), ct);
    }

    public void Deactivate()
    {
        _isActive = false;
    }

    public async Task RefreshCurrentPageAsync(CancellationToken ct)
    {
        if (!_isActive || !CurrentCaseId.HasValue)
        {
            return;
        }

        await LoadPageAsync(Math.Max(CurrentPage - 1, 0), ct);
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
            StatusText = "Open a case to load locations.";
            return;
        }

        if (!TryParseNullableDouble(MinAccuracyText, out var minAccuracy))
        {
            StatusText = "Min accuracy filter is not a valid number.";
            return;
        }

        if (!TryParseNullableDouble(MaxAccuracyText, out var maxAccuracy))
        {
            StatusText = "Max accuracy filter is not a valid number.";
            return;
        }

        var loadCts = BeginLoad(outerCt);
        IsLoading = true;
        StatusText = "Loading locations...";
        var correlationId = AppFileLogger.NewCorrelationId();

        try
        {
            var page = await _locationQueryService.SearchAsync(
                new LocationQueryRequest(
                    CaseId: CurrentCaseId.Value,
                    FromUtc: ConvertLocalDateToStartUtc(FromDateLocal),
                    ToUtc: ConvertLocalDateToInclusiveEndUtc(ToDateLocal),
                    MinAccuracyMeters: minAccuracy,
                    MaxAccuracyMeters: maxAccuracy,
                    SourceType: string.Equals(SelectedSourceType, "Any", StringComparison.OrdinalIgnoreCase)
                        ? null
                        : SelectedSourceType,
                    SubjectType: SelectedSubjectFilter?.SubjectType,
                    SubjectId: SelectedSubjectFilter?.SubjectId,
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
                ? "No location observations match the current filters."
                : $"Loaded {page.Rows.Count:0} location row(s).";
        }
        catch (OperationCanceledException) when (loadCts.IsCancellationRequested)
        {
            if (!ReferenceEquals(_loadCts, loadCts))
            {
                return;
            }

            StatusText = "Locations load canceled.";
        }
        finally
        {
            EndLoad(loadCts);
        }
    }

    private async Task RefreshSubjectFiltersAsync(CancellationToken ct)
    {
        if (!CurrentCaseId.HasValue)
        {
            SubjectFilters.Clear();
            SubjectFilters.Add(LocationSubjectFilterOption.AllSubjects);
            SelectedSubjectFilter = LocationSubjectFilterOption.AllSubjects;
            return;
        }

        var selectedSubjectType = SelectedSubjectFilter?.SubjectType;
        var selectedSubjectId = SelectedSubjectFilter?.SubjectId;

        var targets = await _targetRegistryService.GetTargetsAsync(CurrentCaseId.Value, search: null, ct);
        SubjectFilters.Clear();
        SubjectFilters.Add(LocationSubjectFilterOption.AllSubjects);

        foreach (var target in targets.OrderBy(item => item.DisplayName, StringComparer.OrdinalIgnoreCase))
        {
            SubjectFilters.Add(new LocationSubjectFilterOption(
                SubjectType: "Target",
                SubjectId: target.TargetId,
                DisplayName: $"Target: {target.DisplayName}"
            ));
        }

        foreach (var global in targets
                     .Where(item => item.GlobalEntityId.HasValue)
                     .GroupBy(item => item.GlobalEntityId!.Value)
                     .Select(group =>
                     {
                         var display = group
                             .Select(item => item.GlobalDisplayName)
                             .FirstOrDefault(item => !string.IsNullOrWhiteSpace(item))
                             ?? $"Global Person {group.Key:D}";
                         return new LocationSubjectFilterOption(
                             SubjectType: "GlobalPerson",
                             SubjectId: group.Key,
                             DisplayName: $"Global: {display}"
                         );
                     })
                     .OrderBy(item => item.DisplayName, StringComparer.OrdinalIgnoreCase))
        {
            SubjectFilters.Add(global);
        }

        SelectedSubjectFilter = !string.IsNullOrWhiteSpace(selectedSubjectType) && selectedSubjectId.HasValue
            ? SubjectFilters.FirstOrDefault(
                option => string.Equals(option.SubjectType, selectedSubjectType, StringComparison.OrdinalIgnoreCase)
                    && option.SubjectId == selectedSubjectId
            )
            : LocationSubjectFilterOption.AllSubjects;
        if (SelectedSubjectFilter is null)
        {
            SelectedSubjectFilter = LocationSubjectFilterOption.AllSubjects;
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

    private void SetRows(IReadOnlyList<LocationRowDto> rows)
    {
        var selectedObservationId = SelectedRow?.LocationObservationId;

        Rows.Clear();
        foreach (var row in rows)
        {
            Rows.Add(row);
        }

        SelectedRow = selectedObservationId.HasValue
            ? Rows.FirstOrDefault(row => row.LocationObservationId == selectedObservationId.Value)
            : Rows.FirstOrDefault();

        OnPropertyChanged(nameof(HasRows));
        OnPropertyChanged(nameof(PageSummaryText));
    }

    private void ResetFiltersToDefaults()
    {
        FromDateLocal = null;
        ToDateLocal = null;
        MinAccuracyText = string.Empty;
        MaxAccuracyText = string.Empty;
        SelectedSourceType = SourceTypeFilters[0];

        var subjectSelection = SubjectFilters.FirstOrDefault()
            ?? LocationSubjectFilterOption.AllSubjects;
        SelectedSubjectFilter = subjectSelection;
    }

    private void ClearRows()
    {
        Rows.Clear();
        SelectedRow = null;
        OnPropertyChanged(nameof(HasRows));
        OnPropertyChanged(nameof(PageSummaryText));
    }

    private void ViewSource(LocationRowDto? row)
    {
        var selected = row ?? SelectedRow;
        if (selected is null)
        {
            return;
        }

        StatusText = $"Opening source for {selected.SourceLocator}...";
        ViewSourceRequested?.Invoke(selected);
    }

    private void CopyCitation(LocationRowDto? row)
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

    private static bool TryParseNullableDouble(string text, out double? value)
    {
        value = null;
        if (string.IsNullOrWhiteSpace(text))
        {
            return true;
        }

        var normalized = text.Trim();
        if (double.TryParse(
                normalized,
                NumberStyles.Float | NumberStyles.AllowThousands,
                CultureInfo.InvariantCulture,
                out var parsed)
            || double.TryParse(
                normalized,
                NumberStyles.Float | NumberStyles.AllowThousands,
                CultureInfo.CurrentCulture,
                out parsed))
        {
            value = parsed;
            return true;
        }

        return false;
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

    public sealed record LocationSubjectFilterOption(
        string? SubjectType,
        Guid? SubjectId,
        string DisplayName
    )
    {
        public static LocationSubjectFilterOption AllSubjects { get; } = new(
            SubjectType: null,
            SubjectId: null,
            DisplayName: "Any subject"
        );
    }
}
