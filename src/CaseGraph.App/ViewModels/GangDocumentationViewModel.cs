using CaseGraph.App.Services;
using CaseGraph.Infrastructure.GangDocumentation;
using CaseGraph.Infrastructure.Organizations;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System.Collections.ObjectModel;

namespace CaseGraph.App.ViewModels;

public sealed partial class GangDocumentationViewModel : ObservableObject
{
    private const string FocusOrganizationField = "Organization";
    private const string FocusAffiliationRoleField = "AffiliationRole";
    private const string FocusSummaryField = "Summary";
    private const string FocusCriterionTypeField = "CriterionType";
    private const string FocusCriterionBasisSummaryField = "CriterionBasisSummary";
    private const string FocusWorkflowReviewerField = "WorkflowReviewer";
    private const string FocusWorkflowDecisionNoteField = "WorkflowDecisionNote";

    private readonly IGangDocumentationService _gangDocumentationService;
    private readonly IOrganizationService _organizationService;
    private readonly IGangDocumentationPacketExportService _gangDocumentationPacketExportService;
    private readonly IUserInteractionService _userInteractionService;

    private IReadOnlyList<OrganizationSummaryDto> _allOrganizations = [];

    public GangDocumentationViewModel(
        IGangDocumentationService gangDocumentationService,
        IOrganizationService organizationService,
        IGangDocumentationPacketExportService? gangDocumentationPacketExportService = null,
        IUserInteractionService? userInteractionService = null
    )
    {
        _gangDocumentationService = gangDocumentationService;
        _organizationService = organizationService;
        _gangDocumentationPacketExportService = gangDocumentationPacketExportService ?? new NullGangDocumentationPacketExportService();
        _userInteractionService = userInteractionService ?? NullUserInteractionService.Instance;

        foreach (var role in GangDocumentationCatalog.AffiliationRoles)
        {
            AffiliationRoles.Add(role);
        }

        foreach (var criterionType in GangDocumentationCatalog.CriterionTypes)
        {
            CriterionTypes.Add(criterionType);
        }

        affiliationRole = AffiliationRoles.FirstOrDefault() ?? "member";
        criterionType = CriterionTypes.FirstOrDefault() ?? "self-admission";

        NewRecordCommand = new RelayCommand(BeginNewRecord);
        SaveRecordCommand = new AsyncRelayCommand(SaveRecordAsync);
        SubmitForReviewCommand = new AsyncRelayCommand(SubmitForReviewAsync);
        ApproveCommand = new AsyncRelayCommand(ApproveAsync);
        ReturnForChangesCommand = new AsyncRelayCommand(ReturnForChangesAsync);
        MarkInactiveCommand = new AsyncRelayCommand(MarkInactiveAsync);
        MarkPurgeReviewCommand = new AsyncRelayCommand(MarkPurgeReviewAsync);
        PurgeCommand = new AsyncRelayCommand(PurgeAsync);
        RestoreToApprovedCommand = new AsyncRelayCommand(RestoreToApprovedAsync);
        RestoreToInactiveCommand = new AsyncRelayCommand(RestoreToInactiveAsync);
        PreviewPacketCommand = new AsyncRelayCommand(PreviewPacketAsync);
        ExportPacketCommand = new AsyncRelayCommand(ExportPacketAsync);
        ClosePacketPreviewCommand = new RelayCommand(ClosePacketPreview);
        EditCriterionCommand = new RelayCommand<GangDocumentationCriterionItem?>(BeginCriterionEdit);
        NewCriterionCommand = new RelayCommand(BeginNewCriterion);
        SaveCriterionCommand = new AsyncRelayCommand(SaveCriterionAsync);
        RemoveCriterionCommand = new AsyncRelayCommand<GangDocumentationCriterionItem?>(RemoveCriterionAsync);
    }

    public event Action<string>? FocusRequested;

    public ObservableCollection<GangDocumentationRecordItem> Records { get; } = new();

    public ObservableCollection<GangDocumentationCriterionItem> Criteria { get; } = new();

    public ObservableCollection<GangDocumentationHistoryItem> History { get; } = new();

    public ObservableCollection<OrganizationOptionItem> OrganizationOptions { get; } = new();

    public ObservableCollection<OrganizationOptionItem> SubgroupOptions { get; } = new();

    public ObservableCollection<string> AffiliationRoles { get; } = new();

    public ObservableCollection<string> CriterionTypes { get; } = new();

    public IRelayCommand NewRecordCommand { get; }

    public IAsyncRelayCommand SaveRecordCommand { get; }

    public IAsyncRelayCommand SubmitForReviewCommand { get; }

    public IAsyncRelayCommand ApproveCommand { get; }

    public IAsyncRelayCommand ReturnForChangesCommand { get; }

    public IAsyncRelayCommand MarkInactiveCommand { get; }

    public IAsyncRelayCommand MarkPurgeReviewCommand { get; }

    public IAsyncRelayCommand PurgeCommand { get; }

    public IAsyncRelayCommand RestoreToApprovedCommand { get; }

    public IAsyncRelayCommand RestoreToInactiveCommand { get; }

    public IAsyncRelayCommand PreviewPacketCommand { get; }

    public IAsyncRelayCommand ExportPacketCommand { get; }

    public IRelayCommand ClosePacketPreviewCommand { get; }

    public IRelayCommand<GangDocumentationCriterionItem?> EditCriterionCommand { get; }

    public IRelayCommand NewCriterionCommand { get; }

    public IAsyncRelayCommand SaveCriterionCommand { get; }

    public IAsyncRelayCommand<GangDocumentationCriterionItem?> RemoveCriterionCommand { get; }

    public string SectionIntro =>
        "Formal gang documentation preserves evidence basis and analyst judgment separately from supervisor review metadata.";

    public string RecordsEmptyState => "No formal gang documentation records recorded for this profile.";

    public string CriteriaEmptyState => "No criteria recorded for the selected documentation record.";

    public string HistoryEmptyState => "No workflow history recorded yet.";

    public string SaveRecordButtonText => CurrentDocumentationId.HasValue
        ? SelectedRecord?.WorkflowStatus == GangDocumentationCatalog.WorkflowStatusReturnedForChanges
            ? "Save as Draft"
            : "Save Documentation"
        : "Create Documentation";

    public string SaveCriterionButtonText => CurrentCriterionId.HasValue
        ? "Update Criterion"
        : "Add Criterion";

    public string SubmitForReviewButtonText => SelectedRecord?.WorkflowStatus == GangDocumentationCatalog.WorkflowStatusReturnedForChanges
        ? "Re-submit for Review"
        : "Submit for Review";

    public bool HasRecords => Records.Count > 0;

    public bool HasOrganizationOptions => OrganizationOptions.Count > 0;

    public bool HasCriteria => Criteria.Count > 0;

    public bool HasHistory => History.Count > 0;

    public bool HasSelectedDocumentation => CurrentDocumentationId.HasValue;

    public bool IsCurrentWorkflowEditable => !HasSelectedDocumentation
        || (SelectedRecord is not null && SelectedRecord.IsEditable);

    public bool CanEditDocumentation => HasOrganizationOptions && !IsBusy && IsCurrentWorkflowEditable;

    public bool CanManageCriteria => HasSelectedDocumentation && !IsBusy && IsCurrentWorkflowEditable;

    public bool CanManageWorkflow => HasSelectedDocumentation && !IsBusy;

    public bool CanSubmitForReview => CanManageWorkflow && SelectedRecord?.CanSubmitForReview == true;

    public bool CanApprove => CanManageWorkflow && SelectedRecord?.CanApprove == true;

    public bool CanReturnForChanges => CanManageWorkflow && SelectedRecord?.CanReturnForChanges == true;

    public bool CanMarkInactive => CanManageWorkflow && SelectedRecord?.CanMarkInactive == true;

    public bool CanMarkPurgeReview => CanManageWorkflow && SelectedRecord?.CanMarkPurgeReview == true;

    public bool CanPurge => CanManageWorkflow && SelectedRecord?.CanPurge == true;

    public bool CanRestoreToApproved => CanManageWorkflow && SelectedRecord?.CanRestoreToApproved == true;

    public bool CanRestoreToInactive => CanManageWorkflow && SelectedRecord?.CanRestoreToInactive == true;

    public bool CanPreviewPacket => HasSelectedDocumentation
        && !IsBusy
        && SelectedRecord?.IsApproved == true;

    public bool CanExportPacket => HasPacketPreview && !IsBusy;

    public bool ShowSubmitForReviewAction => SelectedRecord?.CanSubmitForReview == true;

    public bool ShowApproveAction => SelectedRecord?.CanApprove == true;

    public bool ShowReturnForChangesAction => SelectedRecord?.CanReturnForChanges == true;

    public bool ShowMarkInactiveAction => SelectedRecord?.CanMarkInactive == true;

    public bool ShowMarkPurgeReviewAction => SelectedRecord?.CanMarkPurgeReview == true;

    public bool ShowPurgeAction => SelectedRecord?.CanPurge == true;

    public bool ShowRestoreToApprovedAction => SelectedRecord?.CanRestoreToApproved == true;

    public bool ShowRestoreToInactiveAction => SelectedRecord?.CanRestoreToInactive == true;

    public bool ShowPacketExportActions => HasSelectedDocumentation;

    public bool ShowWorkflowDecisionInputs => SelectedRecord?.RequiresWorkflowDecisionInputs == true;

    public bool HasPacketPreview => CurrentPacketPreview is not null;

    public string CreateWorkflowHint => !HasOrganizationOptions
        ? "Create or load at least one organization before creating gang documentation."
        : SelectedRecord?.WorkflowStatus == GangDocumentationCatalog.WorkflowStatusReturnedForChanges
            ? "Returned for Changes content is editable. Saving revisions moves the record back to Draft so it can be re-submitted."
            : IsCurrentWorkflowEditable
                ? "Edit documentation content while the record is in Draft, then submit it for review when ready."
                : "Content is read-only in the current workflow state. Use the visible workflow actions to move the record.";

    public string CriteriaWorkflowHint => !HasSelectedDocumentation
        ? "Save the documentation record first, then add criteria entries."
        : SelectedRecord?.WorkflowStatus == GangDocumentationCatalog.WorkflowStatusReturnedForChanges
            ? "Criteria are editable while Returned for Changes. Saving revisions moves the record back to Draft before re-submission."
            : IsCurrentWorkflowEditable
                ? "Criteria remain editable while the record is in Draft."
                : "Criteria are read-only until the record returns to an editable workflow state.";

    public string WorkflowHint => SelectedRecord is null
        ? "Select or create a documentation record to manage its workflow."
        : SelectedRecord.WorkflowHint;

    public string WorkflowDecisionInputHint => SelectedRecord is null
        ? "Reviewer details appear when a selected action requires supervisor attribution and a decision note."
        : ShowWorkflowDecisionInputs
            ? "Reviewer name and decision note are required for the visible supervisor actions."
            : "The current workflow action does not require reviewer metadata.";

    public string CurrentOrganizationDisplay =>
        ResolveOrganizationName(SelectedOrganizationId) ?? "(none selected)";

    public string CurrentSubgroupDisplay =>
        ResolveOrganizationName(SelectedSubgroupOrganizationId) ?? "(none selected)";

    public string CurrentWorkflowStatusDisplay => SelectedRecord?.WorkflowStatusDisplay ?? "Draft";

    public string CurrentStatusSummary => SelectedRecord is null
        ? "Current status: Draft"
        : $"Current status: {SelectedRecord.WorkflowStatusDisplay}";

    public string NextAllowedActionsDisplay => SelectedRecord?.NextActionsDisplay
        ?? "Next allowed actions: create or select a documentation record.";

    public string CurrentReviewSummary => SelectedRecord?.ReviewSummary ?? "Reviewer: (none) | review timestamp: (none)";

    public string CurrentDecisionNoteDisplay => SelectedRecord?.DecisionNoteDisplay ?? "Latest decision note: (none)";

    public string PacketExportHint => SelectedRecord is null
        ? "Select a documentation record to preview a packet export."
        : !SelectedRecord.IsApproved
            ? "Packet export is available only for Approved gang documentation records."
            : HasPacketPreview
                ? "Preview is ready. Write HTML to export the packet and audit the action."
                : "Build a preview before writing the approved gang documentation packet.";

    public string PacketPreviewEmptyState => "No export preview generated yet.";

    public bool ShowOrganizationRequiredIndicator =>
        RecordSubmitAttempted && !SelectedOrganizationId.HasValue;

    public bool ShowAffiliationRoleRequiredIndicator =>
        RecordSubmitAttempted && string.IsNullOrWhiteSpace(AffiliationRole);

    public bool ShowSummaryRequiredIndicator =>
        RecordSubmitAttempted && string.IsNullOrWhiteSpace(Summary);

    public bool ShowCriterionTypeRequiredIndicator =>
        CriterionSubmitAttempted && string.IsNullOrWhiteSpace(CriterionType);

    public bool ShowCriterionBasisSummaryRequiredIndicator =>
        CriterionSubmitAttempted && string.IsNullOrWhiteSpace(CriterionBasisSummary);

    public bool HasRecordValidationIssues =>
        ShowOrganizationRequiredIndicator
        || ShowAffiliationRoleRequiredIndicator
        || ShowSummaryRequiredIndicator;

    public bool HasCriterionValidationIssues =>
        ShowCriterionTypeRequiredIndicator
        || ShowCriterionBasisSummaryRequiredIndicator;

    public string RecordValidationMessage => HasRecordValidationIssues
        ? "Complete the required gang documentation fields marked with *."
        : string.Empty;

    public string CriterionValidationMessage => HasCriterionValidationIssues
        ? "Complete the required criterion fields marked with *."
        : string.Empty;

    [ObservableProperty]
    private Guid? currentCaseId;

    [ObservableProperty]
    private Guid? currentTargetId;

    [ObservableProperty]
    private bool isBusy;

    [ObservableProperty]
    private string statusMessage = string.Empty;

    [ObservableProperty]
    private GangDocumentationRecordItem? selectedRecord;

    [ObservableProperty]
    private Guid? currentDocumentationId;

    [ObservableProperty]
    private Guid? selectedOrganizationId;

    [ObservableProperty]
    private Guid? selectedSubgroupOrganizationId;

    [ObservableProperty]
    private string summary = string.Empty;

    [ObservableProperty]
    private string notes = string.Empty;

    [ObservableProperty]
    private string affiliationRole;

    [ObservableProperty]
    private bool recordSubmitAttempted;

    [ObservableProperty]
    private string workflowReviewerName = string.Empty;

    [ObservableProperty]
    private string workflowDecisionNote = string.Empty;

    [ObservableProperty]
    private GangDocumentationPacketModel? currentPacketPreview;

    [ObservableProperty]
    private Guid? currentCriterionId;

    [ObservableProperty]
    private string criterionType;

    [ObservableProperty]
    private bool criterionIsMet = true;

    [ObservableProperty]
    private string criterionBasisSummary = string.Empty;

    [ObservableProperty]
    private string criterionSourceNote = string.Empty;

    [ObservableProperty]
    private DateTime? criterionObservedDate;

    [ObservableProperty]
    private bool criterionSubmitAttempted;

    public async Task LoadAsync(Guid caseId, Guid targetId, CancellationToken ct)
    {
        CurrentCaseId = caseId;
        CurrentTargetId = targetId;
        IsBusy = true;
        StatusMessage = string.Empty;

        try
        {
            await EnsureOrganizationsLoadedAsync(ct);
            var records = await _gangDocumentationService.GetDocumentationForTargetAsync(
                caseId,
                targetId,
                ct
            );
            ApplyRecords(records);
        }
        catch (Exception ex)
        {
            StatusMessage = ex.Message;
            ClearAllState();
        }
        finally
        {
            IsBusy = false;
        }
    }

    public void ClearForUnavailableProfile()
    {
        StatusMessage = string.Empty;
        ClearAllState();
        CurrentCaseId = null;
        CurrentTargetId = null;
    }

    partial void OnSelectedRecordChanged(GangDocumentationRecordItem? value)
    {
        PopulateEditorFromSelectedRecord(value);
    }

    partial void OnIsBusyChanged(bool value)
    {
        RaiseSectionStateChanged();
    }

    partial void OnSelectedOrganizationIdChanged(Guid? value)
    {
        RefreshSubgroupOptions(value);
        OnPropertyChanged(nameof(CurrentOrganizationDisplay));
        RaiseValidationStateChanged();
    }

    partial void OnSelectedSubgroupOrganizationIdChanged(Guid? value)
    {
        OnPropertyChanged(nameof(CurrentSubgroupDisplay));
    }

    partial void OnSummaryChanged(string value)
    {
        RaiseValidationStateChanged();
    }

    partial void OnAffiliationRoleChanged(string value)
    {
        RaiseValidationStateChanged();
    }

    partial void OnCriterionTypeChanged(string value)
    {
        RaiseValidationStateChanged();
    }

    partial void OnCriterionBasisSummaryChanged(string value)
    {
        RaiseValidationStateChanged();
    }

    partial void OnCurrentPacketPreviewChanged(GangDocumentationPacketModel? value)
    {
        OnPropertyChanged(nameof(HasPacketPreview));
        OnPropertyChanged(nameof(CanExportPacket));
        OnPropertyChanged(nameof(PacketExportHint));
    }

    private async Task EnsureOrganizationsLoadedAsync(CancellationToken ct)
    {
        if (_allOrganizations.Count > 0)
        {
            RefreshOrganizationOptions();
            return;
        }

        _allOrganizations = await _organizationService.GetOrganizationsAsync(search: null, ct);
        RefreshOrganizationOptions();
    }

    private void RefreshOrganizationOptions()
    {
        OrganizationOptions.Clear();
        foreach (var organization in _allOrganizations.OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase))
        {
            OrganizationOptions.Add(new OrganizationOptionItem(
                organization.OrganizationId,
                organization.Name,
                organization.Type
            ));
        }

        RefreshSubgroupOptions(SelectedOrganizationId);
        EnsureSelectedOrganization();
        RaiseSectionStateChanged();
        OnPropertyChanged(nameof(CurrentOrganizationDisplay));
        OnPropertyChanged(nameof(CurrentSubgroupDisplay));
    }

    private void RefreshSubgroupOptions(Guid? organizationId)
    {
        SubgroupOptions.Clear();
        if (!organizationId.HasValue)
        {
            SelectedSubgroupOrganizationId = null;
            return;
        }

        var subgroups = _allOrganizations
            .Where(item =>
                item.ParentOrganizationId == organizationId.Value
                && (string.Equals(item.Type, "set", StringComparison.Ordinal)
                    || string.Equals(item.Type, "clique", StringComparison.Ordinal)
                    || string.Equals(item.Type, "subgroup", StringComparison.Ordinal))
            )
            .OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase);

        foreach (var subgroup in subgroups)
        {
            SubgroupOptions.Add(new OrganizationOptionItem(
                subgroup.OrganizationId,
                subgroup.Name,
                subgroup.Type
            ));
        }

        if (SelectedSubgroupOrganizationId.HasValue
            && !SubgroupOptions.Any(item => item.OrganizationId == SelectedSubgroupOrganizationId.Value))
        {
            SelectedSubgroupOrganizationId = null;
        }
    }

    private void ApplyRecords(IReadOnlyList<GangDocumentationRecord> records)
    {
        ClearPacketPreview();
        Records.Clear();
        foreach (var record in records)
        {
            Records.Add(new GangDocumentationRecordItem(
                record.DocumentationId,
                record.OrganizationId,
                record.OrganizationName,
                record.SubgroupOrganizationId,
                record.SubgroupOrganizationName,
                record.AffiliationRole,
                record.Summary,
                record.Notes,
                record.CreatedAtUtc,
                record.UpdatedAtUtc,
                record.Review.WorkflowStatus,
                record.Review.WorkflowStatusDisplay,
                record.Review.ReviewerName,
                record.Review.ReviewerIdentity,
                record.Review.SubmittedForReviewAtUtc,
                record.Review.ReviewedAtUtc,
                record.Review.ApprovedAtUtc,
                record.Review.DecisionNote,
                record.Criteria.Count,
                record.Criteria.Select(MapCriterionItem).ToList(),
                record.StatusHistory.Select(MapHistoryItem).ToList()
            ));
        }

        OnPropertyChanged(nameof(HasRecords));
        if (Records.Count > 0)
        {
            SelectedRecord = Records[0];
        }
        else
        {
            SetEditorToNewRecordState();
        }
    }

    private void PopulateEditorFromSelectedRecord(GangDocumentationRecordItem? value)
    {
        ClearPacketPreview();
        Criteria.Clear();
        History.Clear();

        if (value is null)
        {
            RecordSubmitAttempted = false;
            CurrentDocumentationId = null;
            SelectedOrganizationId = OrganizationOptions.FirstOrDefault()?.OrganizationId;
            Summary = string.Empty;
            Notes = string.Empty;
            AffiliationRole = AffiliationRoles.FirstOrDefault() ?? "member";
            WorkflowReviewerName = string.Empty;
            WorkflowDecisionNote = string.Empty;
            SelectedSubgroupOrganizationId = null;
            BeginNewCriterion();
            RaiseSectionStateChanged();
            return;
        }

        RecordSubmitAttempted = false;
        CurrentDocumentationId = value.DocumentationId;
        SelectedOrganizationId = value.OrganizationId;
        SelectedSubgroupOrganizationId = value.SubgroupOrganizationId;
        Summary = value.Summary;
        Notes = value.Notes ?? string.Empty;
        AffiliationRole = value.AffiliationRole;
        WorkflowReviewerName = value.ReviewerName ?? string.Empty;
        WorkflowDecisionNote = string.Empty;

        foreach (var criterion in value.Criteria)
        {
            Criteria.Add(criterion);
        }

        foreach (var entry in value.History)
        {
            History.Add(entry);
        }

        BeginNewCriterion();
        RaiseSectionStateChanged();
    }

    private void BeginNewRecord()
    {
        SetEditorToNewRecordState();
        StatusMessage = string.Empty;
    }

    private async Task SaveRecordAsync()
    {
        RecordSubmitAttempted = true;
        RaiseValidationStateChanged();

        if (!CurrentCaseId.HasValue || !CurrentTargetId.HasValue)
        {
            StatusMessage = "Gang documentation cannot be saved because the current profile is unavailable.";
            return;
        }

        if (!TryValidateRecord(out var recordFocusTarget))
        {
            StatusMessage = RecordValidationMessage;
            RequestFocus(recordFocusTarget);
            return;
        }

        var wasReturnedForChanges = SelectedRecord?.WorkflowStatus == GangDocumentationCatalog.WorkflowStatusReturnedForChanges;

        StatusMessage = string.Empty;
        IsBusy = true;

        try
        {
            var caseId = CurrentCaseId.GetValueOrDefault();
            var targetId = CurrentTargetId.GetValueOrDefault();
            var organizationId = SelectedOrganizationId.GetValueOrDefault();
            GangDocumentationRecord saved;
            if (!CurrentDocumentationId.HasValue)
            {
                saved = await _gangDocumentationService.CreateDocumentationAsync(
                    new CreateGangDocumentationRequest(
                        caseId,
                        targetId,
                        organizationId,
                        SelectedSubgroupOrganizationId,
                        AffiliationRole,
                        Summary,
                        NullIfWhiteSpace(Notes)
                    ),
                    CancellationToken.None
                );
            }
            else
            {
                var documentationId = CurrentDocumentationId.GetValueOrDefault();
                saved = await _gangDocumentationService.UpdateDocumentationAsync(
                    new UpdateGangDocumentationRequest(
                        caseId,
                        documentationId,
                        organizationId,
                        SelectedSubgroupOrganizationId,
                        AffiliationRole,
                        Summary,
                        NullIfWhiteSpace(Notes)
                    ),
                    CancellationToken.None
                );
            }

            await ReloadAndSelectAsync(saved.DocumentationId);
            RecordSubmitAttempted = false;
            RaiseValidationStateChanged();
            StatusMessage = wasReturnedForChanges
                ? "Gang documentation saved as draft."
                : "Gang documentation saved.";
        }
        catch (Exception ex)
        {
            StatusMessage = ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    private Task SubmitForReviewAsync()
    {
        return ExecuteWorkflowAsync(
            GangDocumentationCatalog.WorkflowActionSubmitForReview,
            requiresReviewer: false,
            requiresDecisionNote: false,
            successMessage: "Gang documentation submitted for review."
        );
    }

    private Task ApproveAsync()
    {
        return ExecuteWorkflowAsync(
            GangDocumentationCatalog.WorkflowActionApprove,
            requiresReviewer: true,
            requiresDecisionNote: true,
            successMessage: "Gang documentation approved."
        );
    }

    private Task ReturnForChangesAsync()
    {
        return ExecuteWorkflowAsync(
            GangDocumentationCatalog.WorkflowActionReturnForChanges,
            requiresReviewer: true,
            requiresDecisionNote: true,
            successMessage: "Gang documentation returned for changes."
        );
    }

    private Task MarkInactiveAsync()
    {
        return ExecuteWorkflowAsync(
            GangDocumentationCatalog.WorkflowActionMarkInactive,
            requiresReviewer: true,
            requiresDecisionNote: true,
            successMessage: "Gang documentation marked inactive."
        );
    }

    private Task MarkPurgeReviewAsync()
    {
        return ExecuteWorkflowAsync(
            GangDocumentationCatalog.WorkflowActionMarkPurgeReview,
            requiresReviewer: true,
            requiresDecisionNote: true,
            successMessage: "Gang documentation moved to purge review."
        );
    }

    private Task PurgeAsync()
    {
        return ExecuteWorkflowAsync(
            GangDocumentationCatalog.WorkflowActionPurge,
            requiresReviewer: true,
            requiresDecisionNote: true,
            successMessage: "Gang documentation marked purged."
        );
    }

    private Task RestoreToApprovedAsync()
    {
        return ExecuteWorkflowAsync(
            GangDocumentationCatalog.WorkflowActionRestoreToApproved,
            requiresReviewer: true,
            requiresDecisionNote: true,
            successMessage: "Gang documentation restored to approved."
        );
    }

    private Task RestoreToInactiveAsync()
    {
        return ExecuteWorkflowAsync(
            GangDocumentationCatalog.WorkflowActionRestoreToInactive,
            requiresReviewer: true,
            requiresDecisionNote: true,
            successMessage: "Gang documentation restored to inactive."
        );
    }

    private async Task PreviewPacketAsync()
    {
        if (!CurrentCaseId.HasValue || !CurrentDocumentationId.HasValue)
        {
            StatusMessage = "Select an approved documentation record before previewing a packet export.";
            return;
        }

        if (SelectedRecord?.IsApproved != true)
        {
            StatusMessage = "Only Approved gang documentation records can be previewed for export.";
            return;
        }

        StatusMessage = string.Empty;
        IsBusy = true;

        try
        {
            CurrentPacketPreview = await _gangDocumentationPacketExportService.BuildPreviewAsync(
                new GangDocumentationPacketBuildRequest(
                    CurrentCaseId.Value,
                    CurrentDocumentationId.Value,
                    Environment.UserName
                ),
                CancellationToken.None
            );
            StatusMessage = "Gang documentation packet preview generated.";
        }
        catch (Exception ex)
        {
            CurrentPacketPreview = null;
            StatusMessage = ex.Message;
        }
        finally
        {
            IsBusy = false;
            RaiseSectionStateChanged();
        }
    }

    private async Task ExportPacketAsync()
    {
        if (CurrentPacketPreview is null)
        {
            StatusMessage = "Generate a packet preview before exporting.";
            return;
        }

        var outputPath = _userInteractionService.PickReportOutputPath(
            $"{CurrentPacketPreview.SuggestedFileName}.html"
        );
        if (string.IsNullOrWhiteSpace(outputPath))
        {
            StatusMessage = "Gang documentation packet export canceled.";
            return;
        }

        StatusMessage = string.Empty;
        IsBusy = true;

        try
        {
            var result = await _gangDocumentationPacketExportService.ExportHtmlAsync(
                CurrentPacketPreview,
                outputPath,
                CancellationToken.None
            );
            StatusMessage = $"Gang documentation packet exported to {result.OutputPath}.";
        }
        catch (Exception ex)
        {
            StatusMessage = ex.Message;
        }
        finally
        {
            IsBusy = false;
            RaiseSectionStateChanged();
        }
    }

    private void ClosePacketPreview()
    {
        if (!HasPacketPreview)
        {
            return;
        }

        ClearPacketPreview();
        StatusMessage = "Gang documentation packet preview closed.";
        RaiseSectionStateChanged();
    }

    private async Task ExecuteWorkflowAsync(
        string workflowAction,
        bool requiresReviewer,
        bool requiresDecisionNote,
        string successMessage
    )
    {
        if (!CurrentCaseId.HasValue || !CurrentDocumentationId.HasValue)
        {
            StatusMessage = "Select a documentation record before running workflow actions.";
            return;
        }

        if (requiresReviewer && string.IsNullOrWhiteSpace(WorkflowReviewerName))
        {
            StatusMessage = "Reviewer name is required for this workflow action.";
            RequestFocus(FocusWorkflowReviewerField);
            return;
        }

        if (requiresDecisionNote && string.IsNullOrWhiteSpace(WorkflowDecisionNote))
        {
            StatusMessage = "Decision note is required for this workflow action.";
            RequestFocus(FocusWorkflowDecisionNoteField);
            return;
        }

        StatusMessage = string.Empty;
        IsBusy = true;

        try
        {
            await _gangDocumentationService.TransitionWorkflowAsync(
                new TransitionGangDocumentationWorkflowRequest(
                    CurrentCaseId.Value,
                    CurrentDocumentationId.Value,
                    workflowAction,
                    NullIfWhiteSpace(WorkflowReviewerName),
                    NullIfWhiteSpace(WorkflowDecisionNote)
                ),
                CancellationToken.None
            );

            await ReloadAndSelectAsync(CurrentDocumentationId.Value);
            WorkflowDecisionNote = string.Empty;
            StatusMessage = successMessage;
        }
        catch (Exception ex)
        {
            StatusMessage = ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void BeginNewCriterion()
    {
        CriterionSubmitAttempted = false;
        CurrentCriterionId = null;
        CriterionType = CriterionTypes.FirstOrDefault() ?? "self-admission";
        CriterionIsMet = true;
        CriterionBasisSummary = string.Empty;
        CriterionSourceNote = string.Empty;
        CriterionObservedDate = null;
        OnPropertyChanged(nameof(SaveCriterionButtonText));
    }

    private void BeginCriterionEdit(GangDocumentationCriterionItem? item)
    {
        if (item is null)
        {
            return;
        }

        CurrentCriterionId = item.CriterionId;
        CriterionType = item.CriterionType;
        CriterionIsMet = item.IsMet;
        CriterionBasisSummary = item.BasisSummary;
        CriterionSourceNote = item.SourceNote ?? string.Empty;
        CriterionObservedDate = item.ObservedDateUtc?.UtcDateTime.Date;
        OnPropertyChanged(nameof(SaveCriterionButtonText));
    }

    private async Task SaveCriterionAsync()
    {
        if (!CurrentCaseId.HasValue || !CurrentDocumentationId.HasValue)
        {
            StatusMessage = "Create or select a documentation record before adding criteria.";
            return;
        }

        CriterionSubmitAttempted = true;
        RaiseValidationStateChanged();

        if (!TryValidateCriterion(out var criterionFocusTarget))
        {
            StatusMessage = CriterionValidationMessage;
            RequestFocus(criterionFocusTarget);
            return;
        }

        var wasReturnedForChanges = SelectedRecord?.WorkflowStatus == GangDocumentationCatalog.WorkflowStatusReturnedForChanges;

        StatusMessage = string.Empty;
        IsBusy = true;

        try
        {
            var caseId = CurrentCaseId.GetValueOrDefault();
            var documentationId = CurrentDocumentationId.GetValueOrDefault();
            await _gangDocumentationService.SaveCriterionAsync(
                new SaveGangDocumentationCriterionRequest(
                    caseId,
                    documentationId,
                    CurrentCriterionId,
                    CriterionType,
                    CriterionIsMet,
                    CriterionBasisSummary,
                    ToDateOffset(CriterionObservedDate),
                    NullIfWhiteSpace(CriterionSourceNote)
                ),
                CancellationToken.None
            );

            await ReloadAndSelectAsync(documentationId);
            BeginNewCriterion();
            RaiseValidationStateChanged();
            StatusMessage = wasReturnedForChanges
                ? "Criterion saved. Documentation moved to Draft."
                : "Criterion saved.";
        }
        catch (Exception ex)
        {
            StatusMessage = ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task RemoveCriterionAsync(GangDocumentationCriterionItem? item)
    {
        if (item is null || !CurrentCaseId.HasValue || !CurrentDocumentationId.HasValue)
        {
            return;
        }

        var wasReturnedForChanges = SelectedRecord?.WorkflowStatus == GangDocumentationCatalog.WorkflowStatusReturnedForChanges;

        StatusMessage = string.Empty;
        IsBusy = true;

        try
        {
            await _gangDocumentationService.RemoveCriterionAsync(
                CurrentCaseId.Value,
                CurrentDocumentationId.Value,
                item.CriterionId,
                CancellationToken.None
            );
            await ReloadAndSelectAsync(CurrentDocumentationId.Value);
            BeginNewCriterion();
            StatusMessage = wasReturnedForChanges
                ? "Criterion removed. Documentation moved to Draft."
                : "Criterion removed.";
        }
        catch (Exception ex)
        {
            StatusMessage = ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task ReloadAndSelectAsync(Guid documentationId)
    {
        if (!CurrentCaseId.HasValue || !CurrentTargetId.HasValue)
        {
            return;
        }

        var records = await _gangDocumentationService.GetDocumentationForTargetAsync(
            CurrentCaseId.Value,
            CurrentTargetId.Value,
            CancellationToken.None
        );
        ApplyRecords(records);
        SelectedRecord = Records.FirstOrDefault(item => item.DocumentationId == documentationId);
    }

    private void ClearPacketPreview()
    {
        if (CurrentPacketPreview is not null)
        {
            CurrentPacketPreview = null;
        }
    }

    private void ClearAllState()
    {
        ClearPacketPreview();
        Records.Clear();
        Criteria.Clear();
        History.Clear();
        RecordSubmitAttempted = false;
        CriterionSubmitAttempted = false;
        CurrentDocumentationId = null;
        SelectedRecord = null;
        SelectedOrganizationId = null;
        SelectedSubgroupOrganizationId = null;
        Summary = string.Empty;
        Notes = string.Empty;
        AffiliationRole = AffiliationRoles.FirstOrDefault() ?? "member";
        WorkflowReviewerName = string.Empty;
        WorkflowDecisionNote = string.Empty;
        BeginNewCriterion();
        RaiseSectionStateChanged();
    }

    private void RaiseSectionStateChanged()
    {
        OnPropertyChanged(nameof(HasRecords));
        OnPropertyChanged(nameof(HasOrganizationOptions));
        OnPropertyChanged(nameof(HasCriteria));
        OnPropertyChanged(nameof(HasHistory));
        OnPropertyChanged(nameof(HasSelectedDocumentation));
        OnPropertyChanged(nameof(IsCurrentWorkflowEditable));
        OnPropertyChanged(nameof(CanEditDocumentation));
        OnPropertyChanged(nameof(CanManageCriteria));
        OnPropertyChanged(nameof(CanManageWorkflow));
        OnPropertyChanged(nameof(CanSubmitForReview));
        OnPropertyChanged(nameof(CanApprove));
        OnPropertyChanged(nameof(CanReturnForChanges));
        OnPropertyChanged(nameof(CanMarkInactive));
        OnPropertyChanged(nameof(CanMarkPurgeReview));
        OnPropertyChanged(nameof(CanPurge));
        OnPropertyChanged(nameof(CanRestoreToApproved));
        OnPropertyChanged(nameof(CanRestoreToInactive));
        OnPropertyChanged(nameof(CanPreviewPacket));
        OnPropertyChanged(nameof(CanExportPacket));
        OnPropertyChanged(nameof(ShowSubmitForReviewAction));
        OnPropertyChanged(nameof(ShowApproveAction));
        OnPropertyChanged(nameof(ShowReturnForChangesAction));
        OnPropertyChanged(nameof(ShowMarkInactiveAction));
        OnPropertyChanged(nameof(ShowMarkPurgeReviewAction));
        OnPropertyChanged(nameof(ShowPurgeAction));
        OnPropertyChanged(nameof(ShowRestoreToApprovedAction));
        OnPropertyChanged(nameof(ShowRestoreToInactiveAction));
        OnPropertyChanged(nameof(ShowPacketExportActions));
        OnPropertyChanged(nameof(ShowWorkflowDecisionInputs));
        OnPropertyChanged(nameof(HasPacketPreview));
        OnPropertyChanged(nameof(CreateWorkflowHint));
        OnPropertyChanged(nameof(CriteriaWorkflowHint));
        OnPropertyChanged(nameof(WorkflowHint));
        OnPropertyChanged(nameof(WorkflowDecisionInputHint));
        OnPropertyChanged(nameof(CurrentWorkflowStatusDisplay));
        OnPropertyChanged(nameof(CurrentStatusSummary));
        OnPropertyChanged(nameof(NextAllowedActionsDisplay));
        OnPropertyChanged(nameof(CurrentReviewSummary));
        OnPropertyChanged(nameof(CurrentDecisionNoteDisplay));
        OnPropertyChanged(nameof(PacketExportHint));
        OnPropertyChanged(nameof(PacketPreviewEmptyState));
        OnPropertyChanged(nameof(CurrentOrganizationDisplay));
        OnPropertyChanged(nameof(CurrentSubgroupDisplay));
        OnPropertyChanged(nameof(SaveRecordButtonText));
        OnPropertyChanged(nameof(SaveCriterionButtonText));
        OnPropertyChanged(nameof(SubmitForReviewButtonText));
        RaiseValidationStateChanged();
    }

    private void SetEditorToNewRecordState()
    {
        if (SelectedRecord is not null)
        {
            SelectedRecord = null;
            return;
        }

        PopulateEditorFromSelectedRecord(null);
    }

    private void EnsureSelectedOrganization()
    {
        if (!SelectedOrganizationId.HasValue && OrganizationOptions.Count > 0)
        {
            SelectedOrganizationId = OrganizationOptions[0].OrganizationId;
        }
    }

    private void RaiseValidationStateChanged()
    {
        OnPropertyChanged(nameof(ShowOrganizationRequiredIndicator));
        OnPropertyChanged(nameof(ShowAffiliationRoleRequiredIndicator));
        OnPropertyChanged(nameof(ShowSummaryRequiredIndicator));
        OnPropertyChanged(nameof(ShowCriterionTypeRequiredIndicator));
        OnPropertyChanged(nameof(ShowCriterionBasisSummaryRequiredIndicator));
        OnPropertyChanged(nameof(HasRecordValidationIssues));
        OnPropertyChanged(nameof(HasCriterionValidationIssues));
        OnPropertyChanged(nameof(RecordValidationMessage));
        OnPropertyChanged(nameof(CriterionValidationMessage));
    }

    private bool TryValidateRecord(out string focusTarget)
    {
        if (!SelectedOrganizationId.HasValue)
        {
            focusTarget = FocusOrganizationField;
            return false;
        }

        if (string.IsNullOrWhiteSpace(AffiliationRole))
        {
            focusTarget = FocusAffiliationRoleField;
            return false;
        }

        if (string.IsNullOrWhiteSpace(Summary))
        {
            focusTarget = FocusSummaryField;
            return false;
        }

        focusTarget = string.Empty;
        return true;
    }

    private bool TryValidateCriterion(out string focusTarget)
    {
        if (string.IsNullOrWhiteSpace(CriterionType))
        {
            focusTarget = FocusCriterionTypeField;
            return false;
        }

        if (string.IsNullOrWhiteSpace(CriterionBasisSummary))
        {
            focusTarget = FocusCriterionBasisSummaryField;
            return false;
        }

        focusTarget = string.Empty;
        return true;
    }

    private void RequestFocus(string focusTarget)
    {
        if (!string.IsNullOrWhiteSpace(focusTarget))
        {
            FocusRequested?.Invoke(focusTarget);
        }
    }

    private string? ResolveOrganizationName(Guid? organizationId)
    {
        if (!organizationId.HasValue)
        {
            return null;
        }

        return _allOrganizations
            .FirstOrDefault(item => item.OrganizationId == organizationId.Value)
            ?.Name;
    }

    private static GangDocumentationCriterionItem MapCriterionItem(GangDocumentationCriterion criterion)
    {
        return new GangDocumentationCriterionItem(
            criterion.CriterionId,
            criterion.CriterionType,
            criterion.IsMet,
            criterion.BasisSummary,
            criterion.ObservedDateUtc,
            criterion.SourceNote
        );
    }

    private static GangDocumentationHistoryItem MapHistoryItem(
        GangDocumentationStatusHistoryEntry history
    )
    {
        return new GangDocumentationHistoryItem(
            history.HistoryEntryId,
            history.ActionType,
            history.Summary,
            history.PreviousWorkflowStatus,
            history.NewWorkflowStatus,
            history.DecisionNote,
            history.ChangedBy,
            history.ChangedByIdentity,
            history.ChangedAtUtc
        );
    }

    private static DateTimeOffset? ToDateOffset(DateTime? value)
    {
        return value.HasValue
            ? new DateTimeOffset(DateTime.SpecifyKind(value.Value.Date, DateTimeKind.Utc))
            : null;
    }

    private static string? NullIfWhiteSpace(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

    public sealed record GangDocumentationRecordItem(
        Guid DocumentationId,
        Guid OrganizationId,
        string OrganizationName,
        Guid? SubgroupOrganizationId,
        string? SubgroupOrganizationName,
        string AffiliationRole,
        string Summary,
        string? Notes,
        DateTimeOffset CreatedAtUtc,
        DateTimeOffset UpdatedAtUtc,
        string WorkflowStatus,
        string WorkflowStatusDisplay,
        string? ReviewerName,
        string? ReviewerIdentity,
        DateTimeOffset? SubmittedForReviewAtUtc,
        DateTimeOffset? ReviewedAtUtc,
        DateTimeOffset? ApprovedAtUtc,
        string? DecisionNote,
        int CriteriaCount,
        IReadOnlyList<GangDocumentationCriterionItem> Criteria,
        IReadOnlyList<GangDocumentationHistoryItem> History
    )
    {
        public string RecordTitle => string.IsNullOrWhiteSpace(SubgroupOrganizationName)
            ? OrganizationName
            : $"{OrganizationName} / {SubgroupOrganizationName}";

        public string RecordSubtitle =>
            $"{AffiliationRole} | workflow: {WorkflowStatusDisplay}";

        public string ReviewDisplay => ReviewSummary;

        public string ReviewSummary
        {
            get
            {
                var reviewer = ReviewerName ?? ReviewerIdentity ?? "(none)";

                if (ApprovedAtUtc.HasValue)
                {
                    return $"Reviewer: {reviewer} | approved {ApprovedAtUtc.Value:yyyy-MM-dd HH:mm} UTC";
                }

                if (ReviewedAtUtc.HasValue)
                {
                    return $"Reviewer: {reviewer} | reviewed {ReviewedAtUtc.Value:yyyy-MM-dd HH:mm} UTC";
                }

                if (SubmittedForReviewAtUtc.HasValue)
                {
                    return $"Submitted for review: {SubmittedForReviewAtUtc.Value:yyyy-MM-dd HH:mm} UTC";
                }

                return "Reviewer: (none) | review timestamp: (none)";
            }
        }

        public string DecisionNoteDisplay => string.IsNullOrWhiteSpace(DecisionNote)
            ? "Latest decision note: (none)"
            : $"Latest decision note: {DecisionNote}";

        public IReadOnlyList<string> AllowedWorkflowActions =>
            GangDocumentationCatalog.GetAllowedWorkflowActions(WorkflowStatus);

        public string NextActionsDisplay => AllowedWorkflowActions.Count == 0
            ? "Next allowed actions: (none)"
            : $"Next allowed actions: {string.Join(", ", AllowedWorkflowActions.Select(GetWorkflowActionDisplayNameForCurrentState))}";

        public string WorkflowHint => WorkflowStatus switch
        {
            var status when string.Equals(status, GangDocumentationCatalog.WorkflowStatusDraft, StringComparison.Ordinal)
                => "Draft content is editable. Next action: Submit for Review.",
            var status when string.Equals(status, GangDocumentationCatalog.WorkflowStatusReturnedForChanges, StringComparison.Ordinal)
                => "Returned for Changes content is editable. Saving revisions moves it back to Draft, then re-submit it for review.",
            var status when string.Equals(status, GangDocumentationCatalog.WorkflowStatusPendingSupervisorReview, StringComparison.Ordinal)
                => "Pending Review is read-only. Next actions: Approve, Return for Changes, Mark Inactive, or Move to Purge Review.",
            var status when string.Equals(status, GangDocumentationCatalog.WorkflowStatusApproved, StringComparison.Ordinal)
                => "Approved is read-only. Next actions: Mark Inactive or Move to Purge Review.",
            var status when string.Equals(status, GangDocumentationCatalog.WorkflowStatusInactive, StringComparison.Ordinal)
                => "Inactive is read-only. Next actions: Restore to Approved or Move to Purge Review.",
            var status when string.Equals(status, GangDocumentationCatalog.WorkflowStatusPurgeReview, StringComparison.Ordinal)
                => "Purge Review is read-only. Next actions: Restore to Approved, Restore to Inactive, or Purge.",
            var status when string.Equals(status, GangDocumentationCatalog.WorkflowStatusPurged, StringComparison.Ordinal)
                => "Purged is a soft administrative state. Next actions: Restore to Approved or Restore to Inactive.",
            _ => "Workflow state available."
        };

        public bool IsEditable => GangDocumentationCatalog.IsEditableWorkflowStatus(WorkflowStatus);

        public bool CanSubmitForReview => AllowedWorkflowActions.Contains(GangDocumentationCatalog.WorkflowActionSubmitForReview, StringComparer.Ordinal);

        public bool CanApprove => AllowedWorkflowActions.Contains(GangDocumentationCatalog.WorkflowActionApprove, StringComparer.Ordinal);

        public bool CanReturnForChanges => AllowedWorkflowActions.Contains(GangDocumentationCatalog.WorkflowActionReturnForChanges, StringComparer.Ordinal);

        public bool CanMarkInactive => AllowedWorkflowActions.Contains(GangDocumentationCatalog.WorkflowActionMarkInactive, StringComparer.Ordinal);

        public bool CanMarkPurgeReview => AllowedWorkflowActions.Contains(GangDocumentationCatalog.WorkflowActionMarkPurgeReview, StringComparer.Ordinal);

        public bool CanPurge => AllowedWorkflowActions.Contains(GangDocumentationCatalog.WorkflowActionPurge, StringComparer.Ordinal);

        public bool CanRestoreToApproved => AllowedWorkflowActions.Contains(GangDocumentationCatalog.WorkflowActionRestoreToApproved, StringComparer.Ordinal);

        public bool CanRestoreToInactive => AllowedWorkflowActions.Contains(GangDocumentationCatalog.WorkflowActionRestoreToInactive, StringComparer.Ordinal);

        public bool IsApproved => string.Equals(
            WorkflowStatus,
            GangDocumentationCatalog.WorkflowStatusApproved,
            StringComparison.Ordinal
        );

        public bool RequiresWorkflowDecisionInputs => CanApprove
            || CanReturnForChanges
            || CanMarkInactive
            || CanMarkPurgeReview
            || CanPurge
            || CanRestoreToApproved
            || CanRestoreToInactive;

        public string OrganizationDisplay => $"Linked organization: {OrganizationName}";

        public string SubgroupDisplay => string.IsNullOrWhiteSpace(SubgroupOrganizationName)
            ? "Linked subgroup / set: (none)"
            : $"Linked subgroup / set: {SubgroupOrganizationName}";

        private string GetWorkflowActionDisplayNameForCurrentState(string workflowAction)
        {
            if (string.Equals(
                workflowAction,
                GangDocumentationCatalog.WorkflowActionSubmitForReview,
                StringComparison.Ordinal
            ) && string.Equals(
                WorkflowStatus,
                GangDocumentationCatalog.WorkflowStatusReturnedForChanges,
                StringComparison.Ordinal
            ))
            {
                return "Re-submit for Review";
            }

            return GangDocumentationCatalog.GetWorkflowActionDisplayName(workflowAction);
        }
    }

    public sealed record GangDocumentationCriterionItem(
        Guid CriterionId,
        string CriterionType,
        bool IsMet,
        string BasisSummary,
        DateTimeOffset? ObservedDateUtc,
        string? SourceNote
    )
    {
        public string StatusText => IsMet ? "Met" : "Not met";

        public string DetailText => ObservedDateUtc.HasValue
            ? $"{StatusText} | observed {ObservedDateUtc.Value:yyyy-MM-dd}"
            : $"{StatusText} | observed (none)";
    }

    public sealed record GangDocumentationHistoryItem(
        Guid HistoryEntryId,
        string ActionType,
        string Summary,
        string? PreviousWorkflowStatus,
        string? NewWorkflowStatus,
        string? DecisionNote,
        string? ChangedBy,
        string? ChangedByIdentity,
        DateTimeOffset ChangedAtUtc
    )
    {
        public string DetailText
        {
            get
            {
                var statusText = string.IsNullOrWhiteSpace(NewWorkflowStatus)
                    ? ActionType
                    : string.IsNullOrWhiteSpace(PreviousWorkflowStatus)
                        ? GangDocumentationCatalog.GetWorkflowStatusDisplayName(NewWorkflowStatus)
                        : $"{GangDocumentationCatalog.GetWorkflowStatusDisplayName(PreviousWorkflowStatus!)} -> {GangDocumentationCatalog.GetWorkflowStatusDisplayName(NewWorkflowStatus)}";
                var actor = ChangedBy ?? ChangedByIdentity ?? "(system)";
                return $"{ChangedAtUtc:yyyy-MM-dd HH:mm} UTC | {actor} | {statusText}";
            }
        }

        public string NoteDisplay => string.IsNullOrWhiteSpace(DecisionNote)
            ? string.Empty
            : $"Decision note: {DecisionNote}";
    }

    public sealed record OrganizationOptionItem(
        Guid OrganizationId,
        string Name,
        string Type
    )
    {
        public string DisplayName => $"{Name} ({Type})";
    }

    private sealed class NullGangDocumentationPacketExportService : IGangDocumentationPacketExportService
    {
        public Task<GangDocumentationPacketModel> BuildPreviewAsync(
            GangDocumentationPacketBuildRequest request,
            CancellationToken ct
        ) => throw new NotSupportedException("Gang documentation packet export service is not configured.");

        public Task<GangDocumentationPacketExportResult> ExportHtmlAsync(
            GangDocumentationPacketModel model,
            string outputPath,
            CancellationToken ct
        ) => throw new NotSupportedException("Gang documentation packet export service is not configured.");
    }

    private sealed class NullUserInteractionService : IUserInteractionService
    {
        public static NullUserInteractionService Instance { get; } = new();

        public string? PromptForCaseName() => null;

        public IReadOnlyList<string> PickEvidenceFiles() => [];

        public string? PickDebugBundleOutputPath(string defaultFileName) => null;

        public string? PickReportOutputPath(string defaultFileName) => null;

        public void CopyToClipboard(string value)
        {
        }
    }
}
