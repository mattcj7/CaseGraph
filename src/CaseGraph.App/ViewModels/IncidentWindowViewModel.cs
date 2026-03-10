using CaseGraph.App.Services;
using CaseGraph.Core.Abstractions;
using CaseGraph.Core.Diagnostics;
using CaseGraph.Infrastructure.Diagnostics;
using CaseGraph.Infrastructure.IncidentWindow;
using CaseGraph.Infrastructure.Locations;
using CaseGraph.Infrastructure.Timeline;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows;
using System.Windows.Threading;

namespace CaseGraph.App.ViewModels;

public partial class IncidentWindowViewModel : ObservableObject, IDisposable
{
    private readonly IFeatureReadinessService _featureReadinessService;
    private readonly IBackgroundMaintenanceManager _backgroundMaintenanceManager;
    private readonly IncidentWindowQueryService _queryService;
    private readonly ITargetRegistryService _targetRegistryService;
    private readonly IUserInteractionService _userInteractionService;
    private readonly IPerformanceInstrumentation _performanceInstrumentation;

    private CancellationTokenSource? _loadCts;
    private bool _isActive;
    private bool _isDisposed;
    private bool _hasExecuted;

    public IncidentWindowViewModel(
        IFeatureReadinessService featureReadinessService,
        IBackgroundMaintenanceManager backgroundMaintenanceManager,
        IncidentWindowQueryService queryService,
        ITargetRegistryService targetRegistryService,
        IUserInteractionService userInteractionService,
        IPerformanceInstrumentation? performanceInstrumentation = null
    )
    {
        _featureReadinessService = featureReadinessService;
        _backgroundMaintenanceManager = backgroundMaintenanceManager;
        _queryService = queryService;
        _targetRegistryService = targetRegistryService;
        _userInteractionService = userInteractionService;
        _performanceInstrumentation = performanceInstrumentation
            ?? new PerformanceInstrumentation(new PerformanceBudgetOptions(), TimeProvider.System);
        _backgroundMaintenanceManager.SnapshotChanged += OnMaintenanceSnapshotChanged;

        SubjectFilters.Add(IncidentWindowSubjectFilterOption.AllSubjects);
        SelectedSubjectFilter = IncidentWindowSubjectFilterOption.AllSubjects;
        ResetInputsToDefaults();

        RunCommand = new AsyncRelayCommand(RunAsync);
        ClearCommand = new RelayCommand(Clear);
        ApplyPresetCommand = new RelayCommand(ApplyPresetWindow);
        NextCommsPageCommand = new AsyncRelayCommand(LoadNextCommsPageAsync, () => CanGoCommsNext);
        PreviousCommsPageCommand = new AsyncRelayCommand(LoadPreviousCommsPageAsync, () => CanGoCommsPrevious);
        NextGeoPageCommand = new AsyncRelayCommand(LoadNextGeoPageAsync, () => CanGoGeoNext);
        PreviousGeoPageCommand = new AsyncRelayCommand(LoadPreviousGeoPageAsync, () => CanGoGeoPrevious);
        NextCoLocationPageCommand = new AsyncRelayCommand(LoadNextCoLocationPageAsync, () => CanGoCoLocationNext);
        PreviousCoLocationPageCommand = new AsyncRelayCommand(LoadPreviousCoLocationPageAsync, () => CanGoCoLocationPrevious);
        ViewCommsSourceCommand = new RelayCommand<TimelineRowDto?>(ViewCommsSource);
        CopyCommsCitationCommand = new RelayCommand<TimelineRowDto?>(CopyCommsCitation);
        ViewGeoSourceCommand = new RelayCommand<LocationRowDto?>(ViewGeoSource);
        CopyGeoCitationCommand = new RelayCommand<LocationRowDto?>(CopyGeoCitation);
        ViewCoLocationSourceCommand = new RelayCommand<IncidentWindowCoLocationCandidateDto?>(ViewCoLocationPrimarySource);
        ViewCoLocationSecondarySourceCommand = new RelayCommand<IncidentWindowCoLocationCandidateDto?>(ViewCoLocationSecondarySource);
        CopyCoLocationCitationCommand = new RelayCommand<IncidentWindowCoLocationCandidateDto?>(CopyCoLocationCitation);
    }

    public ObservableCollection<IncidentWindowSubjectFilterOption> SubjectFilters { get; } = new();

    public ObservableCollection<TimelineRowDto> CommsRows { get; } = new();

    public ObservableCollection<LocationRowDto> GeoRows { get; } = new();

    public ObservableCollection<IncidentWindowCoLocationCandidateDto> CoLocationRows { get; } = new();

    public Action<TimelineRowDto>? ViewCommsSourceRequested { get; set; }

    public Action<LocationRowDto>? ViewGeoSourceRequested { get; set; }

    public Action<LocationRowDto>? ViewCoLocationSourceRequested { get; set; }

    public int PageSize => 100;

    public bool CanGoCommsPrevious => _hasExecuted && !IsLoading && CommsCurrentPage > 1;

    public bool CanGoCommsNext => _hasExecuted && !IsLoading && CommsCurrentPage * PageSize < CommsTotalCount;

    public bool CanGoGeoPrevious => _hasExecuted && !IsLoading && GeoCurrentPage > 1;

    public bool CanGoGeoNext => _hasExecuted && !IsLoading && GeoCurrentPage * PageSize < GeoTotalCount;

    public bool CanGoCoLocationPrevious => _hasExecuted && !IsLoading && CoLocationCurrentPage > 1;

    public bool CanGoCoLocationNext => _hasExecuted && !IsLoading && CoLocationCurrentPage * PageSize < CoLocationTotalCount;

    public string CommsPageSummaryText => BuildPageSummary("comms hit", CommsCurrentPage, CommsTotalCount);

    public string GeoPageSummaryText => BuildPageSummary("geo hit", GeoCurrentPage, GeoTotalCount);

    public string CoLocationPageSummaryText => BuildPageSummary("candidate", CoLocationCurrentPage, CoLocationTotalCount);

    public string CoLocationHintText => !RadiusEnabled && IncludeCoLocationCandidates
        ? "Enable a scene center + radius to evaluate co-location candidates."
        : "Best-effort pairs are generated within 100m and 10 minutes.";

    public IAsyncRelayCommand RunCommand { get; }

    public IRelayCommand ClearCommand { get; }

    public IRelayCommand ApplyPresetCommand { get; }

    public IAsyncRelayCommand NextCommsPageCommand { get; }

    public IAsyncRelayCommand PreviousCommsPageCommand { get; }

    public IAsyncRelayCommand NextGeoPageCommand { get; }

    public IAsyncRelayCommand PreviousGeoPageCommand { get; }

    public IAsyncRelayCommand NextCoLocationPageCommand { get; }

    public IAsyncRelayCommand PreviousCoLocationPageCommand { get; }

    public IRelayCommand<TimelineRowDto?> ViewCommsSourceCommand { get; }

    public IRelayCommand<TimelineRowDto?> CopyCommsCitationCommand { get; }

    public IRelayCommand<LocationRowDto?> ViewGeoSourceCommand { get; }

    public IRelayCommand<LocationRowDto?> CopyGeoCitationCommand { get; }

    public IRelayCommand<IncidentWindowCoLocationCandidateDto?> ViewCoLocationSourceCommand { get; }

    public IRelayCommand<IncidentWindowCoLocationCandidateDto?> ViewCoLocationSecondarySourceCommand { get; }

    public IRelayCommand<IncidentWindowCoLocationCandidateDto?> CopyCoLocationCitationCommand { get; }

    [ObservableProperty]
    private Guid? currentCaseId;

    [ObservableProperty]
    private string anchorLocalText = string.Empty;

    [ObservableProperty]
    private string startLocalText = string.Empty;

    [ObservableProperty]
    private string endLocalText = string.Empty;

    [ObservableProperty]
    private bool radiusEnabled;

    [ObservableProperty]
    private string centerLatitudeText = string.Empty;

    [ObservableProperty]
    private string centerLongitudeText = string.Empty;

    [ObservableProperty]
    private string radiusMetersText = "250";

    [ObservableProperty]
    private IncidentWindowSubjectFilterOption? selectedSubjectFilter;

    [ObservableProperty]
    private bool includeCoLocationCandidates = true;

    [ObservableProperty]
    private TimelineRowDto? selectedCommsRow;

    [ObservableProperty]
    private LocationRowDto? selectedGeoRow;

    [ObservableProperty]
    private IncidentWindowCoLocationCandidateDto? selectedCoLocation;

    [ObservableProperty]
    private bool isLoading;

    [ObservableProperty]
    private string statusText = "Open a case to run Incident Window.";

    [ObservableProperty]
    private int commsCurrentPage = 1;

    [ObservableProperty]
    private int commsTotalCount;

    [ObservableProperty]
    private int geoCurrentPage = 1;

    [ObservableProperty]
    private int geoTotalCount;

    [ObservableProperty]
    private int coLocationCurrentPage = 1;

    [ObservableProperty]
    private int coLocationTotalCount;

    [ObservableProperty]
    private ReadinessBannerState maintenanceBanner = ReadinessBannerState.Hidden;

    public async Task SetCurrentCaseAsync(Guid? caseId, CancellationToken ct)
    {
        CurrentCaseId = caseId;
        CancelLoad();
        _hasExecuted = false;
        ClearResults();

        if (!caseId.HasValue)
        {
            SubjectFilters.Clear();
            SubjectFilters.Add(IncidentWindowSubjectFilterOption.AllSubjects);
            SelectedSubjectFilter = IncidentWindowSubjectFilterOption.AllSubjects;
            ResetInputsToDefaults();
            StatusText = "Open a case to run Incident Window.";
            UpdateMaintenanceBanner();
            return;
        }

        ResetInputsToDefaults();
        UpdateMaintenanceBanner();
        if (_isActive)
        {
            await LoadFeatureContextAsync(ct);
        }
        else
        {
            StatusText = "Incident Window ready. Open the page to prepare filters.";
        }
    }

    public async Task ActivateAsync(CancellationToken ct)
    {
        await _performanceInstrumentation.TrackAsync(
            new PerformanceOperationContext(
                PerformanceOperationKinds.FeatureOpen,
                "Activate",
                FeatureName: ReadinessFeature.IncidentWindow.ToString(),
                CaseId: CurrentCaseId
            ),
            async innerCt =>
            {
                _isActive = true;
                if (!CurrentCaseId.HasValue)
                {
                    StatusText = "Open a case to run Incident Window.";
                    return;
                }

                await LoadFeatureContextAsync(innerCt);
                if (!_hasExecuted)
                {
                    StatusText = "Incident Window ready. Adjust the window and run it.";
                }
            },
            ct
        );
    }

    public void Deactivate()
    {
        _isActive = false;
        CancelLoad();
    }

    private async Task RunAsync()
    {
        _hasExecuted = true;
        await LoadResultsAsync(1, 1, 1, writeAudit: true, CancellationToken.None);
    }

    private void Clear()
    {
        CancelLoad();
        _hasExecuted = false;
        ClearResults();
        ResetInputsToDefaults();
        StatusText = CurrentCaseId.HasValue
            ? "Incident Window cleared."
            : "Open a case to run Incident Window.";
    }

    private void ApplyPresetWindow()
    {
        if (!TryParseAnchor(out var anchorUtc, out var error))
        {
            StatusText = error;
            return;
        }

        SetAnchorAndWindow(anchorUtc.ToLocalTime());
        StatusText = "Preset window applied.";
    }

    private Task LoadNextCommsPageAsync() => LoadResultsAsync(CommsCurrentPage + 1, GeoCurrentPage, CoLocationCurrentPage, false, CancellationToken.None);
    private Task LoadPreviousCommsPageAsync() => LoadResultsAsync(Math.Max(CommsCurrentPage - 1, 1), GeoCurrentPage, CoLocationCurrentPage, false, CancellationToken.None);
    private Task LoadNextGeoPageAsync() => LoadResultsAsync(CommsCurrentPage, GeoCurrentPage + 1, CoLocationCurrentPage, false, CancellationToken.None);
    private Task LoadPreviousGeoPageAsync() => LoadResultsAsync(CommsCurrentPage, Math.Max(GeoCurrentPage - 1, 1), CoLocationCurrentPage, false, CancellationToken.None);
    private Task LoadNextCoLocationPageAsync() => LoadResultsAsync(CommsCurrentPage, GeoCurrentPage, CoLocationCurrentPage + 1, false, CancellationToken.None);
    private Task LoadPreviousCoLocationPageAsync() => LoadResultsAsync(CommsCurrentPage, GeoCurrentPage, Math.Max(CoLocationCurrentPage - 1, 1), false, CancellationToken.None);

    private async Task LoadResultsAsync(
        int requestedCommsPage,
        int requestedGeoPage,
        int requestedCoLocationPage,
        bool writeAudit,
        CancellationToken outerCt
    )
    {
        if (!CurrentCaseId.HasValue)
        {
            StatusText = "Open a case to run Incident Window.";
            return;
        }

        if (!TryBuildRequest(requestedCommsPage, requestedGeoPage, requestedCoLocationPage, writeAudit, out var request, out var validationError))
        {
            StatusText = validationError;
            return;
        }

        var loadCts = BeginLoad(outerCt);
        IsLoading = true;
        StatusText = writeAudit ? "Running Incident Window..." : "Loading Incident Window page...";

        try
        {
            var result = await _queryService.ExecuteAsync(request, loadCts.Token);
            if (!ReferenceEquals(_loadCts, loadCts))
            {
                return;
            }

            CommsCurrentPage = requestedCommsPage;
            GeoCurrentPage = requestedGeoPage;
            CoLocationCurrentPage = requestedCoLocationPage;
            ApplyResults(result);
            StatusText =
                $"Incident Window loaded {result.Comms.TotalCount:0} comms hit(s), "
                + $"{result.Geo.TotalCount:0} geo hit(s), "
                + $"{result.CoLocation.TotalCount:0} co-location candidate(s).";
        }
        catch (OperationCanceledException) when (loadCts.IsCancellationRequested)
        {
            if (ReferenceEquals(_loadCts, loadCts))
            {
                StatusText = "Incident Window load canceled.";
            }
        }
        catch (ArgumentException ex)
        {
            if (ReferenceEquals(_loadCts, loadCts))
            {
                StatusText = ex.Message;
            }
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
            SubjectFilters.Add(IncidentWindowSubjectFilterOption.AllSubjects);
            SelectedSubjectFilter = IncidentWindowSubjectFilterOption.AllSubjects;
            return;
        }

        var selectedType = SelectedSubjectFilter?.SubjectType;
        var selectedId = SelectedSubjectFilter?.SubjectId;
        var targets = await _targetRegistryService.GetTargetsAsync(CurrentCaseId.Value, search: null, ct);

        SubjectFilters.Clear();
        SubjectFilters.Add(IncidentWindowSubjectFilterOption.AllSubjects);

        foreach (var target in targets.OrderBy(item => item.DisplayName, StringComparer.OrdinalIgnoreCase))
        {
            SubjectFilters.Add(new IncidentWindowSubjectFilterOption(
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
                         return new IncidentWindowSubjectFilterOption("GlobalPerson", group.Key, $"Global: {display}");
                     })
                     .OrderBy(item => item.DisplayName, StringComparer.OrdinalIgnoreCase))
        {
            SubjectFilters.Add(global);
        }

        SelectedSubjectFilter = !string.IsNullOrWhiteSpace(selectedType) && selectedId.HasValue
            ? SubjectFilters.FirstOrDefault(item =>
                string.Equals(item.SubjectType, selectedType, StringComparison.OrdinalIgnoreCase)
                && item.SubjectId == selectedId)
            : IncidentWindowSubjectFilterOption.AllSubjects;
        if (SelectedSubjectFilter is null)
        {
            SelectedSubjectFilter = IncidentWindowSubjectFilterOption.AllSubjects;
        }
    }

    private async Task LoadFeatureContextAsync(CancellationToken ct)
    {
        await _featureReadinessService.EnsureReadyAsync(
            ReadinessFeature.IncidentWindow,
            CurrentCaseId,
            requiresMessageSearchIndex: false,
            CreateReadinessProgress(),
            ct
        );
        await RefreshSubjectFiltersAsync(ct);
    }

    private void OnMaintenanceSnapshotChanged(object? sender, MaintenanceTaskSnapshot snapshot)
    {
        if (!CurrentCaseId.HasValue || snapshot.CaseId != CurrentCaseId.Value)
        {
            return;
        }

        DispatchToUi(UpdateMaintenanceBanner);
    }

    private void UpdateMaintenanceBanner()
    {
        var snapshot = CurrentCaseId.HasValue
            ? _backgroundMaintenanceManager.GetSnapshot(MaintenanceTaskKeys.MessageSearchIndex(CurrentCaseId.Value))
            : null;
        MaintenanceBanner = ReadinessBannerStateFactory.FromMaintenance(
            ReadinessFeature.IncidentWindow,
            snapshot,
            blocksCurrentAction: false
        );
    }

    private void ResetInputsToDefaults()
    {
        RadiusEnabled = false;
        CenterLatitudeText = string.Empty;
        CenterLongitudeText = string.Empty;
        RadiusMetersText = "250";
        IncludeCoLocationCandidates = true;
        SelectedSubjectFilter = SubjectFilters.FirstOrDefault() ?? IncidentWindowSubjectFilterOption.AllSubjects;
        SetAnchorAndWindow(DateTimeOffset.Now);
    }

    private void SetAnchorAndWindow(DateTimeOffset localAnchor)
    {
        AnchorLocalText = FormatLocalInput(localAnchor);
        StartLocalText = FormatLocalInput(localAnchor.AddHours(-6));
        EndLocalText = FormatLocalInput(localAnchor.AddHours(12));
    }

    private void ClearResults()
    {
        CommsRows.Clear();
        GeoRows.Clear();
        CoLocationRows.Clear();
        SelectedCommsRow = null;
        SelectedGeoRow = null;
        SelectedCoLocation = null;
        CommsCurrentPage = 1;
        GeoCurrentPage = 1;
        CoLocationCurrentPage = 1;
        CommsTotalCount = 0;
        GeoTotalCount = 0;
        CoLocationTotalCount = 0;
        RaisePagingStateChanged();
    }

    private void ApplyResults(IncidentWindowQueryResult result)
    {
        ReplaceRows(CommsRows, result.Comms.Rows);
        ReplaceRows(GeoRows, result.Geo.Rows);
        ReplaceRows(CoLocationRows, result.CoLocation.Rows);
        SelectedCommsRow = CommsRows.FirstOrDefault();
        SelectedGeoRow = GeoRows.FirstOrDefault();
        SelectedCoLocation = CoLocationRows.FirstOrDefault();
        CommsTotalCount = result.Comms.TotalCount;
        GeoTotalCount = result.Geo.TotalCount;
        CoLocationTotalCount = result.CoLocation.TotalCount;
        RaisePagingStateChanged();
    }

    private static void ReplaceRows<T>(
        ObservableCollection<T> destination,
        IReadOnlyList<T> source
    )
    {
        destination.Clear();
        foreach (var row in source)
        {
            destination.Add(row);
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
        RaisePagingStateChanged();
    }

    private void CancelLoad()
    {
        var loadCts = _loadCts;
        _loadCts = null;
        loadCts?.Cancel();
        loadCts?.Dispose();
        IsLoading = false;
        RaisePagingStateChanged();
    }

    private bool TryBuildRequest(
        int requestedCommsPage,
        int requestedGeoPage,
        int requestedCoLocationPage,
        bool writeAudit,
        out IncidentWindowQueryRequest request,
        out string error
    )
    {
        request = null!;
        error = string.Empty;

        if (!TryParseRequiredLocalDateTime(StartLocalText, "Start", out var startUtc, out error)
            || !TryParseRequiredLocalDateTime(EndLocalText, "End", out var endUtc, out error))
        {
            return false;
        }

        double? latitude = null;
        double? longitude = null;
        double? radiusMeters = null;
        if (RadiusEnabled)
        {
            if (!TryParseNullableDouble(CenterLatitudeText, out latitude)
                || !TryParseNullableDouble(CenterLongitudeText, out longitude)
                || !TryParseNullableDouble(RadiusMetersText, out radiusMeters)
                || !latitude.HasValue
                || !longitude.HasValue
                || !radiusMeters.HasValue)
            {
                error = "Center latitude, center longitude, and radius meters are required when radius filtering is enabled.";
                return false;
            }
        }

        request = new IncidentWindowQueryRequest(
            CaseId: CurrentCaseId!.Value,
            StartUtc: startUtc,
            EndUtc: endUtc,
            RadiusEnabled: RadiusEnabled,
            CenterLatitude: latitude,
            CenterLongitude: longitude,
            RadiusMeters: radiusMeters,
            SubjectType: SelectedSubjectFilter?.SubjectType,
            SubjectId: SelectedSubjectFilter?.SubjectId,
            IncludeCoLocationCandidates: IncludeCoLocationCandidates,
            CommsTake: PageSize,
            CommsSkip: (requestedCommsPage - 1) * PageSize,
            GeoTake: PageSize,
            GeoSkip: (requestedGeoPage - 1) * PageSize,
            CoLocationTake: PageSize,
            CoLocationSkip: (requestedCoLocationPage - 1) * PageSize,
            CoLocationDistanceMeters: 100d,
            CoLocationTimeWindowMinutes: 10,
            CorrelationId: AppFileLogger.NewCorrelationId(),
            WriteAuditEvent: writeAudit
        );
        return true;
    }

    private bool TryParseAnchor(out DateTimeOffset anchorUtc, out string error)
    {
        if (string.IsNullOrWhiteSpace(AnchorLocalText))
        {
            anchorUtc = DateTimeOffset.Now.ToUniversalTime();
            error = string.Empty;
            return true;
        }

        return TryParseRequiredLocalDateTime(AnchorLocalText, "Anchor", out anchorUtc, out error);
    }

    private static bool TryParseRequiredLocalDateTime(
        string text,
        string label,
        out DateTimeOffset value,
        out string error
    )
    {
        if (TryParseLocalDateTime(text, out value))
        {
            error = string.Empty;
            return true;
        }

        error = $"{label} time is required. Use a local timestamp like 2026-03-01 14:30.";
        return false;
    }

    private static bool TryParseLocalDateTime(string text, out DateTimeOffset value)
    {
        value = default;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var normalized = text.Trim();
        var formats = new[] { "yyyy-MM-dd HH:mm", "yyyy-MM-dd H:mm", "yyyy-MM-ddTHH:mm" };
        if (DateTimeOffset.TryParseExact(
                normalized,
                formats,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeLocal | DateTimeStyles.AllowWhiteSpaces,
                out var parsed)
            || DateTimeOffset.TryParse(
                normalized,
                CultureInfo.CurrentCulture,
                DateTimeStyles.AssumeLocal | DateTimeStyles.AllowWhiteSpaces,
                out parsed)
            || DateTimeOffset.TryParse(
                normalized,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeLocal | DateTimeStyles.AllowWhiteSpaces,
                out parsed))
        {
            value = parsed.ToUniversalTime();
            return true;
        }

        return false;
    }

    private static bool TryParseNullableDouble(string text, out double? value)
    {
        value = null;
        if (string.IsNullOrWhiteSpace(text))
        {
            return true;
        }

        var normalized = text.Trim();
        if (double.TryParse(normalized, NumberStyles.Float | NumberStyles.AllowThousands, CultureInfo.InvariantCulture, out var parsed)
            || double.TryParse(normalized, NumberStyles.Float | NumberStyles.AllowThousands, CultureInfo.CurrentCulture, out parsed))
        {
            value = parsed;
            return true;
        }

        return false;
    }

    private static string FormatLocalInput(DateTimeOffset value)
    {
        return value.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
    }

    private static string BuildPageSummary(string noun, int currentPage, int totalCount)
    {
        if (totalCount <= 0)
        {
            return $"No {noun}s.";
        }

        var start = ((currentPage - 1) * 100) + 1;
        var end = Math.Min(currentPage * 100, totalCount);
        return $"Showing {start:0}-{end:0} of {totalCount:0}";
    }

    private IProgress<ReadinessProgress> CreateReadinessProgress()
    {
        return new Progress<ReadinessProgress>(update =>
        {
            StatusText = update.DetailText;
        });
    }

    private void RaisePagingStateChanged()
    {
        NextCommsPageCommand.NotifyCanExecuteChanged();
        PreviousCommsPageCommand.NotifyCanExecuteChanged();
        NextGeoPageCommand.NotifyCanExecuteChanged();
        PreviousGeoPageCommand.NotifyCanExecuteChanged();
        NextCoLocationPageCommand.NotifyCanExecuteChanged();
        PreviousCoLocationPageCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(CanGoCommsPrevious));
        OnPropertyChanged(nameof(CanGoCommsNext));
        OnPropertyChanged(nameof(CanGoGeoPrevious));
        OnPropertyChanged(nameof(CanGoGeoNext));
        OnPropertyChanged(nameof(CanGoCoLocationPrevious));
        OnPropertyChanged(nameof(CanGoCoLocationNext));
        OnPropertyChanged(nameof(CommsPageSummaryText));
        OnPropertyChanged(nameof(GeoPageSummaryText));
        OnPropertyChanged(nameof(CoLocationPageSummaryText));
        OnPropertyChanged(nameof(CoLocationHintText));
    }

    private void ViewCommsSource(TimelineRowDto? row)
    {
        var selected = row ?? SelectedCommsRow;
        if (selected is null)
        {
            return;
        }

        StatusText = $"Opening source for {selected.SourceLocator}...";
        ViewCommsSourceRequested?.Invoke(selected);
    }

    private void CopyCommsCitation(TimelineRowDto? row)
    {
        var selected = row ?? SelectedCommsRow;
        if (selected is null)
        {
            return;
        }

        _userInteractionService.CopyToClipboard(selected.Citation);
        StatusText = "Citation copied.";
    }

    private void ViewGeoSource(LocationRowDto? row)
    {
        var selected = row ?? SelectedGeoRow;
        if (selected is null)
        {
            return;
        }

        StatusText = $"Opening source for {selected.SourceLocator}...";
        ViewGeoSourceRequested?.Invoke(selected);
    }

    private void CopyGeoCitation(LocationRowDto? row)
    {
        var selected = row ?? SelectedGeoRow;
        if (selected is null)
        {
            return;
        }

        _userInteractionService.CopyToClipboard(selected.Citation);
        StatusText = "Citation copied.";
    }

    private void ViewCoLocationPrimarySource(IncidentWindowCoLocationCandidateDto? candidate)
    {
        var selected = candidate ?? SelectedCoLocation;
        if (selected is null)
        {
            return;
        }

        StatusText = $"Opening source for {selected.FirstObservation.SourceLocator}...";
        ViewCoLocationSourceRequested?.Invoke(selected.FirstObservation);
    }

    private void ViewCoLocationSecondarySource(IncidentWindowCoLocationCandidateDto? candidate)
    {
        var selected = candidate ?? SelectedCoLocation;
        if (selected is null)
        {
            return;
        }

        StatusText = $"Opening source for {selected.SecondObservation.SourceLocator}...";
        ViewCoLocationSourceRequested?.Invoke(selected.SecondObservation);
    }

    private void CopyCoLocationCitation(IncidentWindowCoLocationCandidateDto? candidate)
    {
        var selected = candidate ?? SelectedCoLocation;
        if (selected is null)
        {
            return;
        }

        _userInteractionService.CopyToClipboard(selected.Citation);
        StatusText = "Citation copied.";
    }

    partial void OnIsLoadingChanged(bool value) => RaisePagingStateChanged();

    partial void OnRadiusEnabledChanged(bool value) => OnPropertyChanged(nameof(CoLocationHintText));

    partial void OnIncludeCoLocationCandidatesChanged(bool value) => OnPropertyChanged(nameof(CoLocationHintText));

    public void Dispose()
    {
        if (_isDisposed)
        {
            return;
        }

        _isDisposed = true;
        _backgroundMaintenanceManager.SnapshotChanged -= OnMaintenanceSnapshotChanged;
        CancelLoad();
    }

    private static void DispatchToUi(Action action)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess())
        {
            action();
            return;
        }

        dispatcher.BeginInvoke(DispatcherPriority.Background, action);
    }

    public sealed record IncidentWindowSubjectFilterOption(string? SubjectType, Guid? SubjectId, string DisplayName)
    {
        public static IncidentWindowSubjectFilterOption AllSubjects { get; } = new(null, null, "Any subject");
    }
}
