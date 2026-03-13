using CaseGraph.App.Services;
using CaseGraph.Core.Diagnostics;
using CaseGraph.Infrastructure.Diagnostics;
using CaseGraph.Infrastructure.Incidents;
using CaseGraph.Infrastructure.Locations;
using CaseGraph.Infrastructure.Timeline;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System.Collections.ObjectModel;
using System.Globalization;

namespace CaseGraph.App.ViewModels;

public partial class OpenIncidentWorkspaceViewModel : ObservableObject, IDisposable
{
    private readonly IFeatureReadinessService _featureReadinessService;
    private readonly IIncidentService _incidentService;
    private readonly IUserInteractionService _userInteractionService;
    private readonly IPerformanceInstrumentation _performanceInstrumentation;

    private CancellationTokenSource? _loadCts;
    private bool _isActive;
    private bool _isDisposed;
    private Guid? _resultIncidentId;

    public OpenIncidentWorkspaceViewModel(
        IFeatureReadinessService featureReadinessService,
        IIncidentService incidentService,
        IUserInteractionService userInteractionService,
        IPerformanceInstrumentation? performanceInstrumentation = null
    )
    {
        _featureReadinessService = featureReadinessService;
        _incidentService = incidentService;
        _userInteractionService = userInteractionService;
        _performanceInstrumentation = performanceInstrumentation
            ?? new PerformanceInstrumentation(new PerformanceBudgetOptions(), TimeProvider.System);

        NewIncidentCommand = new RelayCommand(NewIncident);
        RefreshIncidentsCommand = new AsyncRelayCommand(() => RefreshIncidentsAsync(null, CancellationToken.None));
        SaveIncidentCommand = new AsyncRelayCommand(SaveIncidentAsync);
        RunCrossReferenceCommand = new AsyncRelayCommand(RunCrossReferenceAsync);
        AddLocationCommand = new RelayCommand(AddLocation);
        RemoveLocationCommand = new RelayCommand<IncidentLocationEditor?>(RemoveLocation);
        ViewMessageSourceCommand = new RelayCommand<TimelineRowDto?>(ViewMessageSource);
        CopyMessageCitationCommand = new RelayCommand<TimelineRowDto?>(CopyMessageCitation);
        PinMessageCommand = new AsyncRelayCommand<TimelineRowDto?>(PinMessageAsync);
        ViewLocationSourceCommand = new RelayCommand<IncidentLocationHit?>(ViewLocationSource);
        CopyLocationCitationCommand = new RelayCommand<IncidentLocationHit?>(CopyLocationCitation);
        PinLocationCommand = new AsyncRelayCommand<IncidentLocationHit?>(PinLocationAsync);
        ViewPinnedSourceCommand = new RelayCommand<IncidentPinnedResult?>(ViewPinnedSource);
        CopyPinnedCitationCommand = new RelayCommand<IncidentPinnedResult?>(CopyPinnedCitation);

        ResetEditor();
    }

    public ObservableCollection<IncidentRecord> Incidents { get; } = new();

    public ObservableCollection<IncidentLocationEditor> LocationEditors { get; } = new();

    public ObservableCollection<TimelineRowDto> MessageResults { get; } = new();

    public ObservableCollection<IncidentLocationHit> LocationResults { get; } = new();

    public ObservableCollection<IncidentTimelineItem> TimelineItems { get; } = new();

    public ObservableCollection<IncidentPinnedResult> PinnedResults { get; } = new();

    public Action<TimelineRowDto>? ViewMessageSourceRequested { get; set; }

    public Action<LocationRowDto>? ViewLocationSourceRequested { get; set; }

    public IRelayCommand NewIncidentCommand { get; }

    public IAsyncRelayCommand RefreshIncidentsCommand { get; }

    public IAsyncRelayCommand SaveIncidentCommand { get; }

    public IAsyncRelayCommand RunCrossReferenceCommand { get; }

    public IRelayCommand AddLocationCommand { get; }

    public IRelayCommand<IncidentLocationEditor?> RemoveLocationCommand { get; }

    public IRelayCommand<TimelineRowDto?> ViewMessageSourceCommand { get; }

    public IRelayCommand<TimelineRowDto?> CopyMessageCitationCommand { get; }

    public IAsyncRelayCommand<TimelineRowDto?> PinMessageCommand { get; }

    public IRelayCommand<IncidentLocationHit?> ViewLocationSourceCommand { get; }

    public IRelayCommand<IncidentLocationHit?> CopyLocationCitationCommand { get; }

    public IAsyncRelayCommand<IncidentLocationHit?> PinLocationCommand { get; }

    public IRelayCommand<IncidentPinnedResult?> ViewPinnedSourceCommand { get; }

    public IRelayCommand<IncidentPinnedResult?> CopyPinnedCitationCommand { get; }

    [ObservableProperty]
    private Guid? currentCaseId;

    [ObservableProperty]
    private IncidentRecord? selectedIncident;

    [ObservableProperty]
    private string titleText = string.Empty;

    [ObservableProperty]
    private string incidentTypeText = "Shots Fired";

    [ObservableProperty]
    private string statusValue = "Open";

    [ObservableProperty]
    private string primaryOccurrenceLocalText = string.Empty;

    [ObservableProperty]
    private string offenseWindowStartLocalText = string.Empty;

    [ObservableProperty]
    private string offenseWindowEndLocalText = string.Empty;

    [ObservableProperty]
    private string summaryNotes = string.Empty;

    [ObservableProperty]
    private IncidentLocationEditor? selectedLocationEditor;

    [ObservableProperty]
    private TimelineRowDto? selectedMessageResult;

    [ObservableProperty]
    private IncidentLocationHit? selectedLocationResult;

    [ObservableProperty]
    private IncidentTimelineItem? selectedTimelineItem;

    [ObservableProperty]
    private IncidentPinnedResult? selectedPinnedResult;

    [ObservableProperty]
    private bool isBusy;

    [ObservableProperty]
    private string statusText = "Open a case to use Open Incident Workspace.";

    public async Task SetCurrentCaseAsync(Guid? caseId, CancellationToken ct)
    {
        CurrentCaseId = caseId;
        CancelLoad();
        ReplaceRows(Incidents, Array.Empty<IncidentRecord>());
        ClearResults();

        if (!caseId.HasValue)
        {
            ResetEditor();
            StatusText = "Open a case to use Open Incident Workspace.";
            return;
        }

        ResetEditor();
        if (_isActive)
        {
            await RefreshIncidentsAsync(null, ct);
        }
        else
        {
            StatusText = "Open Incident Workspace ready. Open the page to review or create an incident.";
        }
    }

    public async Task ActivateAsync(CancellationToken ct)
    {
        _isActive = true;
        if (!CurrentCaseId.HasValue)
        {
            StatusText = "Open a case to use Open Incident Workspace.";
            return;
        }

        await _performanceInstrumentation.TrackAsync(
            new PerformanceOperationContext(
                PerformanceOperationKinds.FeatureOpen,
                "Activate",
                FeatureName: "OpenIncidentWorkspace",
                CaseId: CurrentCaseId
            ),
            async innerCt =>
            {
                await _featureReadinessService.EnsureReadyAsync(
                    ReadinessFeature.IncidentWindow,
                    CurrentCaseId,
                    requiresMessageSearchIndex: false,
                    CreateReadinessProgress(),
                    innerCt
                );
                await RefreshIncidentsAsync(null, innerCt);
            },
            ct
        );
    }

    public void Deactivate()
    {
        _isActive = false;
        CancelLoad();
    }

    private void NewIncident()
    {
        SelectedIncident = null;
        ResetEditor();
        ClearResults();
        StatusText = CurrentCaseId.HasValue
            ? "New incident draft ready."
            : "Open a case to create an incident.";
    }

    private async Task SaveIncidentAsync()
    {
        if (!CurrentCaseId.HasValue)
        {
            StatusText = "Open a case before saving an incident.";
            return;
        }

        try
        {
            var incident = BuildIncident();
            var correlationId = AppFileLogger.NewCorrelationId();
            IsBusy = true;
            StatusText = "Saving incident...";
            var saved = await _incidentService.SaveIncidentAsync(incident, correlationId, CancellationToken.None);
            ApplySavedIncident(saved);
            await RefreshIncidentsAsync(saved.IncidentId, CancellationToken.None);
            StatusText = $"Saved incident '{saved.Title}'.";
        }
        catch (Exception ex)
        {
            StatusText = ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task RunCrossReferenceAsync()
    {
        if (!CurrentCaseId.HasValue)
        {
            StatusText = "Open a case before running cross-reference.";
            return;
        }

        try
        {
            if (ResolveIncidentId() == Guid.Empty)
            {
                await SaveIncidentAsync();
            }

            var incidentId = ResolveIncidentId();
            if (incidentId == Guid.Empty)
            {
                return;
            }

            IsBusy = true;
            StatusText = "Running incident cross-reference...";
            var result = await _incidentService.RunCrossReferenceAsync(
                CurrentCaseId.Value,
                incidentId,
                AppFileLogger.NewCorrelationId(),
                CancellationToken.None
            );
            ApplySavedIncident(result.Incident);
            ReplaceRows(MessageResults, result.MessageResults);
            ReplaceRows(LocationResults, result.LocationResults);
            ReplaceRows(TimelineItems, result.TimelineItems);
            SelectedMessageResult = MessageResults.FirstOrDefault();
            SelectedLocationResult = LocationResults.FirstOrDefault();
            SelectedTimelineItem = TimelineItems.FirstOrDefault();
            _resultIncidentId = result.Incident.IncidentId;
            StatusText =
                $"Cross-reference returned {result.MessageResults.Count:0} message hit(s), "
                + $"{result.LocationResults.Count:0} location hit(s), and "
                + $"{result.TimelineItems.Count:0} timeline item(s).";
        }
        catch (Exception ex)
        {
            StatusText = ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task RefreshIncidentsAsync(Guid? selectIncidentId, CancellationToken outerCt)
    {
        if (!CurrentCaseId.HasValue)
        {
            return;
        }

        var loadCts = BeginLoad(outerCt);
        try
        {
            IsBusy = true;
            StatusText = "Loading incidents...";
            var incidents = await _incidentService.GetIncidentsAsync(CurrentCaseId.Value, loadCts.Token);
            if (!ReferenceEquals(_loadCts, loadCts))
            {
                return;
            }

            ReplaceRows(Incidents, incidents);
            var selected = selectIncidentId.HasValue
                ? Incidents.FirstOrDefault(item => item.IncidentId == selectIncidentId.Value)
                : SelectedIncident is not null
                    ? Incidents.FirstOrDefault(item => item.IncidentId == SelectedIncident.IncidentId)
                    : Incidents.FirstOrDefault();
            if (selected is not null)
            {
                SelectedIncident = selected;
            }
            else if (Incidents.Count == 0)
            {
                SelectedIncident = null;
                ResetEditor();
            }

            StatusText = Incidents.Count == 0
                ? "No open incidents saved yet."
                : $"Loaded {Incidents.Count:0} incident(s).";
        }
        catch (OperationCanceledException) when (loadCts.IsCancellationRequested)
        {
            StatusText = "Incident load canceled.";
        }
        finally
        {
            EndLoad(loadCts);
        }
    }

    private void AddLocation()
    {
        LocationEditors.Add(new IncidentLocationEditor
        {
            Label = $"Scene {LocationEditors.Count + 1}",
            RadiusMetersText = "250"
        });
        SelectedLocationEditor = LocationEditors.LastOrDefault();
    }

    private void RemoveLocation(IncidentLocationEditor? editor)
    {
        var target = editor ?? SelectedLocationEditor;
        if (target is null)
        {
            return;
        }

        LocationEditors.Remove(target);
        SelectedLocationEditor = LocationEditors.FirstOrDefault();
    }

    private void ViewMessageSource(TimelineRowDto? row)
    {
        var target = row ?? SelectedMessageResult;
        if (target is null)
        {
            return;
        }

        ViewMessageSourceRequested?.Invoke(target);
        StatusText = $"Opening source for {target.SourceLocator}...";
    }

    private void CopyMessageCitation(TimelineRowDto? row)
    {
        var target = row ?? SelectedMessageResult;
        if (target is null)
        {
            return;
        }

        _userInteractionService.CopyToClipboard(target.Citation);
        StatusText = "Message citation copied.";
    }

    private async Task PinMessageAsync(TimelineRowDto? row)
    {
        var target = row ?? SelectedMessageResult;
        if (target is null || !CurrentCaseId.HasValue)
        {
            return;
        }

        var incidentId = ResolveIncidentId();
        if (incidentId == Guid.Empty)
        {
            StatusText = "Save the incident before pinning results.";
            return;
        }

        IsBusy = true;
        try
        {
            var saved = await _incidentService.PinMessageAsync(
                CurrentCaseId.Value,
                incidentId,
                target,
                AppFileLogger.NewCorrelationId(),
                CancellationToken.None
            );
            ApplySavedIncident(saved);
            await RefreshIncidentsAsync(saved.IncidentId, CancellationToken.None);
            StatusText = "Pinned message result to the incident.";
        }
        catch (Exception ex)
        {
            StatusText = ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void ViewLocationSource(IncidentLocationHit? hit)
    {
        var target = hit ?? SelectedLocationResult;
        if (target is null)
        {
            return;
        }

        ViewLocationSourceRequested?.Invoke(target.Location);
        StatusText = $"Opening source for {target.Location.SourceLocator}...";
    }

    private void CopyLocationCitation(IncidentLocationHit? hit)
    {
        var target = hit ?? SelectedLocationResult;
        if (target is null)
        {
            return;
        }

        _userInteractionService.CopyToClipboard(target.Location.Citation);
        StatusText = "Location citation copied.";
    }

    private async Task PinLocationAsync(IncidentLocationHit? hit)
    {
        var target = hit ?? SelectedLocationResult;
        if (target is null || !CurrentCaseId.HasValue)
        {
            return;
        }

        var incidentId = ResolveIncidentId();
        if (incidentId == Guid.Empty)
        {
            StatusText = "Save the incident before pinning results.";
            return;
        }

        IsBusy = true;
        try
        {
            var saved = await _incidentService.PinLocationAsync(
                CurrentCaseId.Value,
                incidentId,
                target,
                AppFileLogger.NewCorrelationId(),
                CancellationToken.None
            );
            ApplySavedIncident(saved);
            await RefreshIncidentsAsync(saved.IncidentId, CancellationToken.None);
            StatusText = "Pinned location result to the incident.";
        }
        catch (Exception ex)
        {
            StatusText = ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void ViewPinnedSource(IncidentPinnedResult? result)
    {
        var target = result ?? SelectedPinnedResult;
        if (target is null)
        {
            return;
        }

        if (string.Equals(target.ResultType, "Message", StringComparison.OrdinalIgnoreCase))
        {
            ViewMessageSourceRequested?.Invoke(new TimelineRowDto(
                MessageEventId: target.SourceRecordId,
                CaseId: CurrentCaseId ?? Guid.Empty,
                SourceEvidenceItemId: target.SourceEvidenceItemId,
                EventType: "Message",
                TimestampUtc: target.EventUtc,
                Direction: "Unknown",
                ParticipantsSummary: target.Title,
                Preview: target.Summary,
                SourceLocator: target.SourceLocator,
                IngestModuleVersion: string.Empty
            ));
        }
        else
        {
            ViewLocationSourceRequested?.Invoke(new LocationRowDto(
                LocationObservationId: target.SourceRecordId,
                CaseId: CurrentCaseId ?? Guid.Empty,
                ObservedUtc: target.EventUtc ?? DateTimeOffset.UnixEpoch,
                Latitude: target.Latitude ?? 0d,
                Longitude: target.Longitude ?? 0d,
                AccuracyMeters: null,
                AltitudeMeters: null,
                SpeedMps: null,
                HeadingDegrees: null,
                SourceType: string.Empty,
                SourceLabel: target.SceneLabel,
                SubjectType: null,
                SubjectId: null,
                SourceEvidenceItemId: target.SourceEvidenceItemId,
                SourceLocator: target.SourceLocator,
                IngestModuleVersion: string.Empty
            ));
        }

        StatusText = $"Opening pinned source for {target.SourceLocator}...";
    }

    private void CopyPinnedCitation(IncidentPinnedResult? result)
    {
        var target = result ?? SelectedPinnedResult;
        if (target is null)
        {
            return;
        }

        _userInteractionService.CopyToClipboard(target.Citation);
        StatusText = "Pinned citation copied.";
    }

    private IncidentRecord BuildIncident()
    {
        if (!CurrentCaseId.HasValue)
        {
            throw new InvalidOperationException("A case must be open.");
        }

        return new IncidentRecord(
            IncidentId: ResolveIncidentId(),
            CaseId: CurrentCaseId.Value,
            Title: TitleText,
            IncidentType: IncidentTypeText,
            Status: StatusValue,
            SummaryNotes: SummaryNotes,
            PrimaryOccurrenceUtc: TryParseOptionalLocalDateTime(PrimaryOccurrenceLocalText),
            OffenseWindowStartUtc: ParseRequiredLocalDateTime(OffenseWindowStartLocalText, "Offense window start"),
            OffenseWindowEndUtc: ParseRequiredLocalDateTime(OffenseWindowEndLocalText, "Offense window end"),
            CreatedUtc: SelectedIncident?.CreatedUtc ?? DateTimeOffset.UtcNow,
            UpdatedUtc: SelectedIncident?.UpdatedUtc ?? DateTimeOffset.UtcNow,
            Locations: LocationEditors.Select((item, index) => new IncidentLocation(
                IncidentLocationId: item.IncidentLocationId,
                SortOrder: index,
                Label: item.Label,
                Latitude: ParseDouble(item.LatitudeText, $"{item.Label} latitude"),
                Longitude: ParseDouble(item.LongitudeText, $"{item.Label} longitude"),
                RadiusMeters: ParseDouble(item.RadiusMetersText, $"{item.Label} radius"),
                Notes: item.Notes
            )).ToList(),
            PinnedResults: SelectedIncident?.PinnedResults ?? Array.Empty<IncidentPinnedResult>()
        );
    }

    private Guid ResolveIncidentId() => SelectedIncident?.IncidentId ?? Guid.Empty;

    private void ApplySavedIncident(IncidentRecord incident)
    {
        SelectedIncident = incident;
        LoadEditorFromIncident(incident);
        ReplaceRows(PinnedResults, incident.PinnedResults);
        SelectedPinnedResult = PinnedResults.FirstOrDefault();
    }

    private void LoadEditorFromIncident(IncidentRecord incident)
    {
        TitleText = incident.Title;
        IncidentTypeText = incident.IncidentType;
        StatusValue = incident.Status;
        PrimaryOccurrenceLocalText = incident.PrimaryOccurrenceUtc.HasValue
            ? FormatLocalInput(incident.PrimaryOccurrenceUtc.Value)
            : string.Empty;
        OffenseWindowStartLocalText = FormatLocalInput(incident.OffenseWindowStartUtc);
        OffenseWindowEndLocalText = FormatLocalInput(incident.OffenseWindowEndUtc);
        SummaryNotes = incident.SummaryNotes;
        ReplaceRows(LocationEditors, incident.Locations.Select(location => new IncidentLocationEditor
        {
            IncidentLocationId = location.IncidentLocationId,
            Label = location.Label,
            LatitudeText = location.Latitude.ToString(CultureInfo.InvariantCulture),
            LongitudeText = location.Longitude.ToString(CultureInfo.InvariantCulture),
            RadiusMetersText = location.RadiusMeters.ToString(CultureInfo.InvariantCulture),
            Notes = location.Notes
        }));
        SelectedLocationEditor = LocationEditors.FirstOrDefault();
        ReplaceRows(PinnedResults, incident.PinnedResults);
        SelectedPinnedResult = PinnedResults.FirstOrDefault();
    }

    private void ResetEditor()
    {
        var localNow = DateTimeOffset.Now;
        TitleText = string.Empty;
        IncidentTypeText = "Shots Fired";
        StatusValue = "Open";
        PrimaryOccurrenceLocalText = string.Empty;
        OffenseWindowStartLocalText = FormatLocalInput(localNow.AddHours(-1));
        OffenseWindowEndLocalText = FormatLocalInput(localNow.AddHours(1));
        SummaryNotes = string.Empty;
        ReplaceRows(LocationEditors, new[]
        {
            new IncidentLocationEditor
            {
                IncidentLocationId = Guid.Empty,
                Label = "Scene 1",
                RadiusMetersText = "250"
            }
        });
        SelectedLocationEditor = LocationEditors.FirstOrDefault();
        ReplaceRows(PinnedResults, Array.Empty<IncidentPinnedResult>());
        SelectedPinnedResult = null;
    }

    private void ClearResults()
    {
        _resultIncidentId = null;
        ReplaceRows(MessageResults, Array.Empty<TimelineRowDto>());
        ReplaceRows(LocationResults, Array.Empty<IncidentLocationHit>());
        ReplaceRows(TimelineItems, Array.Empty<IncidentTimelineItem>());
        SelectedMessageResult = null;
        SelectedLocationResult = null;
        SelectedTimelineItem = null;
    }

    private CancellationTokenSource BeginLoad(CancellationToken outerCt)
    {
        CancelLoad();
        _loadCts = CancellationTokenSource.CreateLinkedTokenSource(outerCt);
        return _loadCts;
    }

    private void EndLoad(CancellationTokenSource loadCts)
    {
        if (!ReferenceEquals(_loadCts, loadCts))
        {
            loadCts.Dispose();
            return;
        }

        _loadCts.Dispose();
        _loadCts = null;
        IsBusy = false;
    }

    private void CancelLoad()
    {
        var loadCts = _loadCts;
        _loadCts = null;
        loadCts?.Cancel();
        loadCts?.Dispose();
        IsBusy = false;
    }

    private IProgress<ReadinessProgress> CreateReadinessProgress()
    {
        return new Progress<ReadinessProgress>(update =>
        {
            StatusText = update.DetailText;
        });
    }

    private static DateTimeOffset ParseRequiredLocalDateTime(string text, string label)
    {
        var parsed = TryParseOptionalLocalDateTime(text);
        return parsed ?? throw new ArgumentException($"{label} is required. Use a local timestamp like 2026-03-01 14:30.");
    }

    private static DateTimeOffset? TryParseOptionalLocalDateTime(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
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
            return parsed.ToUniversalTime();
        }

        throw new ArgumentException("Use a local timestamp like 2026-03-01 14:30.");
    }

    private static double ParseDouble(string? text, string label)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            throw new ArgumentException($"{label} is required.");
        }

        if (double.TryParse(text.Trim(), NumberStyles.Float | NumberStyles.AllowThousands, CultureInfo.InvariantCulture, out var parsed)
            || double.TryParse(text.Trim(), NumberStyles.Float | NumberStyles.AllowThousands, CultureInfo.CurrentCulture, out parsed))
        {
            return parsed;
        }

        throw new ArgumentException($"{label} must be numeric.");
    }

    private static string FormatLocalInput(DateTimeOffset value)
    {
        return value.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
    }

    private static void ReplaceRows<T>(ObservableCollection<T> destination, IEnumerable<T> source)
    {
        destination.Clear();
        foreach (var item in source)
        {
            destination.Add(item);
        }
    }

    partial void OnSelectedIncidentChanged(IncidentRecord? value)
    {
        if (value is null)
        {
            return;
        }

        LoadEditorFromIncident(value);
        if (_resultIncidentId != value.IncidentId)
        {
            ClearResults();
        }
        StatusText = $"Loaded incident '{value.Title}'.";
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

    public sealed partial class IncidentLocationEditor : ObservableObject
    {
        public Guid IncidentLocationId { get; set; }

        [ObservableProperty]
        private string label = string.Empty;

        [ObservableProperty]
        private string latitudeText = string.Empty;

        [ObservableProperty]
        private string longitudeText = string.Empty;

        [ObservableProperty]
        private string radiusMetersText = "250";

        [ObservableProperty]
        private string notes = string.Empty;
    }
}
