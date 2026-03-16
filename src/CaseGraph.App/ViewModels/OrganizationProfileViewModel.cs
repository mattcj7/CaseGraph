using CaseGraph.Infrastructure.Organizations;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System.Collections.ObjectModel;

namespace CaseGraph.App.ViewModels;

public sealed partial class OrganizationProfileViewModel : ObservableObject
{
    private readonly IOrganizationService _organizationService;

    public OrganizationProfileViewModel(IOrganizationService organizationService)
    {
        _organizationService = organizationService;
        OpenMembershipPersonCommand = new AsyncRelayCommand<OrganizationMembershipProfileItem?>(
            OpenMembershipPersonAsync
        );
        OpenChildOrganizationCommand = new AsyncRelayCommand<OrganizationSummaryDto?>(
            OpenChildOrganizationAsync
        );
        NavigateBackCommand = new RelayCommand(() => NavigateBackRequested?.Invoke());
    }

    public Action<Guid, string?>? OpenPersonProfileRequested { get; set; }

    public Action<Guid>? OpenOrganizationProfileRequested { get; set; }

    public Action? NavigateBackRequested { get; set; }

    public ObservableCollection<string> Aliases { get; } = new();

    public ObservableCollection<OrganizationMembershipProfileItem> Memberships { get; } = new();

    public ObservableCollection<OrganizationSummaryDto> Children { get; } = new();

    public IAsyncRelayCommand<OrganizationMembershipProfileItem?> OpenMembershipPersonCommand { get; }

    public IAsyncRelayCommand<OrganizationSummaryDto?> OpenChildOrganizationCommand { get; }

    public IRelayCommand NavigateBackCommand { get; }

    public bool HasAliases => Aliases.Count > 0;

    public bool HasMemberships => Memberships.Count > 0;

    public bool HasChildren => Children.Count > 0;

    public string OrganizationTypeDisplay => string.IsNullOrWhiteSpace(OrganizationType)
        ? "(none)"
        : OrganizationType;

    public string OrganizationStatusDisplay => string.IsNullOrWhiteSpace(OrganizationStatus)
        ? "(none)"
        : OrganizationStatus;

    public string ParentOrganizationDisplay => string.IsNullOrWhiteSpace(ParentOrganizationName)
        ? "(none)"
        : ParentOrganizationName;

    public string SummaryDisplay => string.IsNullOrWhiteSpace(Summary)
        ? "No summary recorded"
        : Summary;

    public string AliasesEmptyState => "No aliases recorded";

    public string MembershipsEmptyState => "No organization memberships recorded";

    public string ChildrenEmptyState => "No child organizations recorded";

    [ObservableProperty]
    private Guid? currentOrganizationId;

    [ObservableProperty]
    private bool isBusy;

    [ObservableProperty]
    private bool isRecordAvailable;

    [ObservableProperty]
    private string headerTitle = "Organization Profile";

    [ObservableProperty]
    private string headerSubtitle = "Record no longer available";

    [ObservableProperty]
    private string organizationName = string.Empty;

    [ObservableProperty]
    private string organizationType = string.Empty;

    [ObservableProperty]
    private string organizationStatus = string.Empty;

    [ObservableProperty]
    private string parentOrganizationName = string.Empty;

    [ObservableProperty]
    private string summary = string.Empty;

    [ObservableProperty]
    private DateTimeOffset? createdAtUtc;

    [ObservableProperty]
    private DateTimeOffset? updatedAtUtc;

    [ObservableProperty]
    private OrganizationMembershipProfileItem? selectedMembership;

    public async Task LoadAsync(Guid organizationId, CancellationToken ct)
    {
        CurrentOrganizationId = organizationId;
        IsBusy = true;

        try
        {
            var details = await _organizationService.GetOrganizationDetailsAsync(organizationId, ct);
            if (details is null)
            {
                ShowUnavailableState("Record no longer available");
                return;
            }

            ApplyDetails(details);
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
        OrganizationName = string.Empty;
        OrganizationType = string.Empty;
        OrganizationStatus = string.Empty;
        ParentOrganizationName = string.Empty;
        Summary = string.Empty;
        CreatedAtUtc = null;
        UpdatedAtUtc = null;
        Aliases.Clear();
        Memberships.Clear();
        Children.Clear();
        RaiseSectionStateChanged();
    }

    private void ApplyDetails(OrganizationDetailsDto details)
    {
        var organization = details.Organization;
        IsRecordAvailable = true;
        HeaderTitle = organization.Name;
        HeaderSubtitle = $"{organization.Type} / {organization.Status}";
        OrganizationName = organization.Name;
        OrganizationType = organization.Type;
        OrganizationStatus = organization.Status;
        ParentOrganizationName = organization.ParentOrganizationName ?? string.Empty;
        Summary = organization.Summary ?? string.Empty;
        CreatedAtUtc = organization.CreatedAtUtc;
        UpdatedAtUtc = organization.UpdatedAtUtc;

        Aliases.Clear();
        foreach (var alias in details.Aliases.Select(item => item.Alias))
        {
            Aliases.Add(alias);
        }

        Memberships.Clear();
        foreach (var membership in details.Memberships)
        {
            Memberships.Add(new OrganizationMembershipProfileItem(
                membership.GlobalEntityId,
                membership.GlobalDisplayName,
                BuildMembershipDetail(membership),
                membership.DocumentationStatusDisplay,
                membership.DocumentationLinkageSummary,
                membership.HasGangDocumentation ? "Open Documentation" : "Create Documentation"
            ));
        }

        Children.Clear();
        foreach (var child in details.Children)
        {
            Children.Add(child);
        }

        RaiseSectionStateChanged();
    }

    private async Task OpenMembershipPersonAsync(OrganizationMembershipProfileItem? item)
    {
        if (item is null)
        {
            return;
        }

        OpenPersonProfileRequested?.Invoke(item.GlobalEntityId, item.DisplayName);
        await Task.CompletedTask;
    }

    private async Task OpenChildOrganizationAsync(OrganizationSummaryDto? item)
    {
        if (item is null)
        {
            return;
        }

        OpenOrganizationProfileRequested?.Invoke(item.OrganizationId);
        await Task.CompletedTask;
    }

    private void RaiseSectionStateChanged()
    {
        OnPropertyChanged(nameof(HasAliases));
        OnPropertyChanged(nameof(HasMemberships));
        OnPropertyChanged(nameof(HasChildren));
        OnPropertyChanged(nameof(OrganizationTypeDisplay));
        OnPropertyChanged(nameof(OrganizationStatusDisplay));
        OnPropertyChanged(nameof(ParentOrganizationDisplay));
        OnPropertyChanged(nameof(SummaryDisplay));
    }

    private static string BuildMembershipDetail(OrganizationMembershipDto membership)
    {
        var parts = new List<string>
        {
            $"Status: {membership.Status}",
            $"Confidence: {membership.Confidence:0}"
        };

        if (!string.IsNullOrWhiteSpace(membership.Role))
        {
            parts.Add($"Role: {membership.Role}");
        }

        if (membership.LastConfirmedDateUtc.HasValue)
        {
            parts.Add($"Last confirmed: {membership.LastConfirmedDateUtc.Value:yyyy-MM-dd}");
        }

        return string.Join(" | ", parts);
    }

    public sealed record OrganizationMembershipProfileItem(
        Guid GlobalEntityId,
        string DisplayName,
        string DetailText,
        string DocumentationStatus,
        string? DocumentationLinkageSummary,
        string ActionLabel
    )
    {
        public string DocumentationDetailDisplay => string.IsNullOrWhiteSpace(DocumentationLinkageSummary)
            ? DocumentationStatus
            : $"{DocumentationStatus} | {DocumentationLinkageSummary}";
    }
}
