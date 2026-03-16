using CaseGraph.Core.Abstractions;
using CaseGraph.Core.Models;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System.Collections.ObjectModel;

namespace CaseGraph.App.ViewModels;

public sealed partial class PersonProfileViewModel : ObservableObject
{
    private readonly ITargetRegistryService _targetRegistryService;
    private readonly GangDocumentationViewModel _gangDocumentationViewModel;

    public PersonProfileViewModel(
        ITargetRegistryService targetRegistryService,
        GangDocumentationViewModel gangDocumentationViewModel
    )
    {
        _targetRegistryService = targetRegistryService;
        _gangDocumentationViewModel = gangDocumentationViewModel;
        OpenAffiliationCommand = new AsyncRelayCommand<PersonAffiliationItem?>(
            OpenAffiliationAsync
        );
        NavigateBackCommand = new RelayCommand(() => NavigateBackRequested?.Invoke());
    }

    public Action<Guid>? OpenOrganizationProfileRequested { get; set; }

    public Action? NavigateBackRequested { get; set; }

    public ObservableCollection<string> Aliases { get; } = new();

    public ObservableCollection<PersonAffiliationItem> Affiliations { get; } = new();

    public ObservableCollection<string> GlobalAliases { get; } = new();

    public ObservableCollection<string> GlobalIdentifiers { get; } = new();

    public ObservableCollection<string> GlobalOtherCases { get; } = new();

    public GangDocumentationViewModel GangDocumentation => _gangDocumentationViewModel;

    public IAsyncRelayCommand<PersonAffiliationItem?> OpenAffiliationCommand { get; }

    public IRelayCommand NavigateBackCommand { get; }

    public bool HasAliases => Aliases.Count > 0;

    public bool HasAffiliations => Affiliations.Count > 0;

    public bool HasLinkedGlobalPerson => LinkedGlobalPersonId.HasValue;

    public bool HasGlobalAliases => GlobalAliases.Count > 0;

    public bool HasGlobalIdentifiers => GlobalIdentifiers.Count > 0;

    public bool HasGlobalOtherCases => GlobalOtherCases.Count > 0;

    public string PrimaryAliasDisplay => string.IsNullOrWhiteSpace(PrimaryAlias)
        ? "(none)"
        : PrimaryAlias;

    public string NotesDisplay => string.IsNullOrWhiteSpace(Notes)
        ? "No notes recorded"
        : Notes;

    public string AliasesEmptyState => "No aliases recorded";

    public string AffiliationsEmptyState => "No organization memberships recorded";

    public string GlobalAliasesEmptyState => "No aliases recorded";

    public string GlobalIdentifiersEmptyState => "No identifiers recorded";

    public string GlobalOtherCasesEmptyState => "No linked global case references recorded";

    public string GlobalPersonEmptyState => "No linked global person recorded";

    [ObservableProperty]
    private Guid? currentCaseId;

    [ObservableProperty]
    private Guid? currentTargetId;

    [ObservableProperty]
    private bool isBusy;

    [ObservableProperty]
    private bool isRecordAvailable;

    [ObservableProperty]
    private string headerTitle = "Person / Target Profile";

    [ObservableProperty]
    private string headerSubtitle = "Record no longer available";

    [ObservableProperty]
    private string displayName = string.Empty;

    [ObservableProperty]
    private string primaryAlias = string.Empty;

    [ObservableProperty]
    private string notes = string.Empty;

    [ObservableProperty]
    private string affiliationSummary = AffiliationSummaryFormatter.NoOrganizationRecorded;

    [ObservableProperty]
    private DateTimeOffset? createdAtUtc;

    [ObservableProperty]
    private DateTimeOffset? updatedAtUtc;

    [ObservableProperty]
    private Guid? linkedGlobalPersonId;

    [ObservableProperty]
    private string linkedGlobalPersonDisplayName = string.Empty;

    public async Task LoadAsync(Guid caseId, Guid targetId, CancellationToken ct)
    {
        CurrentCaseId = caseId;
        CurrentTargetId = targetId;
        IsBusy = true;

        try
        {
            var details = await _targetRegistryService.GetTargetDetailsAsync(caseId, targetId, ct);
            if (details is null)
            {
                ShowUnavailableState("Record no longer available");
                return;
            }

            ApplyDetails(details);
            await _gangDocumentationViewModel.LoadAsync(caseId, targetId, ct);
        }
        finally
        {
            IsBusy = false;
        }
    }

    public void ShowUnavailableState(string title)
    {
        IsRecordAvailable = false;
        HeaderTitle = title;
        HeaderSubtitle = "Record no longer available";
        DisplayName = string.Empty;
        PrimaryAlias = string.Empty;
        Notes = string.Empty;
        AffiliationSummary = AffiliationSummaryFormatter.NoOrganizationRecorded;
        CreatedAtUtc = null;
        UpdatedAtUtc = null;
        LinkedGlobalPersonId = null;
        LinkedGlobalPersonDisplayName = string.Empty;
        ClearCollections();
        _gangDocumentationViewModel.ClearForUnavailableProfile();
        RaiseSectionStateChanged();
    }

    private void ApplyDetails(TargetDetails details)
    {
        var summary = details.Summary;
        IsRecordAvailable = true;
        HeaderTitle = summary.DisplayName;
        HeaderSubtitle = summary.PrimaryAlias is { Length: > 0 }
            ? $"Alias: {summary.PrimaryAlias}"
            : "Person / Target Profile";
        DisplayName = summary.DisplayName;
        PrimaryAlias = summary.PrimaryAlias ?? string.Empty;
        Notes = summary.Notes ?? string.Empty;
        AffiliationSummary = summary.AffiliationSummary;
        CreatedAtUtc = summary.CreatedAtUtc;
        UpdatedAtUtc = summary.UpdatedAtUtc;
        LinkedGlobalPersonId = details.GlobalPerson?.GlobalEntityId;
        LinkedGlobalPersonDisplayName = details.GlobalPerson?.DisplayName ?? string.Empty;

        Aliases.Clear();
        foreach (var alias in details.Aliases.Select(item => item.Alias))
        {
            Aliases.Add(alias);
        }

        Affiliations.Clear();
        foreach (var affiliation in details.Affiliations)
        {
            Affiliations.Add(new PersonAffiliationItem(
                affiliation.OrganizationId,
                affiliation.OrganizationName,
                BuildAffiliationDetail(affiliation),
                affiliation
            ));
        }

        GlobalAliases.Clear();
        GlobalIdentifiers.Clear();
        GlobalOtherCases.Clear();
        if (details.GlobalPerson is not null)
        {
            foreach (var alias in details.GlobalPerson.Aliases.Select(item => item.Alias))
            {
                GlobalAliases.Add(alias);
            }

            foreach (var identifier in details.GlobalPerson.Identifiers)
            {
                GlobalIdentifiers.Add(
                    $"{identifier.Type}: {identifier.ValueDisplay}"
                    + (identifier.IsPrimary ? " (primary)" : string.Empty)
                );
            }

            foreach (var otherCase in details.GlobalPerson.OtherCases)
            {
                GlobalOtherCases.Add($"{otherCase.CaseName}: {otherCase.TargetDisplayName}");
            }
        }

        RaiseSectionStateChanged();
    }

    private async Task OpenAffiliationAsync(PersonAffiliationItem? item)
    {
        if (item is null)
        {
            return;
        }

        OpenOrganizationProfileRequested?.Invoke(item.OrganizationId);
        await Task.CompletedTask;
    }

    private void ClearCollections()
    {
        Aliases.Clear();
        Affiliations.Clear();
        GlobalAliases.Clear();
        GlobalIdentifiers.Clear();
        GlobalOtherCases.Clear();
    }

    private void RaiseSectionStateChanged()
    {
        OnPropertyChanged(nameof(HasAliases));
        OnPropertyChanged(nameof(HasAffiliations));
        OnPropertyChanged(nameof(HasLinkedGlobalPerson));
        OnPropertyChanged(nameof(HasGlobalAliases));
        OnPropertyChanged(nameof(HasGlobalIdentifiers));
        OnPropertyChanged(nameof(HasGlobalOtherCases));
        OnPropertyChanged(nameof(PrimaryAliasDisplay));
        OnPropertyChanged(nameof(NotesDisplay));
    }

    private static string BuildAffiliationDetail(TargetOrganizationAffiliationInfo affiliation)
    {
        var parts = new List<string>
        {
            $"{affiliation.OrganizationType} / {affiliation.OrganizationStatus}",
            $"Membership: {affiliation.MembershipStatus}"
        };

        if (!string.IsNullOrWhiteSpace(affiliation.Role))
        {
            parts.Add($"Role: {affiliation.Role}");
        }

        parts.Add($"Confidence: {affiliation.Confidence:0}");

        if (affiliation.LastConfirmedDateUtc.HasValue)
        {
            parts.Add($"Last confirmed: {affiliation.LastConfirmedDateUtc.Value:yyyy-MM-dd}");
        }

        return string.Join(" | ", parts);
    }

    public sealed record PersonAffiliationItem(
        Guid OrganizationId,
        string OrganizationName,
        string DetailText,
        TargetOrganizationAffiliationInfo Affiliation
    );
}
