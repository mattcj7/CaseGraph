using CaseGraph.Infrastructure.GangDocumentation;
using CaseGraph.Infrastructure.Organizations;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace CaseGraph.Infrastructure.Tests;

public sealed class GangDocumentationWorkflowTests
{
    [Fact]
    public async Task TransitionWorkflowAsync_AllowsValidSupervisorWorkflowTransitions()
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
        var purgeReview = await service.TransitionWorkflowAsync(
            new TransitionGangDocumentationWorkflowRequest(
                target.CaseId,
                documentation.DocumentationId,
                GangDocumentationCatalog.WorkflowActionMarkPurgeReview,
                "Lieutenant Shaw",
                "Record is under purge consideration."
            ),
            CancellationToken.None
        );
        var purged = await service.TransitionWorkflowAsync(
            new TransitionGangDocumentationWorkflowRequest(
                target.CaseId,
                documentation.DocumentationId,
                GangDocumentationCatalog.WorkflowActionPurge,
                "Lieutenant Shaw",
                "Purge approved."
            ),
            CancellationToken.None
        );

        Assert.Equal(GangDocumentationCatalog.WorkflowStatusPendingSupervisorReview, submitted.Review.WorkflowStatus);
        Assert.Equal(GangDocumentationCatalog.WorkflowStatusApproved, approved.Review.WorkflowStatus);
        Assert.Equal(GangDocumentationCatalog.WorkflowStatusPurgeReview, purgeReview.Review.WorkflowStatus);
        Assert.Equal(GangDocumentationCatalog.WorkflowStatusPurged, purged.Review.WorkflowStatus);
    }

    [Fact]
    public async Task TransitionWorkflowAsync_BlocksInvalidTransitions()
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
                    GangDocumentationCatalog.WorkflowActionApprove,
                    "Sergeant Cole",
                    "Trying to approve from draft should fail."
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

    [Fact]
    public async Task TransitionWorkflowAsync_PersistsReviewerAttributionAndDecisionNote()
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
                "Documentation pending supervisor review.",
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
        var approved = await service.TransitionWorkflowAsync(
            new TransitionGangDocumentationWorkflowRequest(
                target.CaseId,
                documentation.DocumentationId,
                GangDocumentationCatalog.WorkflowActionApprove,
                "Captain Vega",
                "Reviewed and approved for use."
            ),
            CancellationToken.None
        );

        Assert.Equal("Captain Vega", approved.Review.ReviewerName);
        Assert.False(string.IsNullOrWhiteSpace(approved.Review.ReviewerIdentity));
        Assert.Equal("Reviewed and approved for use.", approved.Review.DecisionNote);
        Assert.NotNull(approved.Review.ReviewedAtUtc);
        Assert.NotNull(approved.Review.ApprovedAtUtc);

        await using var db = await fixture.CreateDbContextAsync();
        var persisted = await db.GangDocumentationReviews.AsNoTracking().FirstAsync(
            item => item.DocumentationId == documentation.DocumentationId
        );
        Assert.Equal("Captain Vega", persisted.ReviewerName);
        Assert.False(string.IsNullOrWhiteSpace(persisted.ReviewerIdentity));
        Assert.Equal("Reviewed and approved for use.", persisted.DecisionNote);
    }

    [Fact]
    public async Task TransitionWorkflowAsync_PersistsDurableStatusHistory()
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
                "Reviewable documentation.",
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
        var returned = await service.TransitionWorkflowAsync(
            new TransitionGangDocumentationWorkflowRequest(
                target.CaseId,
                documentation.DocumentationId,
                GangDocumentationCatalog.WorkflowActionReturnForChanges,
                "Sergeant Hale",
                "Add clearer basis citations before approval."
            ),
            CancellationToken.None
        );

        Assert.Equal(GangDocumentationCatalog.WorkflowStatusReturnedForChanges, returned.Review.WorkflowStatus);
        Assert.True(returned.StatusHistory.Count >= 3);

        var reviewAction = Assert.Single(
            returned.StatusHistory.Where(item => item.ActionType == GangDocumentationCatalog.WorkflowActionReturnForChanges)
        );
        Assert.Equal(GangDocumentationCatalog.WorkflowStatusPendingSupervisorReview, reviewAction.PreviousWorkflowStatus);
        Assert.Equal(GangDocumentationCatalog.WorkflowStatusReturnedForChanges, reviewAction.NewWorkflowStatus);
        Assert.Equal("Add clearer basis citations before approval.", reviewAction.DecisionNote);
        Assert.Equal("Sergeant Hale", reviewAction.ChangedBy);
        Assert.False(string.IsNullOrWhiteSpace(reviewAction.ChangedByIdentity));

        await using var db = await fixture.CreateDbContextAsync();
        var persistedHistory = (await db.GangDocumentationStatusHistory
            .AsNoTracking()
            .Where(item => item.DocumentationId == documentation.DocumentationId)
            .ToListAsync())
            .OrderBy(item => item.ChangedAtUtc)
            .ToList();
        Assert.Equal(3, persistedHistory.Count);
        Assert.Contains(persistedHistory, item => item.NewWorkflowStatus == GangDocumentationCatalog.WorkflowStatusReturnedForChanges);
    }
}


