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
            DocumentationStatus = NormalizeCatalogValue(
                request.DocumentationStatus,
                GangDocumentationCatalog.DocumentationStatuses,
                nameof(request.DocumentationStatus)
            ),
            ApprovalStatus = NormalizeCatalogValue(
                request.ApprovalStatus,
                GangDocumentationCatalog.ApprovalStatuses,
                nameof(request.ApprovalStatus)
            ),
            Reviewer = NormalizeOptional(request.Reviewer),
            ReviewDueDateUtc = NormalizeDate(request.ReviewDueDateUtc),
            Summary = NormalizeRequired(request.Summary, "Summary is required."),
            Notes = NormalizeOptional(request.Notes),
            CreatedAtUtc = now,
            UpdatedAtUtc = now
        };

        db.GangDocumentationRecords.Add(documentation);
        AddHistoryEntry(
            db,
            documentation.DocumentationId,
            "Created",
            $"Documentation created with status {documentation.DocumentationStatus} and approval {documentation.ApprovalStatus}.",
            now
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
                ["documentationStatus"] = documentation.DocumentationStatus,
                ["approvalStatus"] = documentation.ApprovalStatus
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
                documentation.DocumentationStatus,
                documentation.ApprovalStatus,
                documentation.Reviewer,
                documentation.ReviewDueDateUtc,
                documentation.Summary
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

        var documentation = await db.GangDocumentationRecords.FirstOrDefaultAsync(
            record => record.CaseId == request.CaseId && record.DocumentationId == request.DocumentationId,
            ct
        );
        if (documentation is null)
        {
            throw new InvalidOperationException("Gang documentation record not found.");
        }

        var target = await GetTargetAsync(db, documentation.CaseId, documentation.TargetId, ct);
        var organization = await GetOrganizationAsync(db, request.OrganizationId, ct);
        var subgroup = await ValidateAndGetSubgroupAsync(
            db,
            request.OrganizationId,
            request.SubgroupOrganizationId,
            ct
        );

        var previousDocumentationStatus = documentation.DocumentationStatus;
        var previousApprovalStatus = documentation.ApprovalStatus;
        var previousReviewer = documentation.Reviewer;
        var previousReviewDueDateUtc = documentation.ReviewDueDateUtc;

        documentation.GlobalEntityId = target.GlobalEntityId;
        documentation.OrganizationId = organization.OrganizationId;
        documentation.SubgroupOrganizationId = subgroup?.OrganizationId;
        documentation.AffiliationRole = NormalizeCatalogValue(
            request.AffiliationRole,
            GangDocumentationCatalog.AffiliationRoles,
            nameof(request.AffiliationRole)
        );
        documentation.DocumentationStatus = NormalizeCatalogValue(
            request.DocumentationStatus,
            GangDocumentationCatalog.DocumentationStatuses,
            nameof(request.DocumentationStatus)
        );
        documentation.ApprovalStatus = NormalizeCatalogValue(
            request.ApprovalStatus,
            GangDocumentationCatalog.ApprovalStatuses,
            nameof(request.ApprovalStatus)
        );
        documentation.Reviewer = NormalizeOptional(request.Reviewer);
        documentation.ReviewDueDateUtc = NormalizeDate(request.ReviewDueDateUtc);
        documentation.Summary = NormalizeRequired(request.Summary, "Summary is required.");
        documentation.Notes = NormalizeOptional(request.Notes);
        documentation.UpdatedAtUtc = _clock.UtcNow.ToUniversalTime();

        var workflowChanged = HasWorkflowChanged(
            previousDocumentationStatus,
            documentation.DocumentationStatus,
            previousApprovalStatus,
            documentation.ApprovalStatus,
            previousReviewer,
            documentation.Reviewer,
            previousReviewDueDateUtc,
            documentation.ReviewDueDateUtc
        );

        if (workflowChanged)
        {
            AddHistoryEntry(
                db,
                documentation.DocumentationId,
                "WorkflowUpdated",
                BuildWorkflowSummary(
                    previousDocumentationStatus,
                    documentation.DocumentationStatus,
                    previousApprovalStatus,
                    documentation.ApprovalStatus,
                    previousReviewer,
                    documentation.Reviewer,
                    previousReviewDueDateUtc,
                    documentation.ReviewDueDateUtc
                ),
                documentation.UpdatedAtUtc
            );
        }

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
                ["documentationStatus"] = documentation.DocumentationStatus,
                ["approvalStatus"] = documentation.ApprovalStatus
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
                documentation.DocumentationStatus,
                documentation.ApprovalStatus,
                documentation.Reviewer,
                documentation.ReviewDueDateUtc,
                documentation.Summary
            },
            ct
        );

        if (workflowChanged)
        {
            await WriteAuditAsync(
                "GangDocumentationStatusChanged",
                request.CaseId,
                $"Gang documentation workflow updated for {target.DisplayName}.",
                new
                {
                    documentation.DocumentationId,
                    documentation.TargetId,
                    target.DisplayName,
                    PreviousDocumentationStatus = previousDocumentationStatus,
                    documentation.DocumentationStatus,
                    PreviousApprovalStatus = previousApprovalStatus,
                    documentation.ApprovalStatus,
                    PreviousReviewer = previousReviewer,
                    documentation.Reviewer,
                    PreviousReviewDueDateUtc = previousReviewDueDateUtc,
                    documentation.ReviewDueDateUtc
                },
                ct
            );
        }

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

        var documentation = await db.GangDocumentationRecords.FirstOrDefaultAsync(
            record => record.CaseId == request.CaseId && record.DocumentationId == request.DocumentationId,
            ct
        );
        if (documentation is null)
        {
            throw new InvalidOperationException("Gang documentation record not found.");
        }

        var targetDisplayName = await db.Targets
            .AsNoTracking()
            .Where(target => target.TargetId == documentation.TargetId)
            .Select(target => target.DisplayName)
            .FirstOrDefaultAsync(ct)
            ?? $"Target {documentation.TargetId:D}";

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
                ["isMet"] = criterion.IsMet
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
                criterion.SortOrder
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

        var documentation = await db.GangDocumentationRecords.FirstOrDefaultAsync(
            record => record.CaseId == caseId && record.DocumentationId == documentationId,
            ct
        );
        if (documentation is null)
        {
            return;
        }

        var criterion = await db.GangDocumentationCriteria.FirstOrDefaultAsync(
            record => record.DocumentationId == documentationId && record.CriterionId == criterionId,
            ct
        );
        if (criterion is null)
        {
            return;
        }

        var targetDisplayName = await db.Targets
            .AsNoTracking()
            .Where(target => target.TargetId == documentation.TargetId)
            .Select(target => target.DisplayName)
            .FirstOrDefaultAsync(ct)
            ?? $"Target {documentation.TargetId:D}";

        documentation.UpdatedAtUtc = _clock.UtcNow.ToUniversalTime();
        db.GangDocumentationCriteria.Remove(criterion);
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
                ["criterionType"] = criterion.CriterionType
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
                criterion.CriterionType
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
            record.DocumentationStatus,
            record.ApprovalStatus,
            record.Reviewer,
            record.ReviewDueDateUtc,
            record.Summary,
            record.Notes,
            record.CreatedAtUtc,
            record.UpdatedAtUtc,
            criteria,
            history
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
            record.ChangedBy,
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

    private static void AddHistoryEntry(
        WorkspaceDbContext db,
        Guid documentationId,
        string actionType,
        string summary,
        DateTimeOffset timestampUtc
    )
    {
        db.GangDocumentationStatusHistory.Add(new GangDocumentationStatusHistoryRecord
        {
            HistoryEntryId = Guid.NewGuid(),
            DocumentationId = documentationId,
            ActionType = actionType,
            Summary = summary,
            ChangedBy = Environment.UserName,
            ChangedAtUtc = timestampUtc
        });
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

    private static bool HasWorkflowChanged(
        string previousDocumentationStatus,
        string currentDocumentationStatus,
        string previousApprovalStatus,
        string currentApprovalStatus,
        string? previousReviewer,
        string? currentReviewer,
        DateTimeOffset? previousReviewDueDateUtc,
        DateTimeOffset? currentReviewDueDateUtc
    )
    {
        return !string.Equals(
                previousDocumentationStatus,
                currentDocumentationStatus,
                StringComparison.Ordinal
            )
            || !string.Equals(previousApprovalStatus, currentApprovalStatus, StringComparison.Ordinal)
            || !string.Equals(previousReviewer, currentReviewer, StringComparison.Ordinal)
            || previousReviewDueDateUtc != currentReviewDueDateUtc;
    }

    private static string BuildWorkflowSummary(
        string previousDocumentationStatus,
        string currentDocumentationStatus,
        string previousApprovalStatus,
        string currentApprovalStatus,
        string? previousReviewer,
        string? currentReviewer,
        DateTimeOffset? previousReviewDueDateUtc,
        DateTimeOffset? currentReviewDueDateUtc
    )
    {
        var changes = new List<string>();
        if (!string.Equals(previousDocumentationStatus, currentDocumentationStatus, StringComparison.Ordinal))
        {
            changes.Add($"status {previousDocumentationStatus} -> {currentDocumentationStatus}");
        }

        if (!string.Equals(previousApprovalStatus, currentApprovalStatus, StringComparison.Ordinal))
        {
            changes.Add($"approval {previousApprovalStatus} -> {currentApprovalStatus}");
        }

        if (!string.Equals(previousReviewer, currentReviewer, StringComparison.Ordinal))
        {
            changes.Add($"reviewer {FormatNullable(previousReviewer)} -> {FormatNullable(currentReviewer)}");
        }

        if (previousReviewDueDateUtc != currentReviewDueDateUtc)
        {
            changes.Add(
                $"review due {FormatNullableDate(previousReviewDueDateUtc)} -> {FormatNullableDate(currentReviewDueDateUtc)}"
            );
        }

        return changes.Count == 0
            ? "Gang documentation workflow updated."
            : $"Gang documentation workflow updated: {string.Join("; ", changes)}.";
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

    private static string FormatNullable(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? "(none)" : value;
    }

    private static string FormatNullableDate(DateTimeOffset? value)
    {
        return value.HasValue ? value.Value.ToString("yyyy-MM-dd") : "(none)";
    }
}
