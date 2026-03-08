using CaseGraph.App.Services;
using CaseGraph.Core.Abstractions;
using CaseGraph.Core.Diagnostics;
using CaseGraph.Core.Models;
using CaseGraph.Infrastructure.Reports;
using CaseGraph.Infrastructure.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Threading;

namespace CaseGraph.App.ViewModels;

public partial class ReportsViewModel : ObservableObject, IDisposable
{
    private readonly ITargetRegistryService _targetRegistryService;
    private readonly ICaseQueryService _caseQueryService;
    private readonly IUserInteractionService _userInteractionService;
    private readonly IJobQueueService _jobQueueService;
    private readonly IJobQueryService _jobQueryService;
    private readonly IDisposable _jobUpdateSubscription;

    private readonly List<ReportSubjectOption> _targetSubjects = [];
    private readonly List<ReportSubjectOption> _globalSubjects = [];
    private Guid? _currentCaseId;
    private string _currentCaseName = string.Empty;
    private string? _lastSuggestedOutputPath;
    private bool _isInitializing;
    private bool _isDisposed;

    public ReportsViewModel(
        ITargetRegistryService targetRegistryService,
        ICaseQueryService caseQueryService,
        IUserInteractionService userInteractionService,
        IJobQueueService jobQueueService,
        IJobQueryService jobQueryService
    )
    {
        _isInitializing = true;
        _targetRegistryService = targetRegistryService;
        _caseQueryService = caseQueryService;
        _userInteractionService = userInteractionService;
        _jobQueueService = jobQueueService;
        _jobQueryService = jobQueryService;

        AvailableSubjectKinds =
        [
            new SubjectKindOption(DossierSubjectTypes.Target, "Target"),
            new SubjectKindOption(DossierSubjectTypes.GlobalPerson, "Global Person")
        ];

        BrowseOutputPathCommand = new RelayCommand(BrowseForOutputPath);
        CreateDossierCommand = new AsyncRelayCommand(CreateDossierAsync, () => CanCreateDossier);
        CancelExportCommand = new AsyncRelayCommand(CancelExportAsync, () => CanCancelExport);
        OpenFolderCommand = new RelayCommand(OpenFolder, () => CanOpenFolder);

        selectedSubjectKind = AvailableSubjectKinds[0];
        RebuildSubjectOptions();
        _isInitializing = false;
        NotifyStateChanged();

        _jobUpdateSubscription = _jobQueueService.JobUpdates.Subscribe(new JobObserver(DispatchJobUpdate));
    }

    public ObservableCollection<ReportSubjectOption> SubjectOptions { get; } = new();

    public IReadOnlyList<SubjectKindOption> AvailableSubjectKinds { get; }

    public bool HasSubjectOptions => SubjectOptions.Count > 0;

    public bool CanCreateDossier => _currentCaseId.HasValue
        && SelectedSubject is not null
        && HasSelectedSections
        && !CanCancelExport;

    public bool HasSelectedSections => IncludeSubjectIdentifiers
        || IncludeWhereSeenSummary
        || IncludeTimelineExcerpt
        || IncludeNotableMessageExcerpts
        || IncludeAppendix;

    public bool CanCancelExport => LatestExportJob is not null
        && LatestExportJob.JobType == JobQueueService.ReportExportJobType
        && LatestExportJob.Status is not JobStatus.Succeeded
            and not JobStatus.Failed
            and not JobStatus.Canceled
            and not JobStatus.Abandoned;

    public bool CanOpenFolder => !string.IsNullOrWhiteSpace(ResolvedOutputPath);

    public string ResolvedOutputPath => !string.IsNullOrWhiteSpace(LastCompletedOutputPath)
        ? LastCompletedOutputPath
        : OutputPath;

    public IRelayCommand BrowseOutputPathCommand { get; }

    public IAsyncRelayCommand CreateDossierCommand { get; }

    public IAsyncRelayCommand CancelExportCommand { get; }

    public IRelayCommand OpenFolderCommand { get; }

    [ObservableProperty]
    private SubjectKindOption? selectedSubjectKind;

    [ObservableProperty]
    private ReportSubjectOption? selectedSubject;

    [ObservableProperty]
    private DateTime? fromDateLocal;

    [ObservableProperty]
    private DateTime? toDateLocal;

    [ObservableProperty]
    private bool includeSubjectIdentifiers = true;

    [ObservableProperty]
    private bool includeWhereSeenSummary = true;

    [ObservableProperty]
    private bool includeTimelineExcerpt = true;

    [ObservableProperty]
    private bool includeNotableMessageExcerpts = true;

    [ObservableProperty]
    private bool includeAppendix = true;

    [ObservableProperty]
    private string outputPath = string.Empty;

    [ObservableProperty]
    private string exportStatusText = "Open a case to create a dossier.";

    [ObservableProperty]
    private double exportProgress;

    [ObservableProperty]
    private JobInfo? latestExportJob;

    [ObservableProperty]
    private string lastCompletedOutputPath = string.Empty;

    public async Task SetCurrentCaseAsync(Guid? caseId, CancellationToken ct)
    {
        LogLifecycleEvent("ReportsCaseContextStarting", "Updating Reports case context.", caseId);

        try
        {
            _currentCaseId = caseId;
            _currentCaseName = string.Empty;
            SubjectOptions.Clear();
            _targetSubjects.Clear();
            _globalSubjects.Clear();
            SelectedSubject = null;
            LatestExportJob = null;
            LastCompletedOutputPath = string.Empty;
            ExportProgress = 0;
            FromDateLocal = null;
            ToDateLocal = null;

            if (!caseId.HasValue)
            {
                OutputPath = string.Empty;
                ExportStatusText = "Open a case to create a dossier.";
                NotifyStateChanged();
                LogLifecycleEvent("ReportsCaseContextCompleted", "Reports case context cleared.");
                return;
            }

            var caseInfo = await _caseQueryService.GetCaseAsync(caseId.Value, ct);
            _currentCaseName = caseInfo?.Name ?? $"Case {caseId.Value:D}";
            var targets = await _targetRegistryService.GetTargetsAsync(caseId.Value, search: null, ct);

            _targetSubjects.AddRange(
                targets
                    .OrderBy(target => target.DisplayName, StringComparer.OrdinalIgnoreCase)
                    .Select(target => new ReportSubjectOption(
                        DossierSubjectTypes.Target,
                        target.TargetId,
                        target.DisplayName,
                        target.PrimaryAlias
                    ))
            );
            _globalSubjects.AddRange(
                targets
                    .Where(target => target.GlobalEntityId.HasValue)
                    .GroupBy(target => target.GlobalEntityId!.Value)
                    .Select(group =>
                    {
                        var displayName = group
                            .Select(item => item.GlobalDisplayName)
                            .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))
                            ?? $"Global Person {group.Key:D}";
                        var detail = string.Join(
                            ", ",
                            group.Select(item => item.DisplayName)
                                .Distinct(StringComparer.OrdinalIgnoreCase)
                                .OrderBy(value => value, StringComparer.OrdinalIgnoreCase)
                        );
                        return new ReportSubjectOption(
                            DossierSubjectTypes.GlobalPerson,
                            group.Key,
                            displayName,
                            detail
                        );
                    })
                    .OrderBy(option => option.DisplayName, StringComparer.OrdinalIgnoreCase)
            );

            if (_targetSubjects.Count == 0 && _globalSubjects.Count == 0)
            {
                ExportStatusText = "No targets or global persons are available in the current case.";
                OutputPath = string.Empty;
                NotifyStateChanged();
                LogLifecycleEvent(
                    "ReportsCaseContextCompleted",
                    "Reports case context updated with no available subjects.",
                    caseId
                );
                return;
            }

            SelectedSubjectKind = _targetSubjects.Count > 0
                ? AvailableSubjectKinds.First(option => option.Value == DossierSubjectTypes.Target)
                : AvailableSubjectKinds.First(option => option.Value == DossierSubjectTypes.GlobalPerson);
            RebuildSubjectOptions();
            await RefreshLatestReportJobAsync(ct);
            NotifyStateChanged();
            LogLifecycleEvent(
                "ReportsCaseContextCompleted",
                "Reports case context updated.",
                caseId,
                subjectCount: SubjectOptions.Count
            );
        }
        catch (Exception ex)
        {
            LogLifecycleFailure("ReportsCaseContextFailed", "Reports case context update failed.", ex, caseId);
            throw;
        }
    }

    public async Task ActivateAsync(CancellationToken ct)
    {
        LogLifecycleEvent("ReportsActivationStarting", "Activating Reports page.", _currentCaseId);

        try
        {
            if (_currentCaseId.HasValue)
            {
                await RefreshLatestReportJobAsync(ct);
            }

            LogLifecycleEvent("ReportsActivationCompleted", "Reports page activated.", _currentCaseId);
        }
        catch (Exception ex)
        {
            LogLifecycleFailure("ReportsActivationFailed", "Reports page activation failed.", ex, _currentCaseId);
            throw;
        }
    }

    public void Deactivate()
    {
        LogLifecycleEvent("ReportsDeactivated", "Reports page deactivated.", _currentCaseId);
    }

    partial void OnSelectedSubjectKindChanged(SubjectKindOption? value)
    {
        if (_isInitializing)
        {
            return;
        }

        RebuildSubjectOptions();
        NotifyStateChanged();
    }

    partial void OnSelectedSubjectChanged(ReportSubjectOption? value)
    {
        if (_isInitializing)
        {
            return;
        }

        ApplySuggestedOutputPath();
        NotifyStateChanged();
    }

    partial void OnIncludeSubjectIdentifiersChanged(bool value)
    {
        NotifyStateChanged();
    }

    partial void OnIncludeWhereSeenSummaryChanged(bool value)
    {
        NotifyStateChanged();
    }

    partial void OnIncludeTimelineExcerptChanged(bool value)
    {
        NotifyStateChanged();
    }

    partial void OnIncludeNotableMessageExcerptsChanged(bool value)
    {
        NotifyStateChanged();
    }

    partial void OnIncludeAppendixChanged(bool value)
    {
        NotifyStateChanged();
    }

    partial void OnLatestExportJobChanged(JobInfo? value)
    {
        OnPropertyChanged(nameof(CanCancelExport));
        OnPropertyChanged(nameof(CanCreateDossier));
        CancelExportCommand.NotifyCanExecuteChanged();
        CreateDossierCommand.NotifyCanExecuteChanged();
    }

    partial void OnLastCompletedOutputPathChanged(string value)
    {
        OnPropertyChanged(nameof(ResolvedOutputPath));
        OnPropertyChanged(nameof(CanOpenFolder));
        OpenFolderCommand.NotifyCanExecuteChanged();
    }

    partial void OnOutputPathChanged(string value)
    {
        OnPropertyChanged(nameof(ResolvedOutputPath));
        OnPropertyChanged(nameof(CanOpenFolder));
        OpenFolderCommand.NotifyCanExecuteChanged();
    }

    private void RebuildSubjectOptions()
    {
        var previousSelection = SelectedSubject;
        SubjectOptions.Clear();

        var source = SelectedSubjectKind?.Value == DossierSubjectTypes.GlobalPerson
            ? _globalSubjects
            : _targetSubjects;

        foreach (var option in source)
        {
            SubjectOptions.Add(option);
        }

        SelectedSubject = previousSelection is not null
            ? SubjectOptions.FirstOrDefault(option =>
                option.SubjectType == previousSelection.SubjectType
                && option.SubjectId == previousSelection.SubjectId)
            : SubjectOptions.FirstOrDefault();

        OnPropertyChanged(nameof(HasSubjectOptions));
        ApplySuggestedOutputPath();
    }

    private async Task CreateDossierAsync()
    {
        if (!_currentCaseId.HasValue || SelectedSubject is null)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(OutputPath))
        {
            BrowseForOutputPath();
            if (string.IsNullOrWhiteSpace(OutputPath))
            {
                return;
            }
        }

        var payload = new DossierExportJobPayload(
            SchemaVersion: 1,
            CaseId: _currentCaseId.Value,
            SubjectType: SelectedSubject.SubjectType,
            SubjectId: SelectedSubject.SubjectId,
            FromUtc: ConvertLocalDateToStartUtc(FromDateLocal),
            ToUtc: ConvertLocalDateToInclusiveEndUtc(ToDateLocal),
            Sections: BuildSections(),
            OutputPath: OutputPath,
            RequestedBy: Environment.UserName
        );

        var jobId = await _jobQueueService.EnqueueAsync(
            new JobEnqueueRequest(
                JobQueueService.ReportExportJobType,
                _currentCaseId.Value,
                EvidenceItemId: null,
                JsonPayload: JsonSerializer.Serialize(payload)
            ),
            CancellationToken.None
        );

        LatestExportJob = new JobInfo
        {
            JobId = jobId,
            JobType = JobQueueService.ReportExportJobType,
            CaseId = _currentCaseId.Value,
            Status = JobStatus.Queued,
            Progress = 0,
            StatusMessage = "Queued.",
            CreatedAtUtc = DateTimeOffset.UtcNow,
            JsonPayload = JsonSerializer.Serialize(payload),
            CorrelationId = string.Empty,
            Operator = Environment.UserName
        };
        LastCompletedOutputPath = string.Empty;
        ExportProgress = 0;
        ExportStatusText = "Dossier export queued.";
        NotifyStateChanged();
    }

    private async Task CancelExportAsync()
    {
        if (LatestExportJob is null || !CanCancelExport)
        {
            return;
        }

        await _jobQueueService.CancelAsync(LatestExportJob.JobId, CancellationToken.None);
        ExportStatusText = "Cancellation requested.";
    }

    private void BrowseForOutputPath()
    {
        var selectedPath = _userInteractionService.PickReportOutputPath(BuildDefaultFileName());
        if (string.IsNullOrWhiteSpace(selectedPath))
        {
            return;
        }

        OutputPath = selectedPath;
        _lastSuggestedOutputPath = selectedPath;
    }

    private async Task RefreshLatestReportJobAsync(CancellationToken ct)
    {
        if (!_currentCaseId.HasValue)
        {
            LatestExportJob = null;
            ExportStatusText = "Open a case to create a dossier.";
            ExportProgress = 0;
            return;
        }

        var recentJobs = await _jobQueryService.GetRecentJobsAsync(_currentCaseId.Value, 25, ct);
        var latest = recentJobs
            .Where(job => job.JobType == JobQueueService.ReportExportJobType)
            .OrderByDescending(job => job.CreatedAtUtc)
            .FirstOrDefault();

        LatestExportJob = latest;
        if (latest is null)
        {
            ExportStatusText = HasSubjectOptions
                ? "Choose a subject and click Create Dossier."
                : "No subjects are available in the current case.";
            ExportProgress = 0;
            return;
        }

        ExportProgress = latest.Status is JobStatus.Succeeded or JobStatus.Failed or JobStatus.Canceled or JobStatus.Abandoned
            ? 1
            : Math.Clamp(latest.Progress, 0, 1);
        ExportStatusText = $"{latest.Status}: {latest.StatusMessage}";
        if (latest.Status == JobStatus.Succeeded)
        {
            LastCompletedOutputPath = TryReadOutputPath(latest.JsonPayload) ?? LastCompletedOutputPath;
        }
    }

    private void DispatchJobUpdate(JobInfo job)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null)
        {
            ApplyJobUpdate(job);
            return;
        }

        if (dispatcher.CheckAccess())
        {
            ApplyJobUpdate(job);
            return;
        }

        dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => ApplyJobUpdate(job)));
    }

    private void ApplyJobUpdate(JobInfo job)
    {
        if (!_currentCaseId.HasValue
            || job.CaseId != _currentCaseId.Value
            || !string.Equals(job.JobType, JobQueueService.ReportExportJobType, StringComparison.Ordinal))
        {
            return;
        }

        if (LatestExportJob is not null
            && LatestExportJob.JobId != job.JobId
            && LatestExportJob.CreatedAtUtc > job.CreatedAtUtc)
        {
            return;
        }

        LatestExportJob = job;
        ExportProgress = job.Status is JobStatus.Succeeded or JobStatus.Failed or JobStatus.Canceled or JobStatus.Abandoned
            ? 1
            : Math.Clamp(job.Progress, 0, 1);
        ExportStatusText = $"{job.Status}: {job.StatusMessage}";
        if (job.Status == JobStatus.Succeeded)
        {
            LastCompletedOutputPath = TryReadOutputPath(job.JsonPayload) ?? OutputPath;
        }
        else if (job.Status is JobStatus.Failed or JobStatus.Canceled)
        {
            LastCompletedOutputPath = string.Empty;
        }

        NotifyStateChanged();
    }

    private void OpenFolder()
    {
        var path = ResolvedOutputPath;
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        var directory = Path.GetDirectoryName(path);
        if (string.IsNullOrWhiteSpace(directory))
        {
            return;
        }

        Directory.CreateDirectory(directory);
        Process.Start(new ProcessStartInfo
        {
            FileName = directory,
            UseShellExecute = true
        });
    }

    private DossierSectionSelection BuildSections()
    {
        return new DossierSectionSelection(
            IncludeSubjectIdentifiers,
            IncludeWhereSeenSummary,
            IncludeTimelineExcerpt,
            IncludeNotableMessageExcerpts,
            IncludeAppendix
        );
    }

    private void ApplySuggestedOutputPath()
    {
        if (!_currentCaseId.HasValue || SelectedSubject is null)
        {
            return;
        }

        var suggested = Path.Combine(
            ResolveDefaultDirectory(),
            BuildDefaultFileName()
        );

        if (string.IsNullOrWhiteSpace(OutputPath)
            || string.Equals(OutputPath, _lastSuggestedOutputPath, StringComparison.OrdinalIgnoreCase))
        {
            OutputPath = suggested;
        }

        _lastSuggestedOutputPath = suggested;
    }

    private string BuildDefaultFileName()
    {
        var casePart = SanitizeFileName(_currentCaseName);
        var subjectPart = SanitizeFileName(SelectedSubject?.DisplayName ?? "subject");
        return $"{casePart}-{subjectPart}-dossier.html";
    }

    private static string ResolveDefaultDirectory()
    {
        var directory = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        if (string.IsNullOrWhiteSpace(directory))
        {
            directory = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        }

        return string.IsNullOrWhiteSpace(directory)
            ? Environment.CurrentDirectory
            : directory;
    }

    private static string SanitizeFileName(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "casegraph";
        }

        var invalid = Path.GetInvalidFileNameChars();
        var sanitized = new string(
            value.Trim()
                .Select(character => invalid.Contains(character) ? '_' : character)
                .ToArray()
        );
        return string.IsNullOrWhiteSpace(sanitized)
            ? "casegraph"
            : sanitized;
    }

    private void NotifyStateChanged()
    {
        OnPropertyChanged(nameof(CanCreateDossier));
        OnPropertyChanged(nameof(CanCancelExport));
        OnPropertyChanged(nameof(CanOpenFolder));
        OnPropertyChanged(nameof(HasSelectedSections));
        OnPropertyChanged(nameof(HasSubjectOptions));
        CreateDossierCommand?.NotifyCanExecuteChanged();
        CancelExportCommand?.NotifyCanExecuteChanged();
        OpenFolderCommand?.NotifyCanExecuteChanged();
    }

    private static void LogLifecycleEvent(
        string eventName,
        string message,
        Guid? caseId = null,
        int? subjectCount = null
    )
    {
        var fields = new Dictionary<string, object?>();
        if (caseId.HasValue)
        {
            fields["caseId"] = caseId.Value.ToString("D");
        }

        if (subjectCount.HasValue)
        {
            fields["subjectCount"] = subjectCount.Value;
        }

        AppFileLogger.LogEvent(
            eventName: eventName,
            level: "INFO",
            message: message,
            fields: fields
        );
    }

    private static void LogLifecycleFailure(
        string eventName,
        string message,
        Exception ex,
        Guid? caseId = null
    )
    {
        var fields = caseId.HasValue
            ? new Dictionary<string, object?>
            {
                ["caseId"] = caseId.Value.ToString("D")
            }
            : null;

        AppFileLogger.LogEvent(
            eventName: eventName,
            level: "ERROR",
            message: message,
            ex: ex,
            fields: fields
        );
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

    private static string? TryReadOutputPath(string? jsonPayload)
    {
        if (string.IsNullOrWhiteSpace(jsonPayload))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(jsonPayload);
            return document.RootElement.TryGetProperty("OutputPath", out var property)
                ? property.GetString()
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public void Dispose()
    {
        if (_isDisposed)
        {
            return;
        }

        _isDisposed = true;
        _jobUpdateSubscription.Dispose();
    }

    public sealed record SubjectKindOption(string Value, string DisplayName);

    public sealed record ReportSubjectOption(
        string SubjectType,
        Guid SubjectId,
        string DisplayName,
        string? Detail
    )
    {
        public string Summary => string.IsNullOrWhiteSpace(Detail)
            ? DisplayName
            : $"{DisplayName} - {Detail}";
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
}
