using CaseGraph.Core.Abstractions;
using CaseGraph.Core.Diagnostics;
using CaseGraph.Core.Models;
using CaseGraph.Infrastructure.Organizations;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System.Collections.ObjectModel;
using System.Globalization;

namespace CaseGraph.App.ViewModels;

public sealed partial class OrganizationRegistryViewModel : ObservableObject, IDisposable
{
    private readonly IOrganizationService _organizationService;
    private readonly ITargetRegistryService _targetRegistryService;
    private bool _isActive;
    private bool _isDisposed;

    public OrganizationRegistryViewModel(
        IOrganizationService organizationService,
        ITargetRegistryService targetRegistryService
    )
    {
        _organizationService = organizationService;
        _targetRegistryService = targetRegistryService;

        SelectedNewOrganizationType = OrganizationTypes[0];
        SelectedNewOrganizationStatus = OrganizationStatuses[0];
        SelectedOrganizationType = OrganizationTypes[0];
        SelectedOrganizationStatus = OrganizationStatuses[0];
        MembershipStatus = MembershipStatuses[0];
        MembershipConfidenceText = "50";

        RefreshOrganizationsCommand = new AsyncRelayCommand(
            () => RefreshOrganizationsAsync(CancellationToken.None)
        );
        CreateOrganizationCommand = new AsyncRelayCommand(CreateOrganizationAsync, () => CanCreateOrganization);
        SaveSelectedOrganizationCommand = new AsyncRelayCommand(
            SaveSelectedOrganizationAsync,
            () => HasSelectedOrganization
        );
        AddAliasCommand = new AsyncRelayCommand(AddAliasAsync, () => HasSelectedOrganization && CanAddAlias);
        RemoveAliasCommand = new AsyncRelayCommand(RemoveAliasAsync, () => HasSelectedAlias);
        SearchGlobalPersonsCommand = new AsyncRelayCommand(SearchGlobalPersonsAsync);
        AddMembershipCommand = new AsyncRelayCommand(AddMembershipAsync, () => CanAddMembership);
        UpdateMembershipCommand = new AsyncRelayCommand(UpdateMembershipAsync, () => HasSelectedMembership);
        RemoveMembershipCommand = new AsyncRelayCommand(RemoveMembershipAsync, () => HasSelectedMembership);
    }

    public ObservableCollection<OrganizationSummaryDto> Organizations { get; } = new();

    public ObservableCollection<OrganizationSummaryDto> AvailableParentOrganizations { get; } = new();

    public ObservableCollection<OrganizationAliasDto> SelectedOrganizationAliases { get; } = new();

    public ObservableCollection<OrganizationMembershipDto> SelectedOrganizationMemberships { get; } = new();

    public ObservableCollection<OrganizationSummaryDto> SelectedOrganizationChildren { get; } = new();

    public ObservableCollection<GlobalPersonSummary> GlobalPersonSearchResults { get; } = new();

    public IReadOnlyList<string> OrganizationTypes => OrganizationRegistryCatalog.OrganizationTypes;

    public IReadOnlyList<string> OrganizationStatuses => OrganizationRegistryCatalog.OrganizationStatuses;

    public IReadOnlyList<string> MembershipStatuses => OrganizationRegistryCatalog.MembershipStatuses;

    public bool HasSelectedOrganization => SelectedOrganization is not null;

    public bool HasSelectedAlias => SelectedOrganizationAlias is not null;

    public bool HasSelectedMembership => SelectedOrganizationMembership is not null;

    public bool HasSelectedGlobalPersonSearchResult => SelectedGlobalPersonSearchResult is not null;

    public bool CanCreateOrganization => !string.IsNullOrWhiteSpace(NewOrganizationName);

    public bool CanAddAlias => !string.IsNullOrWhiteSpace(NewAliasText);

    public bool CanAddMembership => HasSelectedOrganization && HasSelectedGlobalPersonSearchResult;

    public IAsyncRelayCommand RefreshOrganizationsCommand { get; }

    public IAsyncRelayCommand CreateOrganizationCommand { get; }

    public IAsyncRelayCommand SaveSelectedOrganizationCommand { get; }

    public IAsyncRelayCommand AddAliasCommand { get; }

    public IAsyncRelayCommand RemoveAliasCommand { get; }

    public IAsyncRelayCommand SearchGlobalPersonsCommand { get; }

    public IAsyncRelayCommand AddMembershipCommand { get; }

    public IAsyncRelayCommand UpdateMembershipCommand { get; }

    public IAsyncRelayCommand RemoveMembershipCommand { get; }

    [ObservableProperty]
    private Guid? currentCaseId;

    [ObservableProperty]
    private string organizationSearchQuery = string.Empty;

    [ObservableProperty]
    private OrganizationSummaryDto? selectedOrganization;

    [ObservableProperty]
    private string newOrganizationName = string.Empty;

    [ObservableProperty]
    private string selectedNewOrganizationType = OrganizationRegistryCatalog.OrganizationTypes[0];

    [ObservableProperty]
    private string selectedNewOrganizationStatus = OrganizationRegistryCatalog.OrganizationStatuses[0];

    [ObservableProperty]
    private OrganizationSummaryDto? selectedNewOrganizationParent;

    [ObservableProperty]
    private string newOrganizationSummary = string.Empty;

    [ObservableProperty]
    private string selectedOrganizationName = string.Empty;

    [ObservableProperty]
    private string selectedOrganizationType = OrganizationRegistryCatalog.OrganizationTypes[0];

    [ObservableProperty]
    private string selectedOrganizationStatus = OrganizationRegistryCatalog.OrganizationStatuses[0];

    [ObservableProperty]
    private OrganizationSummaryDto? selectedOrganizationParent;

    [ObservableProperty]
    private string selectedOrganizationSummaryText = string.Empty;

    [ObservableProperty]
    private OrganizationAliasDto? selectedOrganizationAlias;

    [ObservableProperty]
    private string newAliasText = string.Empty;

    [ObservableProperty]
    private string globalPersonSearchQuery = string.Empty;

    [ObservableProperty]
    private GlobalPersonSummary? selectedGlobalPersonSearchResult;

    [ObservableProperty]
    private OrganizationMembershipDto? selectedOrganizationMembership;

    [ObservableProperty]
    private string membershipRole = string.Empty;

    [ObservableProperty]
    private string membershipStatus = OrganizationRegistryCatalog.MembershipStatuses[0];

    [ObservableProperty]
    private string membershipConfidenceText = "50";

    [ObservableProperty]
    private string membershipBasisSummary = string.Empty;

    [ObservableProperty]
    private string membershipReviewer = string.Empty;

    [ObservableProperty]
    private string membershipReviewNotes = string.Empty;

    [ObservableProperty]
    private DateTime? membershipStartDateLocal;

    [ObservableProperty]
    private DateTime? membershipEndDateLocal;

    [ObservableProperty]
    private DateTime? membershipLastConfirmedDateLocal;

    [ObservableProperty]
    private bool isBusy;

    [ObservableProperty]
    private string statusText = "Organization registry ready.";

    public async Task SetCurrentCaseAsync(Guid? caseId, CancellationToken ct)
    {
        CurrentCaseId = caseId;
        if (_isActive)
        {
            await RefreshOrganizationsAsync(ct);
        }
    }

    public async Task ActivateAsync(CancellationToken ct)
    {
        _isActive = true;
        await RefreshOrganizationsAsync(ct);
    }

    public void Deactivate()
    {
        _isActive = false;
    }

    private async Task RefreshOrganizationsAsync(CancellationToken ct)
    {
        var selectedOrganizationId = SelectedOrganization?.OrganizationId;
        IsBusy = true;

        try
        {
            var organizations = await _organizationService.GetOrganizationsAsync(OrganizationSearchQuery, ct);

            Organizations.Clear();
            foreach (var organization in organizations)
            {
                Organizations.Add(organization);
            }

            UpdateAvailableParentOrganizations(selectedOrganizationId);

            SelectedOrganization = selectedOrganizationId.HasValue
                ? Organizations.FirstOrDefault(item => item.OrganizationId == selectedOrganizationId.Value)
                : Organizations.FirstOrDefault();

            if (SelectedOrganization is null)
            {
                ClearSelectedOrganizationState();
            }

            StatusText = organizations.Count == 0
                ? "No organizations found."
                : $"Loaded {organizations.Count:0} organization record(s).";
        }
        catch (Exception ex)
        {
            StatusText = $"Failed to load organizations: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task RefreshSelectedOrganizationDetailsAsync(CancellationToken ct)
    {
        if (SelectedOrganization is null)
        {
            ClearSelectedOrganizationDetails();
            return;
        }

        try
        {
            var details = await _organizationService.GetOrganizationDetailsAsync(
                SelectedOrganization.OrganizationId,
                ct
            );
            if (details is null || SelectedOrganization?.OrganizationId != details.Organization.OrganizationId)
            {
                ClearSelectedOrganizationDetails();
                return;
            }

            SyncAliases(details.Aliases);
            SyncMemberships(details.Memberships);
            SyncChildren(details.Children);
            SelectedOrganizationParent = details.Organization.ParentOrganizationId.HasValue
                ? AvailableParentOrganizations.FirstOrDefault(
                    item => item.OrganizationId == details.Organization.ParentOrganizationId.Value
                )
                : null;
        }
        catch (Exception ex)
        {
            StatusText = $"Failed to load organization details: {ex.Message}";
        }
    }

    private async Task CreateOrganizationAsync()
    {
        try
        {
            var created = await _organizationService.CreateOrganizationAsync(
                new CreateOrganizationRequest(
                    NewOrganizationName,
                    SelectedNewOrganizationType,
                    SelectedNewOrganizationStatus,
                    SelectedNewOrganizationParent?.OrganizationId,
                    NewOrganizationSummary
                ),
                CancellationToken.None
            );

            ClearNewOrganizationInputs();
            await RefreshOrganizationsAsync(CancellationToken.None);
            SelectedOrganization = Organizations.FirstOrDefault(
                item => item.OrganizationId == created.OrganizationId
            );
            StatusText = $"Created organization \"{created.Name}\".";
        }
        catch (Exception ex)
        {
            StatusText = $"Failed to create organization: {ex.Message}";
        }
    }

    private async Task SaveSelectedOrganizationAsync()
    {
        if (SelectedOrganization is null)
        {
            return;
        }

        try
        {
            var updated = await _organizationService.UpdateOrganizationAsync(
                new UpdateOrganizationRequest(
                    SelectedOrganization.OrganizationId,
                    SelectedOrganizationName,
                    SelectedOrganizationType,
                    SelectedOrganizationStatus,
                    SelectedOrganizationParent?.OrganizationId,
                    SelectedOrganizationSummaryText
                ),
                CancellationToken.None
            );

            await RefreshOrganizationsAsync(CancellationToken.None);
            SelectedOrganization = Organizations.FirstOrDefault(
                item => item.OrganizationId == updated.OrganizationId
            );
            StatusText = $"Saved organization \"{updated.Name}\".";
        }
        catch (Exception ex)
        {
            StatusText = $"Failed to save organization: {ex.Message}";
        }
    }

    private async Task AddAliasAsync()
    {
        if (SelectedOrganization is null)
        {
            return;
        }

        try
        {
            await _organizationService.AddAliasAsync(
                new AddOrganizationAliasRequest(
                    SelectedOrganization.OrganizationId,
                    NewAliasText,
                    Notes: null
                ),
                CancellationToken.None
            );

            NewAliasText = string.Empty;
            await RefreshOrganizationsAsync(CancellationToken.None);
            await RefreshSelectedOrganizationDetailsAsync(CancellationToken.None);
            StatusText = "Organization alias added.";
        }
        catch (Exception ex)
        {
            StatusText = $"Failed to add alias: {ex.Message}";
        }
    }

    private async Task RemoveAliasAsync()
    {
        if (SelectedOrganizationAlias is null)
        {
            return;
        }

        try
        {
            await _organizationService.RemoveAliasAsync(
                SelectedOrganizationAlias.AliasId,
                CancellationToken.None
            );

            await RefreshOrganizationsAsync(CancellationToken.None);
            await RefreshSelectedOrganizationDetailsAsync(CancellationToken.None);
            StatusText = "Organization alias removed.";
        }
        catch (Exception ex)
        {
            StatusText = $"Failed to remove alias: {ex.Message}";
        }
    }

    private async Task SearchGlobalPersonsAsync()
    {
        try
        {
            var results = await _targetRegistryService.SearchGlobalPersonsAsync(
                GlobalPersonSearchQuery,
                take: 50,
                CancellationToken.None
            );

            GlobalPersonSearchResults.Clear();
            foreach (var result in results)
            {
                GlobalPersonSearchResults.Add(result);
            }

            SelectedGlobalPersonSearchResult = GlobalPersonSearchResults.FirstOrDefault(
                item => SelectedGlobalPersonSearchResult is not null
                    && item.GlobalEntityId == SelectedGlobalPersonSearchResult.GlobalEntityId
            ) ?? GlobalPersonSearchResults.FirstOrDefault();

            StatusText = results.Count == 0
                ? "No global people matched the search."
                : $"Loaded {results.Count:0} global person result(s).";
        }
        catch (Exception ex)
        {
            StatusText = $"Failed to search global people: {ex.Message}";
        }
    }

    private async Task AddMembershipAsync()
    {
        if (SelectedOrganization is null || SelectedGlobalPersonSearchResult is null)
        {
            return;
        }

        try
        {
            var membership = await _organizationService.AddMembershipAsync(
                new AddOrganizationMembershipRequest(
                    SelectedOrganization.OrganizationId,
                    SelectedGlobalPersonSearchResult.GlobalEntityId,
                    MembershipRole,
                    MembershipStatus,
                    ParseConfidence(),
                    MembershipBasisSummary,
                    ConvertLocalDateToStartUtc(MembershipStartDateLocal),
                    ConvertLocalDateToStartUtc(MembershipEndDateLocal),
                    ConvertLocalDateToStartUtc(MembershipLastConfirmedDateLocal),
                    MembershipReviewer,
                    MembershipReviewNotes
                ),
                CancellationToken.None
            );

            ClearMembershipEditor();
            await RefreshOrganizationsAsync(CancellationToken.None);
            await RefreshSelectedOrganizationDetailsAsync(CancellationToken.None);
            SelectedOrganizationMembership = SelectedOrganizationMemberships.FirstOrDefault(
                item => item.MembershipId == membership.MembershipId
            );
            StatusText = "Organization membership added.";
        }
        catch (Exception ex)
        {
            StatusText = $"Failed to add membership: {ex.Message}";
        }
    }

    private async Task UpdateMembershipAsync()
    {
        if (SelectedOrganizationMembership is null)
        {
            return;
        }

        try
        {
            var updated = await _organizationService.UpdateMembershipAsync(
                new UpdateOrganizationMembershipRequest(
                    SelectedOrganizationMembership.MembershipId,
                    MembershipRole,
                    MembershipStatus,
                    ParseConfidence(),
                    MembershipBasisSummary,
                    ConvertLocalDateToStartUtc(MembershipStartDateLocal),
                    ConvertLocalDateToStartUtc(MembershipEndDateLocal),
                    ConvertLocalDateToStartUtc(MembershipLastConfirmedDateLocal),
                    MembershipReviewer,
                    MembershipReviewNotes
                ),
                CancellationToken.None
            );

            await RefreshOrganizationsAsync(CancellationToken.None);
            await RefreshSelectedOrganizationDetailsAsync(CancellationToken.None);
            SelectedOrganizationMembership = SelectedOrganizationMemberships.FirstOrDefault(
                item => item.MembershipId == updated.MembershipId
            );
            StatusText = "Organization membership updated.";
        }
        catch (Exception ex)
        {
            StatusText = $"Failed to update membership: {ex.Message}";
        }
    }

    private async Task RemoveMembershipAsync()
    {
        if (SelectedOrganizationMembership is null)
        {
            return;
        }

        try
        {
            await _organizationService.RemoveMembershipAsync(
                SelectedOrganizationMembership.MembershipId,
                CancellationToken.None
            );

            ClearMembershipEditor();
            await RefreshOrganizationsAsync(CancellationToken.None);
            await RefreshSelectedOrganizationDetailsAsync(CancellationToken.None);
            StatusText = "Organization membership removed.";
        }
        catch (Exception ex)
        {
            StatusText = $"Failed to remove membership: {ex.Message}";
        }
    }

    partial void OnOrganizationSearchQueryChanged(string value)
    {
        if (_isActive)
        {
            RefreshOrganizationsAsync(CancellationToken.None).Forget("RefreshOrganizationsOnSearchChanged");
        }
    }

    partial void OnSelectedOrganizationChanged(OrganizationSummaryDto? value)
    {
        OnPropertyChanged(nameof(HasSelectedOrganization));
        SaveSelectedOrganizationCommand.NotifyCanExecuteChanged();
        AddAliasCommand.NotifyCanExecuteChanged();
        AddMembershipCommand.NotifyCanExecuteChanged();
        UpdateMembershipCommand.NotifyCanExecuteChanged();
        RemoveMembershipCommand.NotifyCanExecuteChanged();

        if (value is null)
        {
            ClearSelectedOrganizationState();
            return;
        }

        SelectedOrganizationName = value.Name;
        SelectedOrganizationType = value.Type;
        SelectedOrganizationStatus = value.Status;
        SelectedOrganizationSummaryText = value.Summary ?? string.Empty;
        UpdateAvailableParentOrganizations(value.OrganizationId);
        SelectedOrganizationParent = value.ParentOrganizationId.HasValue
            ? AvailableParentOrganizations.FirstOrDefault(
                item => item.OrganizationId == value.ParentOrganizationId.Value
            )
            : null;

        RefreshSelectedOrganizationDetailsAsync(CancellationToken.None).Forget(
            "RefreshSelectedOrganizationDetailsOnSelectionChanged"
        );
    }

    partial void OnSelectedOrganizationAliasChanged(OrganizationAliasDto? value)
    {
        OnPropertyChanged(nameof(HasSelectedAlias));
        RemoveAliasCommand.NotifyCanExecuteChanged();
    }

    partial void OnSelectedOrganizationMembershipChanged(OrganizationMembershipDto? value)
    {
        OnPropertyChanged(nameof(HasSelectedMembership));
        UpdateMembershipCommand.NotifyCanExecuteChanged();
        RemoveMembershipCommand.NotifyCanExecuteChanged();

        if (value is null)
        {
            ClearMembershipEditor();
            return;
        }

        MembershipRole = value.Role ?? string.Empty;
        MembershipStatus = value.Status;
        MembershipConfidenceText = value.Confidence.ToString(CultureInfo.InvariantCulture);
        MembershipBasisSummary = value.BasisSummary ?? string.Empty;
        MembershipReviewer = value.Reviewer ?? string.Empty;
        MembershipReviewNotes = value.ReviewNotes ?? string.Empty;
        MembershipStartDateLocal = ConvertUtcDateToLocalDate(value.StartDateUtc);
        MembershipEndDateLocal = ConvertUtcDateToLocalDate(value.EndDateUtc);
        MembershipLastConfirmedDateLocal = ConvertUtcDateToLocalDate(value.LastConfirmedDateUtc);
    }

    partial void OnNewOrganizationNameChanged(string value)
    {
        OnPropertyChanged(nameof(CanCreateOrganization));
        CreateOrganizationCommand.NotifyCanExecuteChanged();
    }

    partial void OnNewAliasTextChanged(string value)
    {
        OnPropertyChanged(nameof(CanAddAlias));
        AddAliasCommand.NotifyCanExecuteChanged();
    }

    partial void OnSelectedGlobalPersonSearchResultChanged(GlobalPersonSummary? value)
    {
        OnPropertyChanged(nameof(HasSelectedGlobalPersonSearchResult));
        OnPropertyChanged(nameof(CanAddMembership));
        AddMembershipCommand.NotifyCanExecuteChanged();
    }

    public void Dispose()
    {
        if (_isDisposed)
        {
            return;
        }

        _isDisposed = true;
    }

    private void UpdateAvailableParentOrganizations(Guid? selectedOrganizationId)
    {
        AvailableParentOrganizations.Clear();
        foreach (var organization in Organizations.Where(item => item.OrganizationId != selectedOrganizationId))
        {
            AvailableParentOrganizations.Add(organization);
        }
    }

    private void SyncAliases(IReadOnlyList<OrganizationAliasDto> aliases)
    {
        var selectedAliasId = SelectedOrganizationAlias?.AliasId;
        SelectedOrganizationAliases.Clear();
        foreach (var alias in aliases)
        {
            SelectedOrganizationAliases.Add(alias);
        }

        SelectedOrganizationAlias = selectedAliasId.HasValue
            ? SelectedOrganizationAliases.FirstOrDefault(item => item.AliasId == selectedAliasId.Value)
            : SelectedOrganizationAliases.FirstOrDefault();
    }

    private void SyncMemberships(IReadOnlyList<OrganizationMembershipDto> memberships)
    {
        var selectedMembershipId = SelectedOrganizationMembership?.MembershipId;
        SelectedOrganizationMemberships.Clear();
        foreach (var membership in memberships)
        {
            SelectedOrganizationMemberships.Add(membership);
        }

        SelectedOrganizationMembership = selectedMembershipId.HasValue
            ? SelectedOrganizationMemberships.FirstOrDefault(
                item => item.MembershipId == selectedMembershipId.Value
            )
            : SelectedOrganizationMemberships.FirstOrDefault();
    }

    private void SyncChildren(IReadOnlyList<OrganizationSummaryDto> children)
    {
        SelectedOrganizationChildren.Clear();
        foreach (var child in children)
        {
            SelectedOrganizationChildren.Add(child);
        }
    }

    private void ClearNewOrganizationInputs()
    {
        NewOrganizationName = string.Empty;
        SelectedNewOrganizationType = OrganizationTypes[0];
        SelectedNewOrganizationStatus = OrganizationStatuses[0];
        SelectedNewOrganizationParent = null;
        NewOrganizationSummary = string.Empty;
    }

    private void ClearSelectedOrganizationState()
    {
        SelectedOrganizationName = string.Empty;
        SelectedOrganizationType = OrganizationTypes[0];
        SelectedOrganizationStatus = OrganizationStatuses[0];
        SelectedOrganizationParent = null;
        SelectedOrganizationSummaryText = string.Empty;
        ClearSelectedOrganizationDetails();
    }

    private void ClearSelectedOrganizationDetails()
    {
        SelectedOrganizationAliases.Clear();
        SelectedOrganizationMemberships.Clear();
        SelectedOrganizationChildren.Clear();
        SelectedOrganizationAlias = null;
        SelectedOrganizationMembership = null;
        OnPropertyChanged(nameof(HasSelectedAlias));
        OnPropertyChanged(nameof(HasSelectedMembership));
    }

    private void ClearMembershipEditor()
    {
        MembershipRole = string.Empty;
        MembershipStatus = MembershipStatuses[0];
        MembershipConfidenceText = "50";
        MembershipBasisSummary = string.Empty;
        MembershipReviewer = string.Empty;
        MembershipReviewNotes = string.Empty;
        MembershipStartDateLocal = null;
        MembershipEndDateLocal = null;
        MembershipLastConfirmedDateLocal = null;
    }

    private int ParseConfidence()
    {
        if (!int.TryParse(
                MembershipConfidenceText,
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out var parsed)
            && !int.TryParse(
                MembershipConfidenceText,
                NumberStyles.Integer,
                CultureInfo.CurrentCulture,
                out parsed))
        {
            throw new ArgumentException("Confidence must be a whole number between 0 and 100.");
        }

        return parsed;
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

    private static DateTime? ConvertUtcDateToLocalDate(DateTimeOffset? value)
    {
        return value?.ToLocalTime().Date;
    }
}
