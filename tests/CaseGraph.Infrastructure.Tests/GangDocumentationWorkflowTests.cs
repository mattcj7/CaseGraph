using CaseGraph.Infrastructure.GangDocumentation;
using CaseGraph.Infrastructure.Organizations;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace CaseGraph.Infrastructure.Tests;

public sealed class GangDocumentationWorkflowTests
{
    [Fact]
    public async Task TransitionWorkflowAsync_AllowsValidForwardTransitions()
    {
        await using var fixture = await GangDocumentationTestWorkspaceFixture.CreateAsync();
        var service = fixture.Services.GetRequiredService<IGangDocumentationService>();
        var organizationService = fixture.Services.GetRequiredService<IOrganizationService>();

        var target = await fixture.CreateTargetAsync("Tyrone Hill", createGlobalPerson: true);
        var organization = await organizationService.CreateOrganizationAsync(
            new CreateOrganizationRequest("Hoover Criminals", "gang", "active", null, null),
            CancellationToken.None
        );
        var documentation = await service.CreateDocumentationAsync(
            new CreateGangDocumentationRequest(
                target.CaseId,
                target.TargetId,
                organization.OrganizationId,
                null,
                "suspected",
                "Initial documentation.",
                null
            ),
            CancellationToken.None
        );

        var submitted = await service.TransitionWorkflowAsync(
            new TransitionGangDocumentationWorkflowRequest(
                target.CaseId,
                documentation.DocumentationId,
                GangDocumentationCatalog.WorkflowActionSubmitForReview,
                null,
                null
            ),
            CancellationToken.None
        );
        var approved = await service.TransitionWorkflowAsync(
            new TransitionGangDocumentationWorkflowRequest(
                target.CaseId,
                documentation.DocumentationId,
                GangDocumentationCatalog.WorkflowActionApprove,
                "Lieutenant Shaw",
                "Meets supervisor review standard."
            ),
            CancellationToken.None
        );
        var inactive = await service.TransitionWorkflowAsync(
            new TransitionGangDocumentationWorkflowRequest(
                target.CaseId,
                documentation.DocumentationId,
                GangDocumentationCatalog.WorkflowActionMarkInactive,
                "Lieutenant Shaw",
                "No longer active for current enforcement package."
            ),
            CancellationToken.None
        );
        var purgeReview = await service.TransitionWorkflowAsync(
            new TransitionGangDocumentationWorkflowRequest(
                target.CaseId,
                documentation.DocumentationId,
                GangDocumentationCatalog.WorkflowActionMarkPurgeReview,
                "Lieutenant Shaw",
                "Move to administrative purge review."
            ),
            CancellationToken.None
        );

        Assert.Equal(GangDocumentationCatalog.WorkflowStatusPendingSupervisorReview, submitted.Review.WorkflowStatus);
        Assert.Equal(GangDocumentationCatalog.WorkflowStatusApproved, approved.Review.WorkflowStatus);
        Assert.Equal(GangDocumentationCatalog.WorkflowStatusInactive, inactive.Review.WorkflowStatus);
        Assert.Equal(GangDocumentationCatalog.WorkflowStatusPurgeReview, purgeReview.Review.WorkflowStatus);
    }

    [Fact]
    public async Task TransitionWorkflowAsync_AllowsReversiblePurgeReviewTransitions()
    {
        await using var fixture = await GangDocumentationTestWorkspaceFixture.CreateAsync();
        var service = fixture.Services.GetRequiredService<IGangDocumentationService>();
        var organizationService = fixture.Services.GetRequiredService<IOrganizationService>();

        var target = await fixture.CreateTargetAsync("Nia Brooks", createGlobalPerson: true);
        var organization = await organizationService.CreateOrganizationAsync(
            new CreateOrganizationRequest("Bounty Hunters", "gang", "active", null, null),
            CancellationToken.None
        );
        var documentation = await service.CreateDocumentationAsync(
            new CreateGangDocumentationRequest(
                target.CaseId,
                target.TargetId,
                organization.OrganizationId,
                null,
                "associate",
                "Workflow clarity regression record.",
                null
            ),
            CancellationToken.None
        );

        await service.TransitionWorkflowAsync(
            new TransitionGangDocumentationWorkflowRequest(
                target.CaseId,
                documentation.DocumentationId,
                GangDocumentationCatalog.WorkflowActionSubmitForReview,
                null,
                null
            ),
            CancellationToken.None
        );
        await service.TransitionWorkflowAsync(
            new TransitionGangDocumentationWorkflowRequest(
                target.CaseId,
                documentation.DocumentationId,
                GangDocumentationCatalog.WorkflowActionApprove,
                "Captain Vega",
                "Approved for active use."
            ),
            CancellationToken.None
        );
        await service.TransitionWorkflowAsync(
            new TransitionGangDocumentationWorkflowRequest(
                target.CaseId,
                documentation.DocumentationId,
                GangDocumentationCatalog.WorkflowActionMarkPurgeReview,
                "Captain Vega",
                "Review whether to purge."
            ),
            CancellationToken.None
        );
        var restoredToApproved = await service.TransitionWorkflowAsync(
            new TransitionGangDocumentationWorkflowRequest(
                target.CaseId,
                documentation.DocumentationId,
                GangDocumentationCatalog.WorkflowActionRestoreToApproved,
                "Captain Vega",
                "Keep the approved record active in documentation."
            ),
            CancellationToken.None
        );
        var inactive = await service.TransitionWorkflowAsync(
            new TransitionGangDocumentationWorkflowRequest(
                target.CaseId,
                documentation.DocumentationId,
                GangDocumentationCatalog.WorkflowActionMarkInactive,
                "Captain Vega",
                "Record should be inactive instead of active."
            ),
            CancellationToken.None
        );
        await service.TransitionWorkflowAsync(
            new TransitionGangDocumentationWorkflowRequest(
                target.CaseId,
                documentation.DocumentationId,
                GangDocumentationCatalog.WorkflowActionMarkPurgeReview,
                "Captain Vega",
                "Second purge review pass."
            ),
            CancellationToken.None
        );
        var restoredToInactive = await service.TransitionWorkflowAsync(
            new TransitionGangDocumentationWorkflowRequest(
                target.CaseId,
                documentation.DocumentationId,
                GangDocumentationCatalog.WorkflowActionRestoreToInactive,
                "Captain Vega",
                "Keep history but leave it inactive."
            ),
            CancellationToken.None
        );

        Assert.Equal(GangDocumentationCatalog.WorkflowStatusApproved, restoredToApproved.Review.WorkflowStatus);
        Assert.Equal(GangDocumentationCatalog.WorkflowStatusInactive, restoredToInactive.Review.WorkflowStatus);
        Assert.Equal("Captain Vega", restoredToInactive.Review.ReviewerName);
        Assert.Equal("Keep history but leave it inactive.", restoredToInactive.Review.DecisionNote);
    }

    [Fact]
    public async Task TransitionWorkflowAsync_AllowsRestoringPurgedRecords()
    {
        await using var fixture = await GangDocumentationTestWorkspaceFixture.CreateAsync();
        var service = fixture.Services.GetRequiredService<IGangDocumentationService>();
        var organizationService = fixture.Services.GetRequiredService<IOrganizationService>();

        var target = await fixture.CreateTargetAsync("Monroe King", createGlobalPerson: true);
        var organization = await organizationService.CreateOrganizationAsync(
            new CreateOrganizationRequest("Rollin 60s", "gang", "active", null, null),
            CancellationToken.None
        );
        var documentation = await service.CreateDocumentationAsync(
            new CreateGangDocumentationRequest(
                target.CaseId,
                target.TargetId,
                organization.OrganizationId,
                null,
                "member",
                "Documentation that will be purged and restored.",
                null
            ),
            CancellationToken.None
        );

        await service.TransitionWorkflowAsync(
            new TransitionGangDocumentationWorkflowRequest(
                target.CaseId,
                documentation.DocumentationId,
                GangDocumentationCatalog.WorkflowActionSubmitForReview,
                null,
                null
            ),
            CancellationToken.None
        );
        await service.TransitionWorkflowAsync(
            new TransitionGangDocumentationWorkflowRequest(
                target.CaseId,
                documentation.DocumentationId,
                GangDocumentationCatalog.WorkflowActionApprove,
                "Sergeant Hale",
                "Approved for use."
            ),
            CancellationToken.None
        );
        await service.TransitionWorkflowAsync(
            new TransitionGangDocumentationWorkflowRequest(
                target.CaseId,
                documentation.DocumentationId,
                GangDocumentationCatalog.WorkflowActionMarkPurgeReview,
                "Sergeant Hale",
                "Administrative purge review."
            ),
            CancellationToken.None
        );
        var purged = await service.TransitionWorkflowAsync(
            new TransitionGangDocumentationWorkflowRequest(
                target.CaseId,
                documentation.DocumentationId,
                GangDocumentationCatalog.WorkflowActionPurge,
                "Sergeant Hale",
                "Soft purge approved."
            ),
            CancellationToken.None
        );
        var restored = await service.TransitionWorkflowAsync(
            new TransitionGangDocumentationWorkflowRequest(
                target.CaseId,
                documentation.DocumentationId,
                GangDocumentationCatalog.WorkflowActionRestoreToApproved,
                "Sergeant Hale",
                "Restore for continued operational use."
            ),
            CancellationToken.None
        );

        Assert.Equal(GangDocumentationCatalog.WorkflowStatusPurged, purged.Review.WorkflowStatus);
        Assert.Equal(GangDocumentationCatalog.WorkflowStatusApproved, restored.Review.WorkflowStatus);
        Assert.Equal("Sergeant Hale", restored.Review.ReviewerName);
        Assert.False(string.IsNullOrWhiteSpace(restored.Review.ReviewerIdentity));
        Assert.Equal("Restore for continued operational use.", restored.Review.DecisionNote);

        var restoreEntry = Assert.Single(
            restored.StatusHistory.Where(item => item.ActionType == GangDocumentationCatalog.WorkflowActionRestoreToApproved)
        );
        Assert.Equal(GangDocumentationCatalog.WorkflowStatusPurged, restoreEntry.PreviousWorkflowStatus);
        Assert.Equal(GangDocumentationCatalog.WorkflowStatusApproved, restoreEntry.NewWorkflowStatus);
        Assert.Equal("Restore for continued operational use.", restoreEntry.DecisionNote);
        Assert.Equal("Sergeant Hale", restoreEntry.ChangedBy);

        await using var db = await fixture.CreateDbContextAsync();
        var persisted = await db.GangDocumentationReviews.AsNoTracking().FirstAsync(
            item => item.DocumentationId == documentation.DocumentationId
        );
        Assert.Equal(GangDocumentationCatalog.WorkflowStatusApproved, persisted.WorkflowStatus);
        Assert.Equal("Sergeant Hale", persisted.ReviewerName);

        var persistedHistory = (await db.GangDocumentationStatusHistory
            .AsNoTracking()
            .Where(item => item.DocumentationId == documentation.DocumentationId)
            .ToListAsync())
            .OrderBy(item => item.ChangedAtUtc)
            .ToList();
        Assert.Contains(persistedHistory, item => item.NewWorkflowStatus == GangDocumentationCatalog.WorkflowStatusPurged);
        Assert.Contains(persistedHistory, item => item.ActionType == GangDocumentationCatalog.WorkflowActionRestoreToApproved);
    }

    [Fact]
    public async Task TransitionWorkflowAsync_BlocksInvalidTransitions()
    {
        await using var fixture = await GangDocumentationTestWorkspaceFixture.CreateAsync();
        var service = fixture.Services.GetRequiredService<IGangDocumentationService>();
        var organizationService = fixture.Services.GetRequiredService<IOrganizationService>();

        var target = await fixture.CreateTargetAsync("Jalen Scott", createGlobalPerson: true);
        var organization = await organizationService.CreateOrganizationAsync(
            new CreateOrganizationRequest("Neighborhood Crips", "gang", "active", null, null),
            CancellationToken.None
        );
        var documentation = await service.CreateDocumentationAsync(
            new CreateGangDocumentationRequest(
                target.CaseId,
                target.TargetId,
                organization.OrganizationId,
                null,
                "affiliate",
                "Draft documentation.",
                null
            ),
            CancellationToken.None
        );

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.TransitionWorkflowAsync(
                new TransitionGangDocumentationWorkflowRequest(
                    target.CaseId,
                    documentation.DocumentationId,
                    GangDocumentationCatalog.WorkflowActionRestoreToApproved,
                    "Captain Vega",
                    "Trying to restore a draft record should fail."
                ),
                CancellationToken.None
            )
        );

        Assert.Contains("Allowed current states", ex.Message);

        var reloaded = Assert.Single(await service.GetDocumentationForTargetAsync(
            target.CaseId,
            target.TargetId,
            CancellationToken.None
        ));
        Assert.Equal(GangDocumentationCatalog.WorkflowStatusDraft, reloaded.Review.WorkflowStatus);
        Assert.Single(reloaded.StatusHistory);
    }
}
