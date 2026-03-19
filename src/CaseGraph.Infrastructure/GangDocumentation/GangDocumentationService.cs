using CaseGraph.Core.Abstractions;
using CaseGraph.Core.Diagnostics;
using CaseGraph.Core.Models;
using CaseGraph.Infrastructure.Organizations;
using CaseGraph.Infrastructure.Persistence;
using CaseGraph.Infrastructure.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using System.Text.Json;

namespace CaseGraph.Infrastructure.GangDocumentation;

public sealed class GangDocumentationService : IGangDocumentationService
{
    private static readonly HashSet<string> GangDocumentationSubgroupTypes =
    [
        "set",
        "clique",
        "subgroup"
    ];

    private static readonly IReadOnlyDictionary<string, WorkflowTransitionDefinition> WorkflowTransitions =
        new Dictionary<string, WorkflowTransitionDefinition>(StringComparer.Ordinal)
        {
            [GangDocumentationCatalog.WorkflowActionSubmitForReview] = new WorkflowTransitionDefinition(
                GangDocumentationCatalog.WorkflowStatusPendingSupervisorReview,
                new HashSet<string>(StringComparer.Ordinal)
                {
                    GangDocumentationCatalog.WorkflowStatusDraft,
                    GangDocumentationCatalog.WorkflowStatusReturnedForChanges
                },
                RequiresReviewer: false,
                RequiresDecisionNote: false
            ),
            [GangDocumentationCatalog.WorkflowActionApprove] = new WorkflowTransitionDefinition(
                GangDocumentationCatalog.WorkflowStatusApproved,
                new HashSet<string>(StringComparer.Ordinal)
                {
                    GangDocumentationCatalog.WorkflowStatusPendingSupervisorReview
                },
                RequiresReviewer: true,
                RequiresDecisionNote: true
            ),
            [GangDocumentationCatalog.WorkflowActionReturnForChanges] = new WorkflowTransitionDefinition(
                GangDocumentationCatalog.WorkflowStatusReturnedForChanges,
                new HashSet<string>(StringComparer.Ordinal)
                {
                    GangDocumentationCatalog.WorkflowStatusPendingSupervisorReview
                },
                RequiresReviewer: true,
                RequiresDecisionNote: true
            ),
            [GangDocumentationCatalog.WorkflowActionMarkInactive] = new WorkflowTransitionDefinition(
                GangDocumentationCatalog.WorkflowStatusInactive,
                new HashSet<string>(StringComparer.Ordinal)
                {
                    GangDocumentationCatalog.WorkflowStatusPendingSupervisorReview,
                    GangDocumentationCatalog.WorkflowStatusApproved
                },
                RequiresReviewer: true,
                RequiresDecisionNote: true
            ),
            [GangDocumentationCatalog.WorkflowActionMarkPurgeReview] = new WorkflowTransitionDefinition(
                GangDocumentationCatalog.WorkflowStatusPurgeReview,
                new HashSet<string>(StringComparer.Ordinal)
                {
                    GangDocumentationCatalog.WorkflowStatusPendingSupervisorReview,
                    GangDocumentationCatalog.WorkflowStatusApproved,
                    GangDocumentationCatalog.WorkflowStatusInactive
                },
                RequiresReviewer: true,
                RequiresDecisionNote: true
            ),
            [GangDocumentationCatalog.WorkflowActionPurge] = new WorkflowTransitionDefinition(
                GangDocumentationCatalog.WorkflowStatusPurged,
                new HashSet<string>(StringComparer.Ordinal)
                {
                    GangDocumentationCatalog.WorkflowStatusPurgeReview
                },
                RequiresReviewer: true,
                RequiresDecisionNote: true
            ),
            [GangDocumentationCatalog.WorkflowActionRestoreToApproved] = new WorkflowTransitionDefinition(
                GangDocumentationCatalog.WorkflowStatusApproved,
                new HashSet<string>(StringComparer.Ordinal)
                {
                    GangDocumentationCatalog.WorkflowStatusInactive,
                    GangDocumentationCatalog.WorkflowStatusPurgeReview,
                    GangDocumentationCatalog.WorkflowStatusPurged
                },
                RequiresReviewer: true,
                RequiresDecisionNote: true
            ),
            [GangDocumentationCatalog.WorkflowActionRestoreToInactive] = new WorkflowTransitionDefinition(
                GangDocumentationCatalog.WorkflowStatusInactive,
                new HashSet<string>(StringComparer.Ordinal)
                {
                    GangDocumentationCatalog.WorkflowStatusPurgeReview,
                    GangDocumentationCatalog.WorkflowStatusPurged
                },
                RequiresReviewer: true,
                RequiresDecisionNote: true
            )
        };

    private readonly IDbContextFactory<WorkspaceDbContext> _dbContextFactory;
    private readonly IWorkspaceDatabaseInitializer _databaseInitializer;
    private readonly IWorkspaceWriteGate _workspaceWriteGate;
    private readonly IAuditLogService _auditLogService;
    private readonly IClock _clock;

    public GangDocumentationService(
        IDbContextFactory<WorkspaceDbContext> dbContextFactory,
        IWorkspaceDatabaseInitializer databaseInitializer,
        IWorkspaceWriteGate workspaceWriteGate,
        IAuditLogService auditLogService,
        IClock clock
    )
    {
        _dbContextFactory = dbContextFactory;
        _databaseInitializer = databaseInitializer;
        _workspaceWriteGate = workspaceWriteGate;
        _auditLogService = auditLogService;
        _clock = clock;
    }

    public Task<IReadOnlyList<GangDocumentationRecord>> GetDocumentationForTargetAsync(
        Guid caseId,
        Guid targetId,
        CancellationToken ct
    )
    {
        return GetDocumentationForTargetCoreAsync(caseId, targetId, ct);
    }

    public Task<GangDocumentationRecord> CreateDocumentationAsync(
        CreateGangDocumentationRequest request,
        CancellationToken ct
    )
    {
        return CreateDocumentationCoreAsync(request, ct);
    }

    public Task<GangDocumentationRecord> UpdateDocumentationAsync(
        UpdateGangDocumentationRequest request,
        CancellationToken ct
    )
    {
        return UpdateDocumentationCoreAsync(request, ct);
    }

    public Task<GangDocumentationRecord> TransitionWorkflowAsync(
        TransitionGangDocumentationWorkflowRequest request,
        CancellationToken ct
    )
    {
        return TransitionWorkflowCoreAsync(request, ct);
    }

    public Task<GangDocumentationCriterion> SaveCriterionAsync(
        SaveGangDocumentationCriterionRequest request,
        CancellationToken ct
    )
    {
        return SaveCriterionCoreAsync(request, ct);
    }

    public Task RemoveCriterionAsync(
        Guid caseId,
        Guid documentationId,
        Guid criterionId,
        CancellationToken ct
    )
    {
        return RemoveCriterionCoreAsync(caseId, documentationId, criterionId, ct);
    }

    private async Task<IReadOnlyList<GangDocumentationRecord>> GetDocumentationForTargetCoreAsync(
        Guid caseId,
        Guid targetId,
        CancellationToken ct
    )
    {
        await _databaseInitializer.EnsureInitializedAsync(ct);
        await using var db = await _dbContextFactory.CreateDbContextAsync(ct);

        var records = await db.GangDocumentationRecords
            .AsNoTracking()
            .Include(record => record.Organization)
            .Include(record => record.SubgroupOrganization)
            .Include(record => record.Review)
            .Where(record => record.CaseId == caseId && record.TargetId == targetId)
            .ToListAsync(ct);

        if (records.Count == 0)
        {
            return [];
        }

        var documentationIds = records.Select(record => record.DocumentationId).ToList();
        var criteria = await db.GangDocumentationCriteria
            .AsNoTracking()
            .Where(record => documentationIds.Contains(record.DocumentationId))
            .ToListAsync(ct);
        var history = await db.GangDocumentationStatusHistory
            .AsNoTracking()
            .Where(record => documentationIds.Contains(record.DocumentationId))
            .ToListAsync(ct);

        var criteriaByDocumentationId = criteria
            .GroupBy(record => record.DocumentationId)
            .ToDictionary(
                group => group.Key,
                group => (IReadOnlyList<GangDocumentationCriterion>)group
                    .OrderBy(item => item.SortOrder)
                    .ThenBy(item => item.CreatedAtUtc)
                    .Select(MapCriterion)
                    .ToList()
            );
        var historyByDocumentationId = history
            .GroupBy(record => record.DocumentationId)
            .ToDictionary(
                group => group.Key,
                group => (IReadOnlyList<GangDocumentationStatusHistoryEntry>)group
                    .OrderByDescending(item => item.ChangedAtUtc)
                    .ThenByDescending(item => item.HistoryEntryId)
                    .Select(MapHistory)
                    .ToList()
            );

        return records
            .OrderByDescending(record => record.UpdatedAtUtc)
            .ThenBy(record => record.CreatedAtUtc)
            .Select(record => MapRecord(
                record,
                criteriaByDocumentationId.GetValueOrDefault(record.DocumentationId) ?? [],
                historyByDocumentationId.GetValueOrDefault(record.DocumentationId) ?? []
            ))
            .ToList();
    }

    private async Task<GangDocumentationRecord> CreateDocumentationCoreAsync(
        CreateGangDocumentationRequest request,
        CancellationToken ct
    )
    {
        ValidateCaseAndTarget(request.CaseId, request.TargetId);
        var now = _clock.UtcNow.ToUniversalTime();

        await _databaseInitializer.EnsureInitializedAsync(ct);
        await using var db = await _dbContextFactory.CreateDbContextAsync(ct);

        var target = await GetTargetAsync(db, request.CaseId, request.TargetId, ct);
        var organization = await GetOrganizationAsync(db, request.OrganizationId, ct);
        var subgroup = await ValidateAndGetSubgroupAsync(
            db,
            request.OrganizationId,
            request.SubgroupOrganizationId,
            ct
        );

        var documentation = new GangDocumentationRecordEntity
        {
            DocumentationId = Guid.NewGuid(),
            CaseId = request.CaseId,
            TargetId = request.TargetId,
            GlobalEntityId = target.GlobalEntityId,
            OrganizationId = organization.OrganizationId,
            SubgroupOrganizationId = subgroup?.OrganizationId,
            AffiliationRole = NormalizeCatalogValue(
                request.AffiliationRole,
                GangDocumentationCatalog.AffiliationRoles,
                nameof(request.AffiliationRole)
            ),
            DocumentationStatus = GangDocumentationCatalog.WorkflowStatusDraft,
            ApprovalStatus = GangDocumentationCatalog.GetApprovalStatus(GangDocumentationCatalog.WorkflowStatusDraft),
            Reviewer = null,
            ReviewDueDateUtc = null,
            Summary = NormalizeRequired(request.Summary, "Summary is required."),
            Notes = NormalizeOptional(request.Notes),
            CreatedAtUtc = now,
            UpdatedAtUtc = now
        };

        documentation.Review = new GangDocumentationReviewRecord
        {
            ReviewId = documentation.DocumentationId,
            DocumentationId = documentation.DocumentationId,
            WorkflowStatus = GangDocumentationCatalog.WorkflowStatusDraft,
            ReviewDueDateUtc = documentation.ReviewDueDateUtc
        };

        db.GangDocumentationRecords.Add(documentation);
        AddHistoryEntry(
            db,
            documentation.DocumentationId,
            actionType: "Created",
            summary: "Documentation created in Draft.",
            previousWorkflowStatus: null,
            newWorkflowStatus: GangDocumentationCatalog.WorkflowStatusDraft,
            decisionNote: null,
            changedBy: Environment.UserName,
            changedByIdentity: Environment.UserName,
            timestampUtc: now
        );

        await SaveChangesWithWritePolicyAsync(
            db,
            "GangDocumentation.CreateDocumentation",
            request.CaseId,
            documentation.DocumentationId,
            ct
        );

        LogDiagnostic(
            "GangDocumentationCreated",
            "Gang documentation record created.",
            request.CaseId,
            documentation.DocumentationId,
            request.TargetId,
            new Dictionary<string, object?>
            {
                ["organizationId"] = documentation.OrganizationId.ToString("D"),
                ["subgroupOrganizationId"] = documentation.SubgroupOrganizationId?.ToString("D"),
                ["workflowStatus"] = documentation.Review.WorkflowStatus
            }
        );

        await WriteAuditAsync(
            "GangDocumentationCreated",
            request.CaseId,
            $"Gang documentation created for {target.DisplayName}.",
            new
            {
                documentation.DocumentationId,
                documentation.TargetId,
                target.DisplayName,
                documentation.GlobalEntityId,
                documentation.OrganizationId,
                OrganizationName = organization.Name,
                documentation.SubgroupOrganizationId,
                SubgroupOrganizationName = subgroup?.Name,
                documentation.AffiliationRole,
                documentation.Summary,
                documentation.Notes,
                WorkflowStatus = documentation.Review.WorkflowStatus
            },
            ct
        );

        return await GetDocumentationByIdAsync(request.CaseId, documentation.DocumentationId, ct);
    }

    private async Task<GangDocumentationRecord> UpdateDocumentationCoreAsync(
        UpdateGangDocumentationRequest request,
        CancellationToken ct
    )
    {
        if (request.CaseId == Guid.Empty || request.DocumentationId == Guid.Empty)
        {
            throw new ArgumentException("CaseId and DocumentationId are required.", nameof(request));
        }

        await _databaseInitializer.EnsureInitializedAsync(ct);
        await using var db = await _dbContextFactory.CreateDbContextAsync(ct);

        var documentation = await db.GangDocumentationRecords
            .Include(record => record.Review)
            .FirstOrDefaultAsync(
                record => record.CaseId == request.CaseId && record.DocumentationId == request.DocumentationId,
                ct
            );
        if (documentation is null)
        {
            throw new InvalidOperationException("Gang documentation record not found.");
        }

        var review = EnsureReviewRecord(db, documentation);
        var previousWorkflowStatus = NormalizeWorkflowStatus(review.WorkflowStatus);
        EnsureWorkflowEditableForContent(previousWorkflowStatus);

        var target = await GetTargetAsync(db, documentation.CaseId, documentation.TargetId, ct);
        var organization = await GetOrganizationAsync(db, request.OrganizationId, ct);
        var subgroup = await ValidateAndGetSubgroupAsync(
            db,
            request.OrganizationId,
            request.SubgroupOrganizationId,
            ct
        );

        documentation.GlobalEntityId = target.GlobalEntityId;
        documentation.OrganizationId = organization.OrganizationId;
        documentation.SubgroupOrganizationId = subgroup?.OrganizationId;
        documentation.AffiliationRole = NormalizeCatalogValue(
            request.AffiliationRole,
            GangDocumentationCatalog.AffiliationRoles,
            nameof(request.AffiliationRole)
        );
        documentation.Summary = NormalizeRequired(request.Summary, "Summary is required.");
        documentation.Notes = NormalizeOptional(request.Notes);
        documentation.UpdatedAtUtc = _clock.UtcNow.ToUniversalTime();
        var savedAsDraft = TransitionEditableChangesToDraftIfNeeded(
            db,
            documentation,
            review,
            previousWorkflowStatus,
            documentation.UpdatedAtUtc
        );
        SyncLegacyWorkflowFields(documentation, review);

        await SaveChangesWithWritePolicyAsync(
            db,
            "GangDocumentation.UpdateDocumentation",
            request.CaseId,
            documentation.DocumentationId,
            ct
        );

        LogDiagnostic(
            "GangDocumentationUpdated",
            "Gang documentation record updated.",
            request.CaseId,
            documentation.DocumentationId,
            documentation.TargetId,
            new Dictionary<string, object?>
            {
                ["organizationId"] = documentation.OrganizationId.ToString("D"),
                ["subgroupOrganizationId"] = documentation.SubgroupOrganizationId?.ToString("D"),
                ["previousWorkflowStatus"] = previousWorkflowStatus,
                ["workflowStatus"] = review.WorkflowStatus,
                ["savedAsDraft"] = savedAsDraft
            }
        );

        await WriteAuditAsync(
            "GangDocumentationUpdated",
            request.CaseId,
            $"Gang documentation updated for {target.DisplayName}.",
            new
            {
                documentation.DocumentationId,
                documentation.TargetId,
                target.DisplayName,
                documentation.GlobalEntityId,
                documentation.OrganizationId,
                OrganizationName = organization.Name,
                documentation.SubgroupOrganizationId,
                SubgroupOrganizationName = subgroup?.Name,
                documentation.AffiliationRole,
                documentation.Summary,
                documentation.Notes,
                PreviousWorkflowStatus = previousWorkflowStatus,
                WorkflowStatus = review.WorkflowStatus,
                SavedAsDraft = savedAsDraft
            },
            ct
        );

        return await GetDocumentationByIdAsync(request.CaseId, documentation.DocumentationId, ct);
    }

    private async Task<GangDocumentationRecord> TransitionWorkflowCoreAsync(
        TransitionGangDocumentationWorkflowRequest request,
        CancellationToken ct
    )
    {
        if (request.CaseId == Guid.Empty || request.DocumentationId == Guid.Empty)
        {
            throw new ArgumentException("CaseId and DocumentationId are required.", nameof(request));
        }

        var workflowAction = NormalizeCatalogValue(
            request.WorkflowAction,
            GangDocumentationCatalog.WorkflowActions,
            nameof(request.WorkflowAction)
        );
        var transition = WorkflowTransitions[workflowAction];
        var reviewerName = NormalizeOptional(request.ReviewerName);
        var decisionNote = NormalizeOptional(request.DecisionNote);

        if (transition.RequiresReviewer && reviewerName is null)
        {
            throw new ArgumentException("Reviewer name is required for this workflow action.", nameof(request.ReviewerName));
        }

        if (transition.RequiresDecisionNote && decisionNote is null)
        {
            throw new ArgumentException("Decision note is required for this workflow action.", nameof(request.DecisionNote));
        }

        await _databaseInitializer.EnsureInitializedAsync(ct);
        await using var db = await _dbContextFactory.CreateDbContextAsync(ct);

        var documentation = await db.GangDocumentationRecords
            .Include(record => record.Review)
            .FirstOrDefaultAsync(
                record => record.CaseId == request.CaseId && record.DocumentationId == request.DocumentationId,
                ct
            ) ?? throw new InvalidOperationException("Gang documentation record not found.");

        var targetDisplayName = await GetTargetDisplayNameAsync(db, documentation.TargetId, ct);
        var review = EnsureReviewRecord(db, documentation);
        var previousWorkflowStatus = NormalizeWorkflowStatus(review.WorkflowStatus);
        EnsureValidTransition(previousWorkflowStatus, workflowAction, transition);

        var now = _clock.UtcNow.ToUniversalTime();
        ApplyWorkflowTransition(review, documentation, workflowAction, reviewerName, decisionNote, now);
        documentation.UpdatedAtUtc = now;
        SyncLegacyWorkflowFields(documentation, review);

        var changedByIdentity = Environment.UserName;
        var changedBy = reviewerName ?? changedByIdentity;
        var summary = BuildTransitionSummary(
            workflowAction,
            previousWorkflowStatus,
            review.WorkflowStatus,
            reviewerName,
            decisionNote
        );

        AddHistoryEntry(
            db,
            documentation.DocumentationId,
            actionType: workflowAction,
            summary: summary,
            previousWorkflowStatus: previousWorkflowStatus,
            newWorkflowStatus: review.WorkflowStatus,
            decisionNote: decisionNote,
            changedBy: changedBy,
            changedByIdentity: changedByIdentity,
            timestampUtc: now
        );

        await SaveChangesWithWritePolicyAsync(
            db,
            "GangDocumentation.TransitionWorkflow",
            request.CaseId,
            documentation.DocumentationId,
            ct
        );

        LogDiagnostic(
            GetDiagnosticEventName(workflowAction),
            summary,
            request.CaseId,
            documentation.DocumentationId,
            documentation.TargetId,
            new Dictionary<string, object?>
            {
                ["workflowAction"] = workflowAction,
                ["previousWorkflowStatus"] = previousWorkflowStatus,
                ["workflowStatus"] = review.WorkflowStatus,
                ["reviewerName"] = reviewerName,
                ["decisionNote"] = decisionNote
            }
        );

        await WriteAuditAsync(
            GetAuditActionType(workflowAction),
            request.CaseId,
            $"{GangDocumentationCatalog.GetWorkflowActionDisplayName(workflowAction)} completed for {targetDisplayName}.",
            new
            {
                documentation.DocumentationId,
                documentation.TargetId,
                targetDisplayName,
                WorkflowAction = workflowAction,
                PreviousWorkflowStatus = previousWorkflowStatus,
                WorkflowStatus = review.WorkflowStatus,
                review.ReviewerName,
                review.ReviewerIdentity,
                review.SubmittedForReviewAtUtc,
                review.ReviewedAtUtc,
                review.ApprovedAtUtc,
                review.DecisionNote
            },
            ct
        );

        return await GetDocumentationByIdAsync(request.CaseId, documentation.DocumentationId, ct);
    }

    private async Task<GangDocumentationCriterion> SaveCriterionCoreAsync(
        SaveGangDocumentationCriterionRequest request,
        CancellationToken ct
    )
    {
        if (request.CaseId == Guid.Empty || request.DocumentationId == Guid.Empty)
        {
            throw new ArgumentException("CaseId and DocumentationId are required.", nameof(request));
        }

        await _databaseInitializer.EnsureInitializedAsync(ct);
        await using var db = await _dbContextFactory.CreateDbContextAsync(ct);

        var documentation = await db.GangDocumentationRecords
            .Include(record => record.Review)
            .FirstOrDefaultAsync(
                record => record.CaseId == request.CaseId && record.DocumentationId == request.DocumentationId,
                ct
            );
        if (documentation is null)
        {
            throw new InvalidOperationException("Gang documentation record not found.");
        }

        var review = EnsureReviewRecord(db, documentation);
        var previousWorkflowStatus = NormalizeWorkflowStatus(review.WorkflowStatus);
        EnsureWorkflowEditableForContent(previousWorkflowStatus);

        var targetDisplayName = await GetTargetDisplayNameAsync(db, documentation.TargetId, ct);

        var now = _clock.UtcNow.ToUniversalTime();
        var isCreate = !request.CriterionId.HasValue || request.CriterionId.Value == Guid.Empty;
        var criterionType = NormalizeCatalogValue(
            request.CriterionType,
            GangDocumentationCatalog.CriterionTypes,
            nameof(request.CriterionType)
        );
        var basisSummary = NormalizeRequired(request.BasisSummary, "Basis summary is required.");
        var sourceNote = NormalizeOptional(request.SourceNote);
        var observedDateUtc = NormalizeDate(request.ObservedDateUtc);

        GangDocumentationCriterionRecord criterion;
        if (isCreate)
        {
            var nextSortOrder = await db.GangDocumentationCriteria
                .Where(record => record.DocumentationId == request.DocumentationId)
                .Select(record => (int?)record.SortOrder)
                .MaxAsync(ct) ?? 0;
            criterion = new GangDocumentationCriterionRecord
            {
                CriterionId = Guid.NewGuid(),
                DocumentationId = request.DocumentationId,
                CriterionType = criterionType,
                IsMet = request.IsMet,
                BasisSummary = basisSummary,
                ObservedDateUtc = observedDateUtc,
                SourceNote = sourceNote,
                SortOrder = nextSortOrder + 1,
                CreatedAtUtc = now,
                UpdatedAtUtc = now
            };
            db.GangDocumentationCriteria.Add(criterion);
        }
        else
        {
            var criterionId = request.CriterionId;
            criterion = await db.GangDocumentationCriteria.FirstOrDefaultAsync(
                record =>
                    record.DocumentationId == request.DocumentationId
                    && record.CriterionId == criterionId!.Value,
                ct
            ) ?? throw new InvalidOperationException("Gang documentation criterion not found.");

            criterion.CriterionType = criterionType;
            criterion.IsMet = request.IsMet;
            criterion.BasisSummary = basisSummary;
            criterion.ObservedDateUtc = observedDateUtc;
            criterion.SourceNote = sourceNote;
            criterion.UpdatedAtUtc = now;
        }

        documentation.UpdatedAtUtc = now;
        var savedAsDraft = TransitionEditableChangesToDraftIfNeeded(
            db,
            documentation,
            review,
            previousWorkflowStatus,
            now
        );
        SyncLegacyWorkflowFields(documentation, review);
        await SaveChangesWithWritePolicyAsync(
            db,
            "GangDocumentation.SaveCriterion",
            request.CaseId,
            documentation.DocumentationId,
            ct
        );

        LogDiagnostic(
            isCreate ? "GangDocumentationCriterionCreated" : "GangDocumentationCriterionUpdated",
            isCreate ? "Gang documentation criterion created." : "Gang documentation criterion updated.",
            request.CaseId,
            documentation.DocumentationId,
            documentation.TargetId,
            new Dictionary<string, object?>
            {
                ["criterionId"] = criterion.CriterionId.ToString("D"),
                ["criterionType"] = criterion.CriterionType,
                ["isMet"] = criterion.IsMet,
                ["previousWorkflowStatus"] = previousWorkflowStatus,
                ["workflowStatus"] = review.WorkflowStatus,
                ["savedAsDraft"] = savedAsDraft
            }
        );

        await WriteAuditAsync(
            isCreate ? "GangDocumentationCriterionCreated" : "GangDocumentationCriterionUpdated",
            request.CaseId,
            $"{(isCreate ? "Gang documentation criterion added" : "Gang documentation criterion updated")} for {targetDisplayName}.",
            new
            {
                documentation.DocumentationId,
                documentation.TargetId,
                targetDisplayName,
                criterion.CriterionId,
                criterion.CriterionType,
                criterion.IsMet,
                criterion.BasisSummary,
                criterion.ObservedDateUtc,
                criterion.SourceNote,
                criterion.SortOrder,
                PreviousWorkflowStatus = previousWorkflowStatus,
                WorkflowStatus = review.WorkflowStatus,
                SavedAsDraft = savedAsDraft
            },
            ct
        );

        return MapCriterion(criterion);
    }

    private async Task RemoveCriterionCoreAsync(
        Guid caseId,
        Guid documentationId,
        Guid criterionId,
        CancellationToken ct
    )
    {
        if (caseId == Guid.Empty || documentationId == Guid.Empty || criterionId == Guid.Empty)
        {
            throw new ArgumentException("CaseId, DocumentationId, and CriterionId are required.");
        }

        await _databaseInitializer.EnsureInitializedAsync(ct);
        await using var db = await _dbContextFactory.CreateDbContextAsync(ct);

        var documentation = await db.GangDocumentationRecords
            .Include(record => record.Review)
            .FirstOrDefaultAsync(
                record => record.CaseId == caseId && record.DocumentationId == documentationId,
                ct
            );
        if (documentation is null)
        {
            return;
        }

        var review = EnsureReviewRecord(db, documentation);
        var previousWorkflowStatus = NormalizeWorkflowStatus(review.WorkflowStatus);
        EnsureWorkflowEditableForContent(previousWorkflowStatus);

        var criterion = await db.GangDocumentationCriteria.FirstOrDefaultAsync(
            record => record.DocumentationId == documentationId && record.CriterionId == criterionId,
            ct
        );
        if (criterion is null)
        {
            return;
        }

        var targetDisplayName = await GetTargetDisplayNameAsync(db, documentation.TargetId, ct);

        documentation.UpdatedAtUtc = _clock.UtcNow.ToUniversalTime();
        db.GangDocumentationCriteria.Remove(criterion);
        var savedAsDraft = TransitionEditableChangesToDraftIfNeeded(
            db,
            documentation,
            review,
            previousWorkflowStatus,
            documentation.UpdatedAtUtc
        );
        SyncLegacyWorkflowFields(documentation, review);
        await SaveChangesWithWritePolicyAsync(
            db,
            "GangDocumentation.RemoveCriterion",
            caseId,
            documentation.DocumentationId,
            ct
        );

        LogDiagnostic(
            "GangDocumentationCriterionRemoved",
            "Gang documentation criterion removed.",
            caseId,
            documentation.DocumentationId,
            documentation.TargetId,
            new Dictionary<string, object?>
            {
                ["criterionId"] = criterionId.ToString("D"),
                ["criterionType"] = criterion.CriterionType,
                ["previousWorkflowStatus"] = previousWorkflowStatus,
                ["workflowStatus"] = review.WorkflowStatus,
                ["savedAsDraft"] = savedAsDraft
            }
        );

        await WriteAuditAsync(
            "GangDocumentationCriterionRemoved",
            caseId,
            $"Gang documentation criterion removed for {targetDisplayName}.",
            new
            {
                documentation.DocumentationId,
                documentation.TargetId,
                targetDisplayName,
                criterion.CriterionId,
                criterion.CriterionType,
                PreviousWorkflowStatus = previousWorkflowStatus,
                WorkflowStatus = review.WorkflowStatus,
                SavedAsDraft = savedAsDraft
            },
            ct
        );
    }

    private async Task<GangDocumentationRecord> GetDocumentationByIdAsync(
        Guid caseId,
        Guid documentationId,
        CancellationToken ct
    )
    {
        await _databaseInitializer.EnsureInitializedAsync(ct);
        await using var db = await _dbContextFactory.CreateDbContextAsync(ct);

        var record = await db.GangDocumentationRecords
            .AsNoTracking()
            .Include(item => item.Organization)
            .Include(item => item.SubgroupOrganization)
            .Include(item => item.Review)
            .FirstOrDefaultAsync(
                item => item.CaseId == caseId && item.DocumentationId == documentationId,
                ct
            ) ?? throw new InvalidOperationException("Gang documentation record not found.");

        var criteria = await db.GangDocumentationCriteria
            .AsNoTracking()
            .Where(item => item.DocumentationId == documentationId)
            .ToListAsync(ct);
        var history = await db.GangDocumentationStatusHistory
            .AsNoTracking()
            .Where(item => item.DocumentationId == documentationId)
            .ToListAsync(ct);

        return MapRecord(
            record,
            criteria
                .OrderBy(item => item.SortOrder)
                .ThenBy(item => item.CreatedAtUtc)
                .Select(MapCriterion)
                .ToList(),
            history
                .OrderByDescending(item => item.ChangedAtUtc)
                .ThenByDescending(item => item.HistoryEntryId)
                .Select(MapHistory)
                .ToList()
        );
    }

    private static GangDocumentationRecord MapRecord(
        GangDocumentationRecordEntity record,
        IReadOnlyList<GangDocumentationCriterion> criteria,
        IReadOnlyList<GangDocumentationStatusHistoryEntry> history
    )
    {
        return new GangDocumentationRecord(
            record.DocumentationId,
            record.CaseId,
            record.TargetId,
            record.GlobalEntityId,
            record.OrganizationId,
            record.Organization?.Name ?? $"Organization {record.OrganizationId:D}",
            record.SubgroupOrganizationId,
            record.SubgroupOrganization?.Name,
            record.AffiliationRole,
            record.Summary,
            record.Notes,
            record.CreatedAtUtc,
            record.UpdatedAtUtc,
            MapReview(record.Review, record),
            criteria,
            history
        );
    }

    private static GangDocumentationReview MapReview(
        GangDocumentationReviewRecord? review,
        GangDocumentationRecordEntity record
    )
    {
        var effectiveReview = review ?? BuildLegacyReview(record);
        return new GangDocumentationReview(
            effectiveReview.ReviewId,
            effectiveReview.DocumentationId,
            NormalizeWorkflowStatus(effectiveReview.WorkflowStatus),
            effectiveReview.ReviewerName,
            effectiveReview.ReviewerIdentity,
            effectiveReview.SubmittedForReviewAtUtc,
            effectiveReview.ReviewedAtUtc,
            effectiveReview.ApprovedAtUtc,
            effectiveReview.ReviewDueDateUtc,
            effectiveReview.DecisionNote
        );
    }

    private static GangDocumentationCriterion MapCriterion(GangDocumentationCriterionRecord record)
    {
        return new GangDocumentationCriterion(
            record.CriterionId,
            record.DocumentationId,
            record.CriterionType,
            record.IsMet,
            record.BasisSummary,
            record.ObservedDateUtc,
            record.SourceNote,
            record.SortOrder,
            record.CreatedAtUtc,
            record.UpdatedAtUtc
        );
    }

    private static GangDocumentationStatusHistoryEntry MapHistory(
        GangDocumentationStatusHistoryRecord record
    )
    {
        return new GangDocumentationStatusHistoryEntry(
            record.HistoryEntryId,
            record.DocumentationId,
            record.ActionType,
            record.Summary,
            record.PreviousWorkflowStatus,
            record.NewWorkflowStatus,
            record.DecisionNote,
            record.ChangedBy,
            record.ChangedByIdentity,
            record.ChangedAtUtc
        );
    }

    private static async Task<TargetRecord> GetTargetAsync(
        WorkspaceDbContext db,
        Guid caseId,
        Guid targetId,
        CancellationToken ct
    )
    {
        return await db.Targets.FirstOrDefaultAsync(
            target => target.CaseId == caseId && target.TargetId == targetId,
            ct
        ) ?? throw new InvalidOperationException("Target not found.");
    }

    private static async Task<string> GetTargetDisplayNameAsync(
        WorkspaceDbContext db,
        Guid targetId,
        CancellationToken ct
    )
    {
        return await db.Targets
            .AsNoTracking()
            .Where(target => target.TargetId == targetId)
            .Select(target => target.DisplayName)
            .FirstOrDefaultAsync(ct)
            ?? $"Target {targetId:D}";
    }

    private static async Task<OrganizationRecord> GetOrganizationAsync(
        WorkspaceDbContext db,
        Guid organizationId,
        CancellationToken ct
    )
    {
        if (organizationId == Guid.Empty)
        {
            throw new ArgumentException("OrganizationId is required.", nameof(organizationId));
        }

        return await db.Organizations.FirstOrDefaultAsync(
            record => record.OrganizationId == organizationId,
            ct
        ) ?? throw new InvalidOperationException("Organization not found.");
    }

    private static async Task<OrganizationRecord?> ValidateAndGetSubgroupAsync(
        WorkspaceDbContext db,
        Guid organizationId,
        Guid? subgroupOrganizationId,
        CancellationToken ct
    )
    {
        if (!subgroupOrganizationId.HasValue || subgroupOrganizationId.Value == Guid.Empty)
        {
            return null;
        }

        var subgroup = await db.Organizations.FirstOrDefaultAsync(
            record => record.OrganizationId == subgroupOrganizationId.Value,
            ct
        );
        if (subgroup is null)
        {
            throw new InvalidOperationException("Subgroup organization not found.");
        }

        if (subgroup.ParentOrganizationId != organizationId)
        {
            throw new InvalidOperationException(
                "Subgroup organization must be a child of the linked organization."
            );
        }

        if (!GangDocumentationSubgroupTypes.Contains(subgroup.Type))
        {
            throw new InvalidOperationException(
                "Subgroup organization must be a set, clique, or subgroup."
            );
        }

        return subgroup;
    }

    private static GangDocumentationReviewRecord EnsureReviewRecord(
        WorkspaceDbContext db,
        GangDocumentationRecordEntity documentation
    )
    {
        if (documentation.Review is not null)
        {
            documentation.Review.WorkflowStatus = NormalizeWorkflowStatus(documentation.Review.WorkflowStatus);
            return documentation.Review;
        }

        var review = BuildLegacyReview(documentation);
        documentation.Review = review;
        db.GangDocumentationReviews.Add(review);
        SyncLegacyWorkflowFields(documentation, review);
        return review;
    }

    private static GangDocumentationReviewRecord BuildLegacyReview(
        GangDocumentationRecordEntity documentation
    )
    {
        return new GangDocumentationReviewRecord
        {
            ReviewId = documentation.DocumentationId,
            DocumentationId = documentation.DocumentationId,
            WorkflowStatus = NormalizeWorkflowStatus(documentation.DocumentationStatus),
            ReviewerName = documentation.Reviewer,
            ReviewerIdentity = null,
            ApprovedAtUtc = string.Equals(
                NormalizeWorkflowStatus(documentation.DocumentationStatus),
                GangDocumentationCatalog.WorkflowStatusApproved,
                StringComparison.Ordinal
            )
                ? documentation.UpdatedAtUtc
                : null,
            ReviewDueDateUtc = documentation.ReviewDueDateUtc
        };
    }

    private static bool TransitionEditableChangesToDraftIfNeeded(
        WorkspaceDbContext db,
        GangDocumentationRecordEntity documentation,
        GangDocumentationReviewRecord review,
        string previousWorkflowStatus,
        DateTimeOffset timestampUtc
    )
    {
        if (!string.Equals(previousWorkflowStatus, GangDocumentationCatalog.WorkflowStatusReturnedForChanges, StringComparison.Ordinal))
        {
            return false;
        }

        review.WorkflowStatus = GangDocumentationCatalog.WorkflowStatusDraft;
        review.SubmittedForReviewAtUtc = null;
        SyncLegacyWorkflowFields(documentation, review);
        AddHistoryEntry(
            db,
            documentation.DocumentationId,
            actionType: "save as draft",
            summary: "Returned-for-changes documentation was saved as Draft so it can be revised before re-submission.",
            previousWorkflowStatus: previousWorkflowStatus,
            newWorkflowStatus: review.WorkflowStatus,
            decisionNote: null,
            changedBy: Environment.UserName,
            changedByIdentity: Environment.UserName,
            timestampUtc: timestampUtc
        );
        return true;
    }

    private static void ApplyWorkflowTransition(
        GangDocumentationReviewRecord review,
        GangDocumentationRecordEntity documentation,
        string workflowAction,
        string? reviewerName,
        string? decisionNote,
        DateTimeOffset timestampUtc
    )
    {
        review.WorkflowStatus = WorkflowTransitions[workflowAction].TargetStatus;

        switch (workflowAction)
        {
            case GangDocumentationCatalog.WorkflowActionSubmitForReview:
                review.SubmittedForReviewAtUtc = timestampUtc;
                review.ReviewedAtUtc = null;
                review.ApprovedAtUtc = null;
                review.DecisionNote = null;
                review.ReviewerName = reviewerName;
                review.ReviewerIdentity = null;
                break;

            case GangDocumentationCatalog.WorkflowActionApprove:
            case GangDocumentationCatalog.WorkflowActionRestoreToApproved:
                review.ReviewerName = reviewerName;
                review.ReviewerIdentity = Environment.UserName;
                review.ReviewedAtUtc = timestampUtc;
                review.ApprovedAtUtc = timestampUtc;
                review.DecisionNote = decisionNote;
                break;

            case GangDocumentationCatalog.WorkflowActionReturnForChanges:
                review.ReviewerName = reviewerName;
                review.ReviewerIdentity = Environment.UserName;
                review.ReviewedAtUtc = timestampUtc;
                review.ApprovedAtUtc = null;
                review.DecisionNote = decisionNote;
                break;

            case GangDocumentationCatalog.WorkflowActionMarkInactive:
            case GangDocumentationCatalog.WorkflowActionMarkPurgeReview:
            case GangDocumentationCatalog.WorkflowActionPurge:
            case GangDocumentationCatalog.WorkflowActionRestoreToInactive:
                review.ReviewerName = reviewerName;
                review.ReviewerIdentity = Environment.UserName;
                review.ReviewedAtUtc = timestampUtc;
                review.DecisionNote = decisionNote;
                break;

            default:
                throw new InvalidOperationException($"Unsupported workflow action '{workflowAction}'.");
        }

        SyncLegacyWorkflowFields(documentation, review);
    }

    private static void AddHistoryEntry(
        WorkspaceDbContext db,
        Guid documentationId,
        string actionType,
        string summary,
        string? previousWorkflowStatus,
        string? newWorkflowStatus,
        string? decisionNote,
        string? changedBy,
        string? changedByIdentity,
        DateTimeOffset timestampUtc
    )
    {
        db.GangDocumentationStatusHistory.Add(new GangDocumentationStatusHistoryRecord
        {
            HistoryEntryId = Guid.NewGuid(),
            DocumentationId = documentationId,
            ActionType = actionType,
            Summary = summary,
            PreviousWorkflowStatus = previousWorkflowStatus,
            NewWorkflowStatus = newWorkflowStatus,
            DecisionNote = decisionNote,
            ChangedBy = changedBy,
            ChangedByIdentity = changedByIdentity,
            ChangedAtUtc = timestampUtc
        });
    }

    private static void SyncLegacyWorkflowFields(
        GangDocumentationRecordEntity documentation,
        GangDocumentationReviewRecord review
    )
    {
        documentation.DocumentationStatus = review.WorkflowStatus;
        documentation.ApprovalStatus = GangDocumentationCatalog.GetApprovalStatus(review.WorkflowStatus);
        documentation.Reviewer = review.ReviewerName;
        documentation.ReviewDueDateUtc = review.ReviewDueDateUtc;
    }

    private static void EnsureWorkflowEditableForContent(string workflowStatus)
    {
        if (GangDocumentationCatalog.IsEditableWorkflowStatus(workflowStatus))
        {
            return;
        }

        throw new InvalidOperationException(
            "Gang documentation content can only be edited while the record is in Draft or Returned for Changes."
        );
    }

    private static void EnsureValidTransition(
        string currentWorkflowStatus,
        string workflowAction,
        WorkflowTransitionDefinition transition
    )
    {
        if (transition.AllowedFrom.Contains(currentWorkflowStatus))
        {
            return;
        }

        var allowedFrom = string.Join(
            ", ",
            transition.AllowedFrom.Select(GangDocumentationCatalog.GetWorkflowStatusDisplayName)
        );
        throw new InvalidOperationException(
            $"Cannot {workflowAction} when the record is in {GangDocumentationCatalog.GetWorkflowStatusDisplayName(currentWorkflowStatus)}. Allowed current states: {allowedFrom}."
        );
    }

    private Task SaveChangesWithWritePolicyAsync(
        WorkspaceDbContext db,
        string operationName,
        Guid caseId,
        Guid documentationId,
        CancellationToken ct
    )
    {
        return _workspaceWriteGate.ExecuteWriteAsync(
            operationName,
            writeCt => db.SaveChangesAsync(writeCt),
            ct,
            correlationId: AppFileLogger.GetScopeValue("correlationId"),
            fields: new Dictionary<string, object?>
            {
                ["caseId"] = caseId.ToString("D"),
                ["documentationId"] = documentationId.ToString("D")
            }
        );
    }

    private async Task WriteAuditAsync(
        string actionType,
        Guid caseId,
        string summary,
        object payload,
        CancellationToken ct
    )
    {
        await _auditLogService.AddAsync(
            new AuditEvent
            {
                TimestampUtc = _clock.UtcNow.ToUniversalTime(),
                Operator = Environment.UserName,
                ActionType = actionType,
                CaseId = caseId,
                Summary = summary,
                JsonPayload = JsonSerializer.Serialize(payload)
            },
            ct
        );
    }

    private static void LogDiagnostic(
        string eventName,
        string message,
        Guid caseId,
        Guid documentationId,
        Guid targetId,
        IReadOnlyDictionary<string, object?> fields
    )
    {
        var eventFields = new Dictionary<string, object?>(fields)
        {
            ["caseId"] = caseId.ToString("D"),
            ["documentationId"] = documentationId.ToString("D"),
            ["targetId"] = targetId.ToString("D"),
            ["correlationId"] = AppFileLogger.GetScopeValue("correlationId")
        };

        AppFileLogger.LogEvent(
            eventName: eventName,
            level: "INFO",
            message: message,
            fields: eventFields
        );
    }

    private static string BuildTransitionSummary(
        string workflowAction,
        string previousWorkflowStatus,
        string newWorkflowStatus,
        string? reviewerName,
        string? decisionNote
    )
    {
        var parts = new List<string>
        {
            $"{GangDocumentationCatalog.GetWorkflowActionDisplayName(workflowAction)} moved the record from {GangDocumentationCatalog.GetWorkflowStatusDisplayName(previousWorkflowStatus)} to {GangDocumentationCatalog.GetWorkflowStatusDisplayName(newWorkflowStatus)}."
        };

        if (!string.IsNullOrWhiteSpace(reviewerName))
        {
            parts.Add($"Reviewer: {reviewerName}.");
        }

        if (!string.IsNullOrWhiteSpace(decisionNote))
        {
            parts.Add($"Note: {decisionNote}.");
        }

        return string.Join(" ", parts);
    }

    private static string GetAuditActionType(string workflowAction)
    {
        return workflowAction switch
        {
            GangDocumentationCatalog.WorkflowActionSubmitForReview => "GangDocumentationSubmittedForReview",
            GangDocumentationCatalog.WorkflowActionApprove => "GangDocumentationApproved",
            GangDocumentationCatalog.WorkflowActionReturnForChanges => "GangDocumentationReturnedForChanges",
            GangDocumentationCatalog.WorkflowActionMarkInactive => "GangDocumentationMarkedInactive",
            GangDocumentationCatalog.WorkflowActionMarkPurgeReview => "GangDocumentationMarkedPurgeReview",
            GangDocumentationCatalog.WorkflowActionPurge => "GangDocumentationPurged",
            GangDocumentationCatalog.WorkflowActionRestoreToApproved => "GangDocumentationRestoredToApproved",
            GangDocumentationCatalog.WorkflowActionRestoreToInactive => "GangDocumentationRestoredToInactive",
            _ => "GangDocumentationWorkflowChanged"
        };
    }

    private static string GetDiagnosticEventName(string workflowAction)
    {
        return workflowAction switch
        {
            GangDocumentationCatalog.WorkflowActionSubmitForReview => "GangDocumentationSubmittedForReview",
            GangDocumentationCatalog.WorkflowActionApprove => "GangDocumentationApproved",
            GangDocumentationCatalog.WorkflowActionReturnForChanges => "GangDocumentationReturnedForChanges",
            GangDocumentationCatalog.WorkflowActionMarkInactive => "GangDocumentationMarkedInactive",
            GangDocumentationCatalog.WorkflowActionMarkPurgeReview => "GangDocumentationMarkedPurgeReview",
            GangDocumentationCatalog.WorkflowActionPurge => "GangDocumentationPurged",
            GangDocumentationCatalog.WorkflowActionRestoreToApproved => "GangDocumentationRestoredToApproved",
            GangDocumentationCatalog.WorkflowActionRestoreToInactive => "GangDocumentationRestoredToInactive",
            _ => "GangDocumentationWorkflowChanged"
        };
    }

    private static void ValidateCaseAndTarget(Guid caseId, Guid targetId)
    {
        if (caseId == Guid.Empty || targetId == Guid.Empty)
        {
            throw new ArgumentException("CaseId and TargetId are required.");
        }
    }

    private static string NormalizeRequired(string? value, string errorMessage)
    {
        var normalized = value?.Trim() ?? string.Empty;
        if (normalized.Length == 0)
        {
            throw new ArgumentException(errorMessage);
        }

        return normalized;
    }

    private static string? NormalizeOptional(string? value)
    {
        var normalized = value?.Trim();
        return string.IsNullOrWhiteSpace(normalized) ? null : normalized;
    }

    private static DateTimeOffset? NormalizeDate(DateTimeOffset? value)
    {
        return value?.ToUniversalTime();
    }

    private static string NormalizeCatalogValue(
        string? value,
        IReadOnlyList<string> allowedValues,
        string parameterName
    )
    {
        var normalized = NormalizeRequired(value, $"{parameterName} is required.").ToLowerInvariant();
        if (!allowedValues.Contains(normalized, StringComparer.Ordinal))
        {
            throw new ArgumentException($"{parameterName} is not supported.", parameterName);
        }

        return normalized;
    }

    private static string NormalizeWorkflowStatus(string? workflowStatus)
    {
        return workflowStatus?.Trim().ToLowerInvariant() switch
        {
            "pending review" => GangDocumentationCatalog.WorkflowStatusPendingSupervisorReview,
            null or "" => GangDocumentationCatalog.WorkflowStatusDraft,
            var normalized when GangDocumentationCatalog.WorkflowStatuses.Contains(normalized, StringComparer.Ordinal) => normalized,
            _ => GangDocumentationCatalog.WorkflowStatusDraft
        };
    }

    private sealed record WorkflowTransitionDefinition(
        string TargetStatus,
        IReadOnlySet<string> AllowedFrom,
        bool RequiresReviewer,
        bool RequiresDecisionNote
    );
}







