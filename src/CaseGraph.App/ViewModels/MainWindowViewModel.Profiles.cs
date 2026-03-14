using CaseGraph.App.Models;
using CaseGraph.App.Views.Pages;
using CaseGraph.Core.Diagnostics;
using CaseGraph.Core.Models;
using CommunityToolkit.Mvvm.Input;

namespace CaseGraph.App.ViewModels;

public partial class MainWindowViewModel
{
    private readonly PersonProfileViewModel _personProfile;
    private readonly OrganizationProfileViewModel _organizationProfile;
    private readonly Stack<WorkspaceContentState> _workspaceHistory = new();
    private WorkspaceContentState _currentWorkspaceState = WorkspaceContentState.ForNavigation(
        NavigationPage.Dashboard
    );

    public bool HasSelectedTargetAliases => SelectedTargetAliases.Count > 0;

    public bool HasSelectedTargetIdentifiers => SelectedTargetIdentifiers.Count > 0;

    public IAsyncRelayCommand OpenSelectedTargetProfileCommand { get; private set; } = null!;

    public IAsyncRelayCommand<TargetSummary?> OpenTargetProfileCommand { get; private set; } = null!;

    private void ConfigureProfileNavigation()
    {
        OrganizationRegistry.OpenOrganizationProfileRequested = organizationId =>
            OpenOrganizationProfileAsync(organizationId).Forget(
                "OpenOrganizationProfileFromRegistry",
                caseId: _appSessionState.CurrentCaseId,
                evidenceId: _appSessionState.CurrentEvidenceId
            );
        _personProfile.OpenOrganizationProfileRequested = organizationId =>
            OpenOrganizationProfileAsync(organizationId).Forget(
                "OpenOrganizationProfileFromPersonProfile",
                caseId: _appSessionState.CurrentCaseId,
                evidenceId: _appSessionState.CurrentEvidenceId
            );
        _personProfile.NavigateBackRequested = () =>
            NavigateBackFromProfileAsync().Forget(
                "NavigateBackFromPersonProfile",
                caseId: _appSessionState.CurrentCaseId,
                evidenceId: _appSessionState.CurrentEvidenceId
            );
        _organizationProfile.OpenOrganizationProfileRequested = organizationId =>
            OpenOrganizationProfileAsync(organizationId).Forget(
                "OpenOrganizationProfileFromOrganizationProfile",
                caseId: _appSessionState.CurrentCaseId,
                evidenceId: _appSessionState.CurrentEvidenceId
            );
        _organizationProfile.OpenPersonProfileRequested = (globalEntityId, displayName) =>
            OpenPersonProfileForGlobalPersonAsync(globalEntityId, displayName).Forget(
                "OpenPersonProfileFromOrganizationProfile",
                caseId: _appSessionState.CurrentCaseId,
                evidenceId: _appSessionState.CurrentEvidenceId
            );
        _organizationProfile.NavigateBackRequested = () =>
            NavigateBackFromProfileAsync().Forget(
                "NavigateBackFromOrganizationProfile",
                caseId: _appSessionState.CurrentCaseId,
                evidenceId: _appSessionState.CurrentEvidenceId
            );
    }

    private async Task OpenSelectedTargetProfileAsync()
    {
        await OpenTargetProfileAsync(SelectedTargetSummary);
    }

    private async Task OpenTargetProfileAsync(TargetSummary? targetSummary, bool pushHistory = true)
    {
        if (targetSummary is null)
        {
            OperationText = "Select a target before opening the profile.";
            return;
        }

        if (CurrentCaseInfo is null)
        {
            OperationText = "Open a case before opening a person profile.";
            return;
        }

        var targetState = WorkspaceContentState.ForPerson(targetSummary.TargetId, targetSummary.DisplayName);
        if (pushHistory)
        {
            PushCurrentWorkspaceState(targetState);
        }

        await _personProfile.LoadAsync(CurrentCaseInfo.CaseId, targetSummary.TargetId, CancellationToken.None);
        CurrentView = new PersonProfileView
        {
            DataContext = _personProfile
        };
        _currentWorkspaceState = targetState;
    }

    private async Task OpenOrganizationProfileAsync(Guid organizationId, bool pushHistory = true)
    {
        if (organizationId == Guid.Empty)
        {
            OperationText = "Select an organization before opening the profile.";
            return;
        }

        var targetState = WorkspaceContentState.ForOrganization(organizationId);
        if (pushHistory)
        {
            PushCurrentWorkspaceState(targetState);
        }

        await _organizationProfile.LoadAsync(organizationId, CancellationToken.None);
        CurrentView = new OrganizationProfileView
        {
            DataContext = _organizationProfile
        };
        _currentWorkspaceState = targetState;
    }

    private async Task OpenPersonProfileForGlobalPersonAsync(
        Guid globalEntityId,
        string? fallbackDisplayName,
        bool pushHistory = true
    )
    {
        if (CurrentCaseInfo is null)
        {
            if (pushHistory)
            {
                PushCurrentWorkspaceState(WorkspaceContentState.ForUnavailablePerson(fallbackDisplayName));
            }

            _personProfile.ShowUnavailableState(fallbackDisplayName ?? "Record no longer available");
            CurrentView = new PersonProfileView
            {
                DataContext = _personProfile
            };
            _currentWorkspaceState = WorkspaceContentState.ForUnavailablePerson(fallbackDisplayName);
            return;
        }

        var targets = await _targetRegistryService.GetTargetsAsync(
            CurrentCaseInfo.CaseId,
            search: null,
            CancellationToken.None
        );
        var target = targets.FirstOrDefault(item => item.GlobalEntityId == globalEntityId);
        if (target is not null)
        {
            await OpenTargetProfileAsync(target, pushHistory);
            return;
        }

        if (pushHistory)
        {
            PushCurrentWorkspaceState(WorkspaceContentState.ForUnavailablePerson(fallbackDisplayName));
        }

        _personProfile.ShowUnavailableState(fallbackDisplayName ?? "Record no longer available");
        CurrentView = new PersonProfileView
        {
            DataContext = _personProfile
        };
        _currentWorkspaceState = WorkspaceContentState.ForUnavailablePerson(fallbackDisplayName);
    }

    private async Task NavigateBackFromProfileAsync()
    {
        if (_workspaceHistory.Count == 0)
        {
            return;
        }

        var previous = _workspaceHistory.Pop();
        await RestoreWorkspaceStateAsync(previous);
    }

    private async Task RestoreWorkspaceStateAsync(WorkspaceContentState state)
    {
        switch (state.Kind)
        {
            case WorkspaceContentKind.Navigation:
                RestoreNavigationPageView(state.Page!.Value);
                break;
            case WorkspaceContentKind.PersonProfile:
                if (state.EntityId.HasValue)
                {
                    var target = Targets.FirstOrDefault(item => item.TargetId == state.EntityId.Value)
                        ?? (CurrentCaseInfo is null
                            ? null
                            : (await _targetRegistryService.GetTargetsAsync(
                                CurrentCaseInfo.CaseId,
                                search: null,
                                CancellationToken.None
                            )).FirstOrDefault(item => item.TargetId == state.EntityId.Value));
                    if (target is not null)
                    {
                        await OpenTargetProfileAsync(target, pushHistory: false);
                    }
                    else
                    {
                        _personProfile.ShowUnavailableState(state.DisplayName ?? "Record no longer available");
                        CurrentView = new PersonProfileView
                        {
                            DataContext = _personProfile
                        };
                        _currentWorkspaceState = WorkspaceContentState.ForUnavailablePerson(
                            state.DisplayName
                        );
                    }
                }
                break;
            case WorkspaceContentKind.OrganizationProfile:
                if (state.EntityId.HasValue)
                {
                    await OpenOrganizationProfileAsync(state.EntityId.Value, pushHistory: false);
                }
                break;
            case WorkspaceContentKind.UnavailablePersonProfile:
                _personProfile.ShowUnavailableState(state.DisplayName ?? "Record no longer available");
                CurrentView = new PersonProfileView
                {
                    DataContext = _personProfile
                };
                _currentWorkspaceState = state;
                break;
        }
    }

    private void RestoreNavigationPageView(NavigationPage page)
    {
        CurrentView = _navigationService.CreateView(page);
        _currentWorkspaceState = WorkspaceContentState.ForNavigation(page);
        ApplyNavigationPageActivation(page);
    }

    private void ApplyNavigationPageActivation(NavigationPage page)
    {
        if (page == NavigationPage.Timeline)
        {
            Timeline.ActivateAsync(CancellationToken.None).Forget(
                "ActivateTimelineOnNavigate",
                caseId: _appSessionState.CurrentCaseId,
                evidenceId: _appSessionState.CurrentEvidenceId
            );
        }
        else
        {
            Timeline.Deactivate();
        }

        if (page == NavigationPage.IncidentWindow)
        {
            OpenIncidentWorkspace.ActivateAsync(CancellationToken.None).Forget(
                "ActivateOpenIncidentWorkspaceOnNavigate",
                caseId: _appSessionState.CurrentCaseId,
                evidenceId: _appSessionState.CurrentEvidenceId
            );
        }
        else
        {
            OpenIncidentWorkspace.Deactivate();
        }

        if (page == NavigationPage.Locations)
        {
            Locations.ActivateAsync(CancellationToken.None).Forget(
                "ActivateLocationsOnNavigate",
                caseId: _appSessionState.CurrentCaseId,
                evidenceId: _appSessionState.CurrentEvidenceId
            );
        }
        else
        {
            Locations.Deactivate();
        }

        if (page == NavigationPage.Organizations)
        {
            OrganizationRegistry.ActivateAsync(CancellationToken.None).Forget(
                "ActivateOrganizationsOnNavigate",
                caseId: _appSessionState.CurrentCaseId,
                evidenceId: _appSessionState.CurrentEvidenceId
            );
        }
        else
        {
            OrganizationRegistry.Deactivate();
        }

        if (page == NavigationPage.Reports)
        {
            Reports.ActivateAsync(CancellationToken.None).Forget(
                "ActivateReportsOnNavigate",
                caseId: _appSessionState.CurrentCaseId,
                evidenceId: _appSessionState.CurrentEvidenceId
            );
        }
        else
        {
            Reports.Deactivate();
        }
    }

    private void PushCurrentWorkspaceState(WorkspaceContentState targetState)
    {
        if (_currentWorkspaceState == targetState)
        {
            return;
        }

        _workspaceHistory.Push(_currentWorkspaceState);
    }

    private void ClearWorkspaceHistory()
    {
        _workspaceHistory.Clear();
    }

    private enum WorkspaceContentKind
    {
        Navigation,
        PersonProfile,
        OrganizationProfile,
        UnavailablePersonProfile
    }

    private sealed record WorkspaceContentState(
        WorkspaceContentKind Kind,
        NavigationPage? Page,
        Guid? EntityId,
        string? DisplayName
    )
    {
        public static WorkspaceContentState ForNavigation(NavigationPage page)
            => new(WorkspaceContentKind.Navigation, page, null, null);

        public static WorkspaceContentState ForPerson(Guid targetId, string? displayName)
            => new(WorkspaceContentKind.PersonProfile, null, targetId, displayName);

        public static WorkspaceContentState ForOrganization(Guid organizationId)
            => new(WorkspaceContentKind.OrganizationProfile, null, organizationId, null);

        public static WorkspaceContentState ForUnavailablePerson(string? displayName)
            => new(WorkspaceContentKind.UnavailablePersonProfile, null, null, displayName);
    }
}
