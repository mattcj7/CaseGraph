using CaseGraph.Core.Abstractions;
using CaseGraph.Core.Diagnostics;
using CaseGraph.Core.Models;
using CaseGraph.Infrastructure.Persistence;
using CaseGraph.Infrastructure.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using System.Text.Json;

namespace CaseGraph.Infrastructure.Services;

public sealed class TargetRegistryService : ITargetRegistryService
{
    private const string ManualSourceType = "Manual";
    private const string ManualModuleVersion = "manual-ui@1";
    private const string DerivedSourceType = "Derived";
    private const string ParticipantLinkModuleVersion = "UI-Link-v1";

    private readonly IDbContextFactory<WorkspaceDbContext> _dbContextFactory;
    private readonly IWorkspaceDatabaseInitializer _databaseInitializer;
    private readonly IWorkspaceWriteGate _workspaceWriteGate;
    private readonly IAuditLogService _auditLogService;
    private readonly IClock _clock;
    private readonly ITargetMessagePresenceIndexService? _targetMessagePresenceIndexService;

    public TargetRegistryService(
        IDbContextFactory<WorkspaceDbContext> dbContextFactory,
        IWorkspaceDatabaseInitializer databaseInitializer,
        IWorkspaceWriteGate workspaceWriteGate,
        IAuditLogService auditLogService,
        IClock clock,
        ITargetMessagePresenceIndexService? targetMessagePresenceIndexService = null
    )
    {
        _dbContextFactory = dbContextFactory;
        _databaseInitializer = databaseInitializer;
        _workspaceWriteGate = workspaceWriteGate;
        _auditLogService = auditLogService;
        _clock = clock;
        _targetMessagePresenceIndexService = targetMessagePresenceIndexService;
    }

    public async Task<IReadOnlyList<TargetSummary>> GetTargetsAsync(
        Guid caseId,
        string? search,
        CancellationToken ct
    )
    {
        await _databaseInitializer.EnsureInitializedAsync(ct);

        await using var db = await _dbContextFactory.CreateDbContextAsync(ct);
        var query = db.Targets
            .AsNoTracking()
            .Where(t => t.CaseId == caseId);

        var normalizedSearch = search?.Trim();
        if (!string.IsNullOrWhiteSpace(normalizedSearch))
        {
            var lowered = normalizedSearch.ToLowerInvariant();
            query = query.Where(t =>
                t.DisplayName.ToLower().Contains(lowered)
                || (t.PrimaryAlias != null && t.PrimaryAlias.ToLower().Contains(lowered))
                || (t.GlobalPerson != null && t.GlobalPerson.DisplayName.ToLower().Contains(lowered))
            );
        }

        var targets = await query
            .Select(target => new
            {
                Target = target,
                GlobalDisplayName = target.GlobalPerson == null
                    ? null
                    : target.GlobalPerson.DisplayName
            })
            .ToListAsync(ct);

        return targets
            .OrderByDescending(item =>
                item.Target.UpdatedAtUtc == default
                    ? item.Target.CreatedAtUtc
                    : item.Target.UpdatedAtUtc
            )
            .ThenBy(item => item.Target.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.Target.TargetId)
            .Select(item => MapSummary(item.Target, item.GlobalDisplayName))
            .ToList();
    }

    public async Task<TargetDetails?> GetTargetDetailsAsync(Guid caseId, Guid targetId, CancellationToken ct)
    {
        await _databaseInitializer.EnsureInitializedAsync(ct);

        await using var db = await _dbContextFactory.CreateDbContextAsync(ct);
        var target = await db.Targets
            .AsNoTracking()
            .FirstOrDefaultAsync(t => t.CaseId == caseId && t.TargetId == targetId, ct);
        if (target is null)
        {
            return null;
        }

        var aliases = await db.TargetAliases
            .AsNoTracking()
            .Where(a => a.CaseId == caseId && a.TargetId == targetId)
            .OrderBy(a => a.Alias)
            .ToListAsync(ct);

        var identifiers = await db.TargetIdentifierLinks
            .AsNoTracking()
            .Include(link => link.Identifier)
            .Where(link => link.CaseId == caseId && link.TargetId == targetId)
            .OrderBy(link => link.IsPrimary ? 0 : 1)
            .ThenBy(link => link.Identifier!.Type)
            .ThenBy(link => link.Identifier!.ValueRaw)
            .ToListAsync(ct);

        var whereSeenCount = await db.TargetMessagePresences
            .AsNoTracking()
            .Where(row => row.CaseId == caseId && row.TargetId == targetId)
            .Select(row => row.MessageEventId)
            .Distinct()
            .CountAsync(ct);

        var globalPersonInfo = await BuildTargetGlobalPersonInfoAsync(
            db,
            caseId,
            target.GlobalEntityId,
            ct
        );

        var globalDisplayName = globalPersonInfo?.DisplayName;

        return new TargetDetails(
            MapSummary(target, globalDisplayName),
            aliases.Select(MapAlias).ToList(),
            identifiers
                .Where(link => link.Identifier is not null)
                .Select(link => MapIdentifier(link.Identifier!, link.IsPrimary))
                .ToList(),
            whereSeenCount,
            globalPersonInfo
        );
    }

    public async Task<IReadOnlyList<GlobalPersonSummary>> SearchGlobalPersonsAsync(
        string? search,
        int take,
        CancellationToken ct
    )
    {
        await _databaseInitializer.EnsureInitializedAsync(ct);
        await using var db = await _dbContextFactory.CreateDbContextAsync(ct);

        var query = db.PersonEntities.AsNoTracking();
        var normalized = search?.Trim();
        if (!string.IsNullOrWhiteSpace(normalized))
        {
            var lowered = normalized.ToLowerInvariant();
            query = query.Where(person =>
                person.DisplayName.ToLower().Contains(lowered)
                || db.PersonAliases.Any(alias =>
                    alias.GlobalEntityId == person.GlobalEntityId
                    && alias.AliasNormalized.Contains(lowered)
                )
                || db.PersonIdentifiers.Any(identifier =>
                    identifier.GlobalEntityId == person.GlobalEntityId
                    && (
                        identifier.ValueNormalized.ToLower().Contains(lowered)
                        || identifier.ValueDisplay.ToLower().Contains(lowered)
                    )
                )
            );
        }

        var clampedTake = Math.Clamp(take, 1, 200);
        var rows = await query.ToListAsync(ct);

        return rows
            .OrderByDescending(person =>
                person.UpdatedAtUtc == default
                    ? person.CreatedAtUtc
                    : person.UpdatedAtUtc
            )
            .ThenBy(person => person.DisplayName, StringComparer.OrdinalIgnoreCase)
            .Take(clampedTake)
            .Select(MapGlobalSummary)
            .ToList();
    }

    public async Task<TargetSummary> CreateTargetAsync(CreateTargetRequest request, CancellationToken ct)
    {
        if (request.CaseId == Guid.Empty)
        {
            throw new ArgumentException("CaseId is required.", nameof(request));
        }

        var displayName = NormalizeDisplayName(request.DisplayName);
        if (displayName.Length == 0)
        {
            throw new ArgumentException("Display name is required.", nameof(request));
        }

        await _databaseInitializer.EnsureInitializedAsync(ct);

        var now = _clock.UtcNow.ToUniversalTime();
        await using var db = await _dbContextFactory.CreateDbContextAsync(ct);
        if (request.CreateGlobalPerson && request.GlobalEntityId.HasValue)
        {
            throw new ArgumentException(
                "Specify either CreateGlobalPerson or GlobalEntityId, not both.",
                nameof(request)
            );
        }

        PersonEntityRecord? globalPerson = null;
        if (request.CreateGlobalPerson)
        {
            globalPerson = new PersonEntityRecord
            {
                GlobalEntityId = Guid.NewGuid(),
                DisplayName = displayName,
                CreatedAtUtc = now,
                UpdatedAtUtc = now
            };
            db.PersonEntities.Add(globalPerson);
        }
        else if (request.GlobalEntityId.HasValue)
        {
            globalPerson = await db.PersonEntities.FirstOrDefaultAsync(
                person => person.GlobalEntityId == request.GlobalEntityId.Value,
                ct
            );
            if (globalPerson is null)
            {
                throw new InvalidOperationException("Global person not found.");
            }
        }

        var target = new TargetRecord
        {
            TargetId = Guid.NewGuid(),
            CaseId = request.CaseId,
            GlobalEntityId = globalPerson?.GlobalEntityId,
            DisplayName = displayName,
            PrimaryAlias = NormalizeOptional(request.PrimaryAlias),
            Notes = NormalizeOptional(request.Notes),
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
            SourceType = ManualSourceType,
            SourceEvidenceItemId = null,
            SourceLocator = NormalizeSourceLocator(request.SourceLocator, "manual:targets/create"),
            IngestModuleVersion = ManualModuleVersion
        };
        db.Targets.Add(target);

        TargetAliasRecord? aliasRecord = null;
        if (!string.IsNullOrWhiteSpace(target.PrimaryAlias))
        {
            aliasRecord = new TargetAliasRecord
            {
                AliasId = Guid.NewGuid(),
                TargetId = target.TargetId,
                CaseId = target.CaseId,
                Alias = target.PrimaryAlias,
                AliasNormalized = NormalizeAlias(target.PrimaryAlias),
                SourceType = ManualSourceType,
                SourceEvidenceItemId = null,
                SourceLocator = "manual:targets/primary-alias",
                IngestModuleVersion = ManualModuleVersion
            };
            db.TargetAliases.Add(aliasRecord);

            if (globalPerson is not null && request.CreateGlobalPerson)
            {
                db.PersonAliases.Add(new PersonAliasRecord
                {
                    AliasId = Guid.NewGuid(),
                    GlobalEntityId = globalPerson.GlobalEntityId,
                    Alias = target.PrimaryAlias,
                    AliasNormalized = NormalizeAlias(target.PrimaryAlias),
                    Notes = null
                });
            }
        }

        await SaveChangesWithWritePolicyAsync(
            db,
            operationName: "TargetRegistry.CreateTarget",
            caseId: request.CaseId,
            ct
        );

        await WriteAuditAsync(
            actionType: "TargetCreated",
            caseId: request.CaseId,
            evidenceItemId: null,
            summary: $"Target created: {target.DisplayName}",
            payload: new
            {
                target.TargetId,
                target.DisplayName,
                target.PrimaryAlias
            },
            ct
        );

        if (aliasRecord is not null)
        {
            await WriteAuditAsync(
                actionType: "AliasAdded",
                caseId: request.CaseId,
                evidenceItemId: null,
                summary: $"Alias added to target {target.DisplayName}: {aliasRecord.Alias}",
                payload: new
                {
                    target.TargetId,
                    aliasRecord.AliasId,
                    aliasRecord.Alias
                },
                ct
            );
        }

        if (globalPerson is not null)
        {
            await WriteAuditAsync(
                actionType: "TargetLinkedToGlobalPerson",
                caseId: request.CaseId,
                evidenceItemId: null,
                summary: $"Target linked to global person: {globalPerson.DisplayName}",
                payload: new
                {
                    target.TargetId,
                    globalPerson.GlobalEntityId,
                    globalPerson.DisplayName
                },
                ct
            );
        }

        return MapSummary(target, globalPerson?.DisplayName);
    }

    public async Task<TargetSummary> UpdateTargetAsync(UpdateTargetRequest request, CancellationToken ct)
    {
        var displayName = NormalizeDisplayName(request.DisplayName);
        if (displayName.Length == 0)
        {
            throw new ArgumentException("Display name is required.", nameof(request));
        }

        await _databaseInitializer.EnsureInitializedAsync(ct);
        await using var db = await _dbContextFactory.CreateDbContextAsync(ct);
        var target = await db.Targets.FirstOrDefaultAsync(
            t => t.CaseId == request.CaseId && t.TargetId == request.TargetId,
            ct
        );
        if (target is null)
        {
            throw new InvalidOperationException("Target not found.");
        }

        target.DisplayName = displayName;
        target.PrimaryAlias = NormalizeOptional(request.PrimaryAlias);
        target.Notes = NormalizeOptional(request.Notes);
        target.UpdatedAtUtc = _clock.UtcNow.ToUniversalTime();
        await SaveChangesWithWritePolicyAsync(
            db,
            operationName: "TargetRegistry.UpdateTarget",
            caseId: request.CaseId,
            ct
        );

        await WriteAuditAsync(
            actionType: "TargetUpdated",
            caseId: request.CaseId,
            evidenceItemId: null,
            summary: $"Target updated: {target.DisplayName}",
            payload: new
            {
                target.TargetId,
                target.DisplayName,
                target.PrimaryAlias
            },
            ct
        );

        string? globalDisplayName = null;
        if (target.GlobalEntityId.HasValue)
        {
            globalDisplayName = await db.PersonEntities
                .AsNoTracking()
                .Where(person => person.GlobalEntityId == target.GlobalEntityId.Value)
                .Select(person => person.DisplayName)
                .FirstOrDefaultAsync(ct);
        }

        return MapSummary(target, globalDisplayName);
    }

    public async Task<TargetGlobalPersonInfo> CreateAndLinkGlobalPersonAsync(
        CreateGlobalPersonForTargetRequest request,
        CancellationToken ct
    )
    {
        if (request.CaseId == Guid.Empty || request.TargetId == Guid.Empty)
        {
            throw new ArgumentException("CaseId and TargetId are required.", nameof(request));
        }

        await _databaseInitializer.EnsureInitializedAsync(ct);
        await using var db = await _dbContextFactory.CreateDbContextAsync(ct);

        var target = await db.Targets.FirstOrDefaultAsync(
            record => record.CaseId == request.CaseId && record.TargetId == request.TargetId,
            ct
        );
        if (target is null)
        {
            throw new InvalidOperationException("Target not found.");
        }

        var now = _clock.UtcNow.ToUniversalTime();
        var globalPerson = new PersonEntityRecord
        {
            GlobalEntityId = Guid.NewGuid(),
            DisplayName = NormalizeDisplayName(request.DisplayName).Length == 0
                ? target.DisplayName
                : NormalizeDisplayName(request.DisplayName),
            CreatedAtUtc = now,
            UpdatedAtUtc = now
        };
        db.PersonEntities.Add(globalPerson);

        if (!string.IsNullOrWhiteSpace(target.PrimaryAlias))
        {
            db.PersonAliases.Add(new PersonAliasRecord
            {
                AliasId = Guid.NewGuid(),
                GlobalEntityId = globalPerson.GlobalEntityId,
                Alias = target.PrimaryAlias,
                AliasNormalized = NormalizeAlias(target.PrimaryAlias),
                Notes = null
            });
        }

        target.GlobalEntityId = globalPerson.GlobalEntityId;
        target.UpdatedAtUtc = now;

        await SyncAllTargetIdentifiersToGlobalAsync(
            db,
            request.CaseId,
            target.TargetId,
            globalPerson.GlobalEntityId,
            request.ConflictResolution,
            request.SourceLocator,
            ct
        );

        var createdPersonIsStillLinked = target.GlobalEntityId == globalPerson.GlobalEntityId;
        if (!createdPersonIsStillLinked)
        {
            var orphanAliases = await db.PersonAliases
                .Where(alias => alias.GlobalEntityId == globalPerson.GlobalEntityId)
                .ToListAsync(ct);
            db.PersonAliases.RemoveRange(orphanAliases);
            db.PersonEntities.Remove(globalPerson);
        }

        await SaveChangesWithWritePolicyAsync(
            db,
            operationName: "TargetRegistry.CreateAndLinkGlobalPerson",
            caseId: request.CaseId,
            ct
        );

        var effectiveGlobalEntityId = target.GlobalEntityId ?? globalPerson.GlobalEntityId;
        var effectiveGlobalDisplayName = await db.PersonEntities
            .AsNoTracking()
            .Where(person => person.GlobalEntityId == effectiveGlobalEntityId)
            .Select(person => person.DisplayName)
            .FirstOrDefaultAsync(ct)
            ?? $"Global Person {effectiveGlobalEntityId:D}";

        if (createdPersonIsStillLinked)
        {
            await WriteAuditAsync(
                actionType: "GlobalPersonCreated",
                caseId: request.CaseId,
                evidenceItemId: null,
                summary: $"Global person created: {globalPerson.DisplayName}",
                payload: new
                {
                    globalPerson.GlobalEntityId,
                    globalPerson.DisplayName,
                    target.TargetId
                },
                ct
            );
        }

        await WriteAuditAsync(
            actionType: "TargetLinkedToGlobalPerson",
            caseId: request.CaseId,
            evidenceItemId: null,
            summary: $"Target linked to global person: {effectiveGlobalDisplayName}",
            payload: new
            {
                target.TargetId,
                GlobalEntityId = effectiveGlobalEntityId,
                DisplayName = effectiveGlobalDisplayName
            },
            ct
        );

        var info = await BuildTargetGlobalPersonInfoAsync(
            db,
            request.CaseId,
            effectiveGlobalEntityId,
            ct
        );
        return info ?? throw new InvalidOperationException("Global person info not found after linking.");
    }

    public async Task<TargetGlobalPersonInfo> LinkTargetToGlobalPersonAsync(
        LinkTargetToGlobalPersonRequest request,
        CancellationToken ct
    )
    {
        if (request.CaseId == Guid.Empty || request.TargetId == Guid.Empty || request.GlobalEntityId == Guid.Empty)
        {
            throw new ArgumentException("CaseId, TargetId, and GlobalEntityId are required.", nameof(request));
        }

        await _databaseInitializer.EnsureInitializedAsync(ct);
        await using var db = await _dbContextFactory.CreateDbContextAsync(ct);

        var target = await db.Targets.FirstOrDefaultAsync(
            record => record.CaseId == request.CaseId && record.TargetId == request.TargetId,
            ct
        );
        if (target is null)
        {
            throw new InvalidOperationException("Target not found.");
        }

        var globalPerson = await db.PersonEntities.FirstOrDefaultAsync(
            person => person.GlobalEntityId == request.GlobalEntityId,
            ct
        );
        if (globalPerson is null)
        {
            throw new InvalidOperationException("Global person not found.");
        }

        target.GlobalEntityId = globalPerson.GlobalEntityId;
        target.UpdatedAtUtc = _clock.UtcNow.ToUniversalTime();

        await SyncAllTargetIdentifiersToGlobalAsync(
            db,
            request.CaseId,
            request.TargetId,
            globalPerson.GlobalEntityId,
            request.ConflictResolution,
            request.SourceLocator,
            ct
        );

        var effectiveGlobalEntityId = target.GlobalEntityId ?? request.GlobalEntityId;
        var effectiveGlobalDisplayName = await db.PersonEntities
            .AsNoTracking()
            .Where(person => person.GlobalEntityId == effectiveGlobalEntityId)
            .Select(person => person.DisplayName)
            .FirstOrDefaultAsync(ct)
            ?? $"Global Person {effectiveGlobalEntityId:D}";

        await SaveChangesWithWritePolicyAsync(
            db,
            operationName: "TargetRegistry.LinkTargetToGlobalPerson",
            caseId: request.CaseId,
            ct
        );

        await WriteAuditAsync(
            actionType: "TargetLinkedToGlobalPerson",
            caseId: request.CaseId,
            evidenceItemId: null,
            summary: $"Target linked to global person: {effectiveGlobalDisplayName}",
            payload: new
            {
                request.TargetId,
                GlobalEntityId = effectiveGlobalEntityId,
                DisplayName = effectiveGlobalDisplayName
            },
            ct
        );

        var info = await BuildTargetGlobalPersonInfoAsync(
            db,
            request.CaseId,
            effectiveGlobalEntityId,
            ct
        );
        return info ?? throw new InvalidOperationException("Global person info not found after linking.");
    }

    public async Task UnlinkTargetFromGlobalPersonAsync(Guid caseId, Guid targetId, CancellationToken ct)
    {
        if (caseId == Guid.Empty || targetId == Guid.Empty)
        {
            throw new ArgumentException("CaseId and TargetId are required.");
        }

        await _databaseInitializer.EnsureInitializedAsync(ct);
        await using var db = await _dbContextFactory.CreateDbContextAsync(ct);

        var target = await db.Targets.FirstOrDefaultAsync(
            record => record.CaseId == caseId && record.TargetId == targetId,
            ct
        );
        if (target is null || !target.GlobalEntityId.HasValue)
        {
            return;
        }

        var globalEntityId = target.GlobalEntityId.Value;
        target.GlobalEntityId = null;
        target.UpdatedAtUtc = _clock.UtcNow.ToUniversalTime();

        await SaveChangesWithWritePolicyAsync(
            db,
            operationName: "TargetRegistry.UnlinkTargetFromGlobalPerson",
            caseId: caseId,
            ct
        );

        await WriteAuditAsync(
            actionType: "TargetUnlinkedFromGlobalPerson",
            caseId: caseId,
            evidenceItemId: null,
            summary: "Target unlinked from global person.",
            payload: new
            {
                targetId,
                globalEntityId
            },
            ct
        );
    }

    public async Task<TargetAliasInfo> AddAliasAsync(AddTargetAliasRequest request, CancellationToken ct)
    {
        var alias = NormalizeDisplayName(request.Alias);
        if (alias.Length == 0)
        {
            throw new ArgumentException("Alias is required.", nameof(request));
        }

        await _databaseInitializer.EnsureInitializedAsync(ct);
        await using var db = await _dbContextFactory.CreateDbContextAsync(ct);

        var target = await db.Targets.FirstOrDefaultAsync(
            t => t.CaseId == request.CaseId && t.TargetId == request.TargetId,
            ct
        );
        if (target is null)
        {
            throw new InvalidOperationException("Target not found.");
        }

        var aliasNormalized = NormalizeAlias(alias);
        var existing = await db.TargetAliases.FirstOrDefaultAsync(
            a => a.CaseId == request.CaseId
                && a.TargetId == request.TargetId
                && a.AliasNormalized == aliasNormalized,
            ct
        );
        if (existing is not null)
        {
            return MapAlias(existing);
        }

        var aliasRecord = new TargetAliasRecord
        {
            AliasId = Guid.NewGuid(),
            TargetId = request.TargetId,
            CaseId = request.CaseId,
            Alias = alias,
            AliasNormalized = aliasNormalized,
            SourceType = ManualSourceType,
            SourceEvidenceItemId = null,
            SourceLocator = NormalizeSourceLocator(request.SourceLocator, "manual:targets/alias-add"),
            IngestModuleVersion = ManualModuleVersion
        };
        db.TargetAliases.Add(aliasRecord);
        target.UpdatedAtUtc = _clock.UtcNow.ToUniversalTime();
        await SaveChangesWithWritePolicyAsync(
            db,
            operationName: "TargetRegistry.AddAlias",
            caseId: request.CaseId,
            ct
        );

        await WriteAuditAsync(
            actionType: "AliasAdded",
            caseId: request.CaseId,
            evidenceItemId: null,
            summary: $"Alias added: {alias}",
            payload: new
            {
                request.TargetId,
                aliasRecord.AliasId,
                aliasRecord.Alias
            },
            ct
        );

        return MapAlias(aliasRecord);
    }

    public async Task RemoveAliasAsync(Guid caseId, Guid aliasId, CancellationToken ct)
    {
        await _databaseInitializer.EnsureInitializedAsync(ct);
        await using var db = await _dbContextFactory.CreateDbContextAsync(ct);

        var aliasRecord = await db.TargetAliases
            .Include(a => a.Target)
            .FirstOrDefaultAsync(a => a.CaseId == caseId && a.AliasId == aliasId, ct);
        if (aliasRecord is null)
        {
            return;
        }

        db.TargetAliases.Remove(aliasRecord);
        if (aliasRecord.Target is not null)
        {
            aliasRecord.Target.UpdatedAtUtc = _clock.UtcNow.ToUniversalTime();
        }

        await SaveChangesWithWritePolicyAsync(
            db,
            operationName: "TargetRegistry.RemoveAlias",
            caseId: caseId,
            ct
        );

        await WriteAuditAsync(
            actionType: "AliasRemoved",
            caseId: caseId,
            evidenceItemId: null,
            summary: $"Alias removed: {aliasRecord.Alias}",
            payload: new
            {
                aliasRecord.TargetId,
                aliasRecord.AliasId,
                aliasRecord.Alias
            },
            ct
        );
    }

    public Task<TargetIdentifierMutationResult> AddIdentifierAsync(
        AddTargetIdentifierRequest request,
        CancellationToken ct
    )
    {
        return UpsertIdentifierAsync(
            caseId: request.CaseId,
            targetId: request.TargetId,
            identifierIdToReplace: null,
            type: request.Type,
            valueRaw: request.ValueRaw,
            notes: request.Notes,
            isPrimary: request.IsPrimary,
            sourceType: ManualSourceType,
            sourceEvidenceItemId: null,
            sourceLocator: NormalizeSourceLocator(request.SourceLocator, "manual:targets/identifier-add"),
            ingestModuleVersion: ManualModuleVersion,
            conflictResolution: request.ConflictResolution,
            globalConflictResolution: request.GlobalConflictResolution,
            isUpdateOperation: false,
            ct: ct
        );
    }

    public Task<TargetIdentifierMutationResult> UpdateIdentifierAsync(
        UpdateTargetIdentifierRequest request,
        CancellationToken ct
    )
    {
        return UpsertIdentifierAsync(
            caseId: request.CaseId,
            targetId: request.TargetId,
            identifierIdToReplace: request.IdentifierId,
            type: request.Type,
            valueRaw: request.ValueRaw,
            notes: request.Notes,
            isPrimary: request.IsPrimary,
            sourceType: ManualSourceType,
            sourceEvidenceItemId: null,
            sourceLocator: NormalizeSourceLocator(request.SourceLocator, "manual:targets/identifier-update"),
            ingestModuleVersion: ManualModuleVersion,
            conflictResolution: request.ConflictResolution,
            globalConflictResolution: request.GlobalConflictResolution,
            isUpdateOperation: true,
            ct: ct
        );
    }

    public async Task RemoveIdentifierAsync(RemoveTargetIdentifierRequest request, CancellationToken ct)
    {
        await _databaseInitializer.EnsureInitializedAsync(ct);
        await using var db = await _dbContextFactory.CreateDbContextAsync(ct);

        var link = await db.TargetIdentifierLinks
            .Include(l => l.Identifier)
            .FirstOrDefaultAsync(
                l => l.CaseId == request.CaseId
                    && l.TargetId == request.TargetId
                    && l.IdentifierId == request.IdentifierId,
                ct
            );
        if (link is null)
        {
            return;
        }

        db.TargetIdentifierLinks.Remove(link);
        await SaveChangesWithWritePolicyAsync(
            db,
            operationName: "TargetRegistry.RemoveIdentifier.Unlink",
            caseId: request.CaseId,
            ct
        );

        await WriteAuditAsync(
            actionType: "IdentifierUnlinkedFromTarget",
            caseId: request.CaseId,
            evidenceItemId: null,
            summary: "Identifier unlinked from target.",
            payload: new
            {
                request.TargetId,
                request.IdentifierId
            },
            ct
        );

        await DeleteIdentifierIfOrphanedAsync(db, request.CaseId, request.IdentifierId, ct);
        await SaveChangesWithWritePolicyAsync(
            db,
            operationName: "TargetRegistry.RemoveIdentifier.Cleanup",
            caseId: request.CaseId,
            ct
        );

        if (_targetMessagePresenceIndexService is not null)
        {
            await _targetMessagePresenceIndexService.RefreshForIdentifierAsync(
                request.CaseId,
                request.IdentifierId,
                ct
            );
        }
    }

    public async Task<MessageParticipantLinkResult> LinkMessageParticipantAsync(
        LinkMessageParticipantRequest request,
        CancellationToken ct
    )
    {
        if (request.CaseId == Guid.Empty || request.MessageEventId == Guid.Empty)
        {
            throw new ArgumentException("CaseId and MessageEventId are required.", nameof(request));
        }

        var participantRaw = NormalizeDisplayName(request.ParticipantRaw);
        if (participantRaw.Length == 0)
        {
            throw new ArgumentException("Participant value is required.", nameof(request));
        }

        await _databaseInitializer.EnsureInitializedAsync(ct);
        await using var db = await _dbContextFactory.CreateDbContextAsync(ct);
        var messageEvent = await db.MessageEvents
            .AsNoTracking()
            .FirstOrDefaultAsync(
                e => e.CaseId == request.CaseId && e.MessageEventId == request.MessageEventId,
                ct
            );
        if (messageEvent is null)
        {
            throw new InvalidOperationException("Message event not found.");
        }

        var type = ResolveParticipantIdentifierType(request.RequestedIdentifierType, participantRaw);
        if (!IdentifierValueGuard.TryPrepare(type, participantRaw, out var preparedParticipantRaw))
        {
            throw new ArgumentException(IdentifierValueGuard.RequiredMessage, nameof(request.ParticipantRaw));
        }

        participantRaw = preparedParticipantRaw;
        var normalized = IdentifierNormalizer.Normalize(type, participantRaw);
        if (normalized.Length == 0)
        {
            throw new InvalidOperationException("Participant identifier could not be normalized.");
        }

        var typeText = ToIdentifierTypeText(type);
        var existingIdentifier = await db.Identifiers
            .FirstOrDefaultAsync(
                i => i.CaseId == request.CaseId
                    && i.Type == typeText
                    && i.ValueNormalized == normalized,
                ct
            );
        TargetIdentifierLinkRecord? existingIdentifierLink = null;
        if (existingIdentifier is not null)
        {
            existingIdentifierLink = await db.TargetIdentifierLinks
                .Include(link => link.Target)
                .FirstOrDefaultAsync(
                    link => link.CaseId == request.CaseId && link.IdentifierId == existingIdentifier.IdentifierId,
                    ct
                );
        }

        var linkSourceLocator = $"{messageEvent.SourceLocator};role={request.Role}";
        var createdTarget = false;
        var targetId = request.TargetId;
        TargetRecord? target = null;
        if (!targetId.HasValue)
        {
            if (existingIdentifierLink is not null)
            {
                if (request.ConflictResolution == IdentifierConflictResolution.Cancel)
                {
                    throw BuildIdentifierConflict(request.CaseId, existingIdentifier!, existingIdentifierLink);
                }

                if (request.ConflictResolution == IdentifierConflictResolution.UseExistingTarget)
                {
                    targetId = existingIdentifierLink.TargetId;
                }
            }

            if (!targetId.HasValue)
            {
                var newTarget = await CreateTargetInternalAsync(
                    db,
                    request.CaseId,
                    request.NewTargetDisplayName ?? participantRaw,
                    primaryAlias: null,
                    notes: $"Created from {request.Role} link action.",
                    sourceLocator: linkSourceLocator,
                    ct
                );
                targetId = newTarget.TargetId;
                createdTarget = true;
                target = newTarget;
            }
        }

        target ??= await db.Targets.FirstOrDefaultAsync(
            t => t.CaseId == request.CaseId && t.TargetId == targetId.Value,
            ct
        );
        if (target is null)
        {
            throw new InvalidOperationException("Target not found.");
        }

        var identifierMutation = await UpsertIdentifierInternalAsync(
            db,
            caseId: request.CaseId,
            targetId: target.TargetId,
            identifierIdToReplace: null,
            type: type,
            valueRaw: participantRaw,
            notes: "Linked from message participant.",
            isPrimary: false,
            sourceType: DerivedSourceType,
            sourceEvidenceItemId: messageEvent.EvidenceItemId,
            sourceLocator: linkSourceLocator,
            ingestModuleVersion: ParticipantLinkModuleVersion,
            conflictResolution: request.ConflictResolution,
            globalConflictResolution: request.GlobalConflictResolution,
            isUpdateOperation: false,
            ct
        );

        var effectiveTargetId = identifierMutation.EffectiveTargetId;
        var roleText = request.Role.ToString();
        var existingParticipantLink = await db.MessageParticipantLinks
            .FirstOrDefaultAsync(
                l => l.CaseId == request.CaseId
                    && l.MessageEventId == request.MessageEventId
                    && l.Role == roleText
                    && l.IdentifierId == identifierMutation.Identifier.IdentifierId
                    && l.ParticipantRaw == participantRaw,
                ct
            );

        Guid participantLinkId;
        if (existingParticipantLink is null)
        {
            var participantLink = new MessageParticipantLinkRecord
            {
                ParticipantLinkId = Guid.NewGuid(),
                CaseId = request.CaseId,
                MessageEventId = request.MessageEventId,
                Role = roleText,
                ParticipantRaw = participantRaw,
                IdentifierId = identifierMutation.Identifier.IdentifierId,
                TargetId = effectiveTargetId,
                CreatedAtUtc = _clock.UtcNow.ToUniversalTime(),
                SourceType = DerivedSourceType,
                SourceEvidenceItemId = messageEvent.EvidenceItemId,
                SourceLocator = linkSourceLocator,
                IngestModuleVersion = ParticipantLinkModuleVersion
            };
            db.MessageParticipantLinks.Add(participantLink);
            participantLinkId = participantLink.ParticipantLinkId;
        }
        else
        {
            existingParticipantLink.TargetId = effectiveTargetId;
            existingParticipantLink.SourceLocator = linkSourceLocator;
            participantLinkId = existingParticipantLink.ParticipantLinkId;
        }

        await SaveChangesWithWritePolicyAsync(
            db,
            operationName: "TargetRegistry.LinkMessageParticipant",
            caseId: request.CaseId,
            ct
        );

        if (_targetMessagePresenceIndexService is not null)
        {
            await _targetMessagePresenceIndexService.RefreshForIdentifierAsync(
                request.CaseId,
                identifierMutation.Identifier.IdentifierId,
                ct
            );
        }

        if (createdTarget)
        {
            await WriteAuditAsync(
                actionType: "CreateTargetFromParticipant",
                caseId: request.CaseId,
                evidenceItemId: messageEvent.EvidenceItemId,
                summary: "Target created from message participant.",
                payload: new
                {
                    request.MessageEventId,
                    target.TargetId,
                    target.DisplayName,
                    request.Role,
                    participantRaw,
                    messageEvent.EvidenceItemId,
                    SourceLocator = linkSourceLocator
                },
                ct
            );
        }

        await WriteAuditAsync(
            actionType: "LinkIdentifierToTarget",
            caseId: request.CaseId,
            evidenceItemId: messageEvent.EvidenceItemId,
            summary: "Identifier linked to target from message participant.",
            payload: new
            {
                request.MessageEventId,
                request.Role,
                Type = type.ToString(),
                participantRaw,
                NormalizedValue = normalized,
                IdentifierId = identifierMutation.Identifier.IdentifierId,
                TargetId = effectiveTargetId,
                messageEvent.EvidenceItemId,
                SourceLocator = linkSourceLocator,
                IngestModuleVersion = ParticipantLinkModuleVersion
            },
            ct
        );

        await WriteAuditAsync(
            actionType: "ParticipantLinked",
            caseId: request.CaseId,
            evidenceItemId: messageEvent.EvidenceItemId,
            summary: $"Message participant linked ({request.Role}).",
            payload: new
            {
                request.MessageEventId,
                Role = request.Role.ToString(),
                participantRaw,
                IdentifierId = identifierMutation.Identifier.IdentifierId,
                TargetId = effectiveTargetId
            },
            ct
        );

        return new MessageParticipantLinkResult(
            participantLinkId,
            identifierMutation.Identifier.IdentifierId,
            effectiveTargetId,
            createdTarget,
            identifierMutation.CreatedIdentifier,
            identifierMutation.MovedIdentifier,
            identifierMutation.UsedExistingTarget
        );
    }

    private async Task<TargetIdentifierMutationResult> UpsertIdentifierAsync(
        Guid caseId,
        Guid targetId,
        Guid? identifierIdToReplace,
        TargetIdentifierType type,
        string valueRaw,
        string? notes,
        bool isPrimary,
        string sourceType,
        Guid? sourceEvidenceItemId,
        string sourceLocator,
        string ingestModuleVersion,
        IdentifierConflictResolution conflictResolution,
        GlobalPersonIdentifierConflictResolution globalConflictResolution,
        bool isUpdateOperation,
        CancellationToken ct
    )
    {
        await _databaseInitializer.EnsureInitializedAsync(ct);
        await using var db = await _dbContextFactory.CreateDbContextAsync(ct);
        var result = await UpsertIdentifierInternalAsync(
            db,
            caseId,
            targetId,
            identifierIdToReplace,
            type,
            valueRaw,
            notes,
            isPrimary,
            sourceType,
            sourceEvidenceItemId,
            sourceLocator,
            ingestModuleVersion,
            conflictResolution,
            globalConflictResolution,
            isUpdateOperation,
            ct
        );
        await SaveChangesWithWritePolicyAsync(
            db,
            operationName: "TargetRegistry.UpsertIdentifier",
            caseId: caseId,
            ct
        );

        if (_targetMessagePresenceIndexService is not null)
        {
            await _targetMessagePresenceIndexService.RefreshForIdentifierAsync(
                caseId,
                result.Identifier.IdentifierId,
                ct
            );

            if (identifierIdToReplace.HasValue
                && identifierIdToReplace.Value != result.Identifier.IdentifierId)
            {
                await _targetMessagePresenceIndexService.RefreshForIdentifierAsync(
                    caseId,
                    identifierIdToReplace.Value,
                    ct
                );
            }
        }

        return result;
    }

    private async Task<TargetIdentifierMutationResult> UpsertIdentifierInternalAsync(
        WorkspaceDbContext db,
        Guid caseId,
        Guid targetId,
        Guid? identifierIdToReplace,
        TargetIdentifierType type,
        string valueRaw,
        string? notes,
        bool isPrimary,
        string sourceType,
        Guid? sourceEvidenceItemId,
        string sourceLocator,
        string ingestModuleVersion,
        IdentifierConflictResolution conflictResolution,
        GlobalPersonIdentifierConflictResolution globalConflictResolution,
        bool isUpdateOperation,
        CancellationToken ct
    )
    {
        var target = db.Targets.Local.FirstOrDefault(
            t => t.CaseId == caseId && t.TargetId == targetId
        ) ?? await db.Targets.FirstOrDefaultAsync(
            t => t.CaseId == caseId && t.TargetId == targetId,
            ct
        );
        if (target is null)
        {
            throw new InvalidOperationException("Target not found.");
        }

        var normalized = IdentifierNormalizer.Normalize(type, valueRaw);
        if (normalized.Length == 0)
        {
            throw new ArgumentException("Identifier value is required.", nameof(valueRaw));
        }

        var typeText = ToIdentifierTypeText(type);
        var now = _clock.UtcNow.ToUniversalTime();
        var trimmedRaw = NormalizeDisplayName(valueRaw);
        var normalizedNotes = NormalizeOptional(notes);
        var existing = await db.Identifiers.FirstOrDefaultAsync(
            i => i.CaseId == caseId
                && i.Type == typeText
                && i.ValueNormalized == normalized,
            ct
        );

        var createdIdentifier = false;
        IdentifierRecord identifier;
        if (existing is null)
        {
            identifier = new IdentifierRecord
            {
                IdentifierId = Guid.NewGuid(),
                CaseId = caseId,
                Type = typeText,
                ValueRaw = trimmedRaw,
                ValueNormalized = normalized,
                Notes = normalizedNotes,
                CreatedAtUtc = now,
                SourceType = sourceType,
                SourceEvidenceItemId = sourceEvidenceItemId,
                SourceLocator = sourceLocator,
                IngestModuleVersion = ingestModuleVersion
            };
            db.Identifiers.Add(identifier);
            createdIdentifier = true;
        }
        else
        {
            identifier = existing;
            if (identifierIdToReplace.HasValue && identifier.IdentifierId == identifierIdToReplace.Value)
            {
                identifier.Type = typeText;
                identifier.ValueRaw = trimmedRaw;
                identifier.ValueNormalized = normalized;
                identifier.Notes = normalizedNotes;
            }
        }

        var existingLinks = await db.TargetIdentifierLinks
            .Include(l => l.Target)
            .Where(l => l.CaseId == caseId && l.IdentifierId == identifier.IdentifierId)
            .ToListAsync(ct);

        var currentLink = existingLinks.FirstOrDefault(link => link.TargetId == targetId);
        var conflictingLinks = existingLinks
            .Where(link => link.TargetId != targetId)
            .ToList();
        var movedIdentifier = false;
        var usedExistingTarget = false;
        var effectiveTargetId = targetId;
        var previousTargetIds = new List<Guid>();

        TargetIdentifierLinkRecord BuildLink(bool primary)
        {
            return new TargetIdentifierLinkRecord
            {
                LinkId = Guid.NewGuid(),
                CaseId = caseId,
                TargetId = targetId,
                IdentifierId = identifier.IdentifierId,
                IsPrimary = primary,
                CreatedAtUtc = now,
                SourceType = sourceType,
                SourceEvidenceItemId = sourceEvidenceItemId,
                SourceLocator = sourceLocator,
                IngestModuleVersion = ingestModuleVersion
            };
        }

        if (currentLink is null)
        {
            if (conflictingLinks.Count == 0)
            {
                db.TargetIdentifierLinks.Add(BuildLink(isPrimary));
            }
            else
            {
                switch (conflictResolution)
                {
                    case IdentifierConflictResolution.Cancel:
                        throw BuildIdentifierConflict(caseId, identifier, conflictingLinks[0]);
                    case IdentifierConflictResolution.UseExistingTarget:
                        usedExistingTarget = true;
                        effectiveTargetId = conflictingLinks[0].TargetId;
                        break;
                    case IdentifierConflictResolution.KeepExistingAndAlsoLinkToRequestedTarget:
                        db.TargetIdentifierLinks.Add(BuildLink(primary: false));
                        break;
                    case IdentifierConflictResolution.MoveIdentifierToRequestedTarget:
                        foreach (var conflictingLink in conflictingLinks)
                        {
                            previousTargetIds.Add(conflictingLink.TargetId);
                            db.TargetIdentifierLinks.Remove(conflictingLink);
                        }

                        db.TargetIdentifierLinks.Add(BuildLink(isPrimary));
                        movedIdentifier = true;
                        effectiveTargetId = targetId;
                        break;
                    default:
                        throw new InvalidOperationException(
                            $"Unsupported conflict resolution '{conflictResolution}'."
                        );
                }
            }
        }
        else
        {
            currentLink.IsPrimary = isPrimary;
            if (conflictingLinks.Count > 0
                && conflictResolution == IdentifierConflictResolution.MoveIdentifierToRequestedTarget)
            {
                foreach (var conflictingLink in conflictingLinks)
                {
                    previousTargetIds.Add(conflictingLink.TargetId);
                    db.TargetIdentifierLinks.Remove(conflictingLink);
                }

                movedIdentifier = true;
            }
        }

        if (identifierIdToReplace.HasValue && identifierIdToReplace.Value != identifier.IdentifierId)
        {
            var previousLink = await db.TargetIdentifierLinks.FirstOrDefaultAsync(
                l => l.CaseId == caseId
                    && l.TargetId == targetId
                    && l.IdentifierId == identifierIdToReplace.Value,
                ct
            );
            if (previousLink is not null)
            {
                db.TargetIdentifierLinks.Remove(previousLink);
                await WriteAuditAsync(
                    actionType: "IdentifierUnlinkedFromTarget",
                    caseId: caseId,
                    evidenceItemId: sourceEvidenceItemId,
                    summary: "Identifier unlinked during update.",
                    payload: new
                    {
                        targetId,
                        IdentifierId = identifierIdToReplace.Value
                    },
                    ct
                );
            }

            await DeleteIdentifierIfOrphanedAsync(db, caseId, identifierIdToReplace.Value, ct);
        }

        if (createdIdentifier)
        {
            await WriteAuditAsync(
                actionType: "IdentifierCreated",
                caseId: caseId,
                evidenceItemId: sourceEvidenceItemId,
                summary: $"Identifier created: {identifier.Type} {identifier.ValueRaw}",
                payload: new
                {
                    identifier.IdentifierId,
                    identifier.Type,
                    identifier.ValueRaw
                },
                ct
            );
        }
        else if (isUpdateOperation && !usedExistingTarget)
        {
            await WriteAuditAsync(
                actionType: "IdentifierUpdated",
                caseId: caseId,
                evidenceItemId: sourceEvidenceItemId,
                summary: $"Identifier updated: {identifier.Type} {identifier.ValueRaw}",
                payload: new
                {
                    identifier.IdentifierId,
                    identifier.Type,
                    identifier.ValueRaw
                },
                ct
            );
        }

        if (movedIdentifier)
        {
            await WriteAuditAsync(
                actionType: "IdentifierUnlinkedFromTarget",
                caseId: caseId,
                evidenceItemId: sourceEvidenceItemId,
                summary: "Identifier moved away from previous target.",
                payload: new
                {
                    PreviousTargetIds = previousTargetIds,
                    identifier.IdentifierId
                },
                ct
            );
        }

        if (!usedExistingTarget)
        {
            await WriteAuditAsync(
                actionType: "IdentifierLinkedToTarget",
                caseId: caseId,
                evidenceItemId: sourceEvidenceItemId,
                summary: "Identifier linked to target.",
                payload: new
                {
                    TargetId = effectiveTargetId,
                    identifier.IdentifierId,
                    movedIdentifier,
                    createdIdentifier
                },
                ct
            );

            await SyncIdentifierToTargetGlobalPersonAsync(
                db,
                caseId,
                targetId,
                identifier,
                isPrimary,
                globalConflictResolution,
                sourceLocator,
                ct
            );
        }

        return new TargetIdentifierMutationResult(
            MapIdentifier(identifier, isPrimary),
            effectiveTargetId,
            createdIdentifier,
            movedIdentifier,
            usedExistingTarget
        );
    }

    private async Task<TargetRecord> CreateTargetInternalAsync(
        WorkspaceDbContext db,
        Guid caseId,
        string displayName,
        string? primaryAlias,
        string? notes,
        string sourceLocator,
        CancellationToken ct
    )
    {
        var now = _clock.UtcNow.ToUniversalTime();
        var target = new TargetRecord
        {
            TargetId = Guid.NewGuid(),
            CaseId = caseId,
            DisplayName = NormalizeDisplayName(displayName),
            PrimaryAlias = NormalizeOptional(primaryAlias),
            Notes = NormalizeOptional(notes),
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
            SourceType = ManualSourceType,
            SourceEvidenceItemId = null,
            SourceLocator = sourceLocator,
            IngestModuleVersion = ManualModuleVersion
        };
        db.Targets.Add(target);

        await WriteAuditAsync(
            actionType: "TargetCreated",
            caseId: caseId,
            evidenceItemId: null,
            summary: $"Target created: {target.DisplayName}",
            payload: new
            {
                target.TargetId,
                target.DisplayName
            },
            ct
        );

        return target;
    }

    private async Task<TargetGlobalPersonInfo?> BuildTargetGlobalPersonInfoAsync(
        WorkspaceDbContext db,
        Guid caseId,
        Guid? globalEntityId,
        CancellationToken ct
    )
    {
        if (!globalEntityId.HasValue || globalEntityId.Value == Guid.Empty)
        {
            return null;
        }

        var person = await db.PersonEntities
            .AsNoTracking()
            .FirstOrDefaultAsync(record => record.GlobalEntityId == globalEntityId.Value, ct);
        if (person is null)
        {
            return null;
        }

        var aliases = await db.PersonAliases
            .AsNoTracking()
            .Where(record => record.GlobalEntityId == person.GlobalEntityId)
            .OrderBy(record => record.Alias)
            .ToListAsync(ct);

        var identifiers = await db.PersonIdentifiers
            .AsNoTracking()
            .Where(record => record.GlobalEntityId == person.GlobalEntityId)
            .OrderBy(record => record.IsPrimary ? 0 : 1)
            .ThenBy(record => record.Type)
            .ThenBy(record => record.ValueDisplay)
            .ToListAsync(ct);

        var otherCases = await (
            from target in db.Targets.AsNoTracking()
            join caseRecord in db.Cases.AsNoTracking() on target.CaseId equals caseRecord.CaseId
            where target.GlobalEntityId == person.GlobalEntityId
                  && target.CaseId != caseId
            orderby caseRecord.Name, target.DisplayName, target.TargetId
            select new GlobalPersonCaseReference(
                target.CaseId,
                caseRecord.Name,
                target.TargetId,
                target.DisplayName
            )
        )
            .ToListAsync(ct);

        return new TargetGlobalPersonInfo(
            person.GlobalEntityId,
            person.DisplayName,
            aliases.Select(MapGlobalAlias).ToList(),
            identifiers.Select(MapGlobalIdentifier).ToList(),
            otherCases
        );
    }

    private async Task SyncAllTargetIdentifiersToGlobalAsync(
        WorkspaceDbContext db,
        Guid caseId,
        Guid targetId,
        Guid globalEntityId,
        GlobalPersonIdentifierConflictResolution conflictResolution,
        string sourceLocator,
        CancellationToken ct
    )
    {
        var links = await db.TargetIdentifierLinks
            .Include(link => link.Identifier)
            .Where(link => link.CaseId == caseId && link.TargetId == targetId)
            .ToListAsync(ct);

        foreach (var link in links)
        {
            if (link.Identifier is null)
            {
                continue;
            }

            await UpsertGlobalIdentifierAsync(
                db,
                caseId,
                targetId,
                globalEntityId,
                link.Identifier,
                link.IsPrimary,
                conflictResolution,
                sourceLocator,
                ct
            );
        }
    }

    private async Task SyncIdentifierToTargetGlobalPersonAsync(
        WorkspaceDbContext db,
        Guid caseId,
        Guid targetId,
        IdentifierRecord identifier,
        bool isPrimary,
        GlobalPersonIdentifierConflictResolution conflictResolution,
        string sourceLocator,
        CancellationToken ct
    )
    {
        var target = db.Targets.Local.FirstOrDefault(
            item => item.CaseId == caseId && item.TargetId == targetId
        ) ?? await db.Targets.FirstOrDefaultAsync(
            item => item.CaseId == caseId && item.TargetId == targetId,
            ct
        );
        if (target is null || !target.GlobalEntityId.HasValue)
        {
            return;
        }

        await UpsertGlobalIdentifierAsync(
            db,
            caseId,
            targetId,
            target.GlobalEntityId.Value,
            identifier,
            isPrimary,
            conflictResolution,
            sourceLocator,
            ct
        );
    }

    private async Task UpsertGlobalIdentifierAsync(
        WorkspaceDbContext db,
        Guid caseId,
        Guid targetId,
        Guid requestedGlobalEntityId,
        IdentifierRecord identifier,
        bool isPrimary,
        GlobalPersonIdentifierConflictResolution conflictResolution,
        string sourceLocator,
        CancellationToken ct
    )
    {
        var target = db.Targets.Local.FirstOrDefault(
            item => item.CaseId == caseId && item.TargetId == targetId
        ) ?? await db.Targets.FirstOrDefaultAsync(
            item => item.CaseId == caseId && item.TargetId == targetId,
            ct
        );
        if (target is null)
        {
            throw new InvalidOperationException("Target not found.");
        }

        var now = _clock.UtcNow.ToUniversalTime();
        var personIdentifier = await db.PersonIdentifiers.FirstOrDefaultAsync(
            item => item.Type == identifier.Type
                && item.ValueNormalized == identifier.ValueNormalized,
            ct
        );

        Guid effectiveGlobalEntityId = requestedGlobalEntityId;
        Guid? movedFromGlobalEntityId = null;
        var createdIdentifier = false;

        if (personIdentifier is null)
        {
            personIdentifier = new PersonIdentifierRecord
            {
                PersonIdentifierId = Guid.NewGuid(),
                GlobalEntityId = requestedGlobalEntityId,
                Type = identifier.Type,
                ValueNormalized = identifier.ValueNormalized,
                ValueDisplay = identifier.ValueRaw,
                IsPrimary = isPrimary,
                Notes = identifier.Notes,
                CreatedAtUtc = now
            };
            db.PersonIdentifiers.Add(personIdentifier);
            createdIdentifier = true;
        }
        else if (personIdentifier.GlobalEntityId == requestedGlobalEntityId)
        {
            personIdentifier.ValueDisplay = identifier.ValueRaw;
            personIdentifier.Notes = identifier.Notes;
            personIdentifier.IsPrimary = isPrimary || personIdentifier.IsPrimary;
        }
        else
        {
            switch (conflictResolution)
            {
                case GlobalPersonIdentifierConflictResolution.Cancel:
                    throw await BuildGlobalPersonIdentifierConflictAsync(db, personIdentifier, ct);
                case GlobalPersonIdentifierConflictResolution.UseExistingPerson:
                    effectiveGlobalEntityId = personIdentifier.GlobalEntityId;
                    target.GlobalEntityId = effectiveGlobalEntityId;
                    target.UpdatedAtUtc = now;
                    await WriteAuditAsync(
                        actionType: "TargetLinkedToGlobalPerson",
                        caseId: caseId,
                        evidenceItemId: null,
                        summary: "Target linked to existing global person due identifier conflict.",
                        payload: new
                        {
                            targetId,
                            target.GlobalEntityId,
                            identifier.IdentifierId,
                            identifier.ValueRaw,
                            sourceLocator
                        },
                        ct
                    );
                    break;
                case GlobalPersonIdentifierConflictResolution.MoveIdentifierToRequestedPerson:
                    movedFromGlobalEntityId = personIdentifier.GlobalEntityId;
                    personIdentifier.GlobalEntityId = requestedGlobalEntityId;
                    personIdentifier.ValueDisplay = identifier.ValueRaw;
                    personIdentifier.Notes = identifier.Notes;
                    personIdentifier.IsPrimary = isPrimary;
                    await WriteAuditAsync(
                        actionType: "GlobalIdentifierMoved",
                        caseId: caseId,
                        evidenceItemId: null,
                        summary: "Global identifier moved to requested global person.",
                        payload: new
                        {
                            personIdentifier.PersonIdentifierId,
                            identifier.ValueRaw,
                            movedFromGlobalEntityId,
                            requestedGlobalEntityId,
                            sourceLocator
                        },
                        ct
                    );
                    break;
                default:
                    throw new InvalidOperationException(
                        $"Unsupported global conflict resolution '{conflictResolution}'."
                    );
            }
        }

        if (isPrimary)
        {
            var personIdentifiers = await db.PersonIdentifiers
                .Where(item => item.GlobalEntityId == effectiveGlobalEntityId)
                .ToListAsync(ct);
            foreach (var item in personIdentifiers)
            {
                item.IsPrimary = item.PersonIdentifierId == personIdentifier.PersonIdentifierId;
            }
        }

        var person = await db.PersonEntities.FirstOrDefaultAsync(
            item => item.GlobalEntityId == effectiveGlobalEntityId,
            ct
        );
        if (person is not null)
        {
            person.UpdatedAtUtc = now;
        }

        if (movedFromGlobalEntityId.HasValue
            && movedFromGlobalEntityId.Value != effectiveGlobalEntityId)
        {
            var movedFrom = await db.PersonEntities.FirstOrDefaultAsync(
                item => item.GlobalEntityId == movedFromGlobalEntityId.Value,
                ct
            );
            if (movedFrom is not null)
            {
                movedFrom.UpdatedAtUtc = now;
            }
        }

        if (createdIdentifier)
        {
            await WriteAuditAsync(
                actionType: "GlobalIdentifierCreated",
                caseId: caseId,
                evidenceItemId: null,
                summary: "Global identifier created.",
                payload: new
                {
                    personIdentifier.PersonIdentifierId,
                    personIdentifier.GlobalEntityId,
                    personIdentifier.Type,
                    personIdentifier.ValueDisplay,
                    sourceLocator
                },
                ct
            );
        }
    }

    private async Task DeleteIdentifierIfOrphanedAsync(
        WorkspaceDbContext db,
        Guid caseId,
        Guid identifierId,
        CancellationToken ct
    )
    {
        var hasAnyTargetLinks = await db.TargetIdentifierLinks.AnyAsync(
            l => l.CaseId == caseId && l.IdentifierId == identifierId,
            ct
        );
        if (hasAnyTargetLinks)
        {
            return;
        }

        var hasParticipantLinks = await db.MessageParticipantLinks.AnyAsync(
            l => l.CaseId == caseId && l.IdentifierId == identifierId,
            ct
        );
        if (hasParticipantLinks)
        {
            return;
        }

        var identifier = await db.Identifiers.FirstOrDefaultAsync(
            i => i.CaseId == caseId && i.IdentifierId == identifierId,
            ct
        );
        if (identifier is null)
        {
            return;
        }

        db.Identifiers.Remove(identifier);
        await WriteAuditAsync(
            actionType: "IdentifierRemoved",
            caseId: caseId,
            evidenceItemId: null,
            summary: $"Identifier removed: {identifier.Type} {identifier.ValueRaw}",
            payload: new
            {
                identifier.IdentifierId,
                identifier.Type,
                identifier.ValueRaw
            },
            ct
        );
    }

    private IdentifierConflictException BuildIdentifierConflict(
        Guid caseId,
        IdentifierRecord identifier,
        TargetIdentifierLinkRecord currentLink
    )
    {
        var existingTargetName = currentLink.Target?.DisplayName;
        if (string.IsNullOrWhiteSpace(existingTargetName))
        {
            existingTargetName = $"Target {currentLink.TargetId:D}";
        }

        var conflict = new IdentifierConflictInfo(
            caseId,
            identifier.IdentifierId,
            ParseIdentifierType(identifier.Type),
            identifier.ValueRaw,
            identifier.ValueNormalized,
            currentLink.TargetId,
            existingTargetName
        );
        return new IdentifierConflictException(conflict);
    }

    private async Task<GlobalPersonIdentifierConflictException> BuildGlobalPersonIdentifierConflictAsync(
        WorkspaceDbContext db,
        PersonIdentifierRecord identifier,
        CancellationToken ct
    )
    {
        var existingDisplayName = await db.PersonEntities
            .AsNoTracking()
            .Where(person => person.GlobalEntityId == identifier.GlobalEntityId)
            .Select(person => person.DisplayName)
            .FirstOrDefaultAsync(ct);

        if (string.IsNullOrWhiteSpace(existingDisplayName))
        {
            existingDisplayName = $"Global Person {identifier.GlobalEntityId:D}";
        }

        var conflict = new GlobalPersonIdentifierConflictInfo(
            identifier.PersonIdentifierId,
            ParseIdentifierType(identifier.Type),
            identifier.ValueDisplay,
            identifier.ValueNormalized,
            identifier.GlobalEntityId,
            existingDisplayName
        );
        return new GlobalPersonIdentifierConflictException(conflict);
    }

    private Task SaveChangesWithWritePolicyAsync(
        WorkspaceDbContext db,
        string operationName,
        Guid caseId,
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
                ["caseId"] = caseId.ToString("D")
            }
        );
    }

    private async Task WriteAuditAsync(
        string actionType,
        Guid caseId,
        Guid? evidenceItemId,
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
                EvidenceItemId = evidenceItemId,
                Summary = summary,
                JsonPayload = JsonSerializer.Serialize(payload)
            },
            ct
        );
    }

    private static TargetIdentifierType ResolveParticipantIdentifierType(
        TargetIdentifierType? requestedType,
        string participantRaw
    )
    {
        if (requestedType.HasValue)
        {
            return requestedType.Value;
        }

        var inferred = IdentifierNormalizer.InferType(participantRaw);
        if (inferred is TargetIdentifierType.Phone
            or TargetIdentifierType.Email
            or TargetIdentifierType.SocialHandle)
        {
            return inferred;
        }

        throw new ArgumentException(
            "Identifier type could not be inferred. Provide RequestedIdentifierType.",
            nameof(participantRaw)
        );
    }

    private static string ToIdentifierTypeText(TargetIdentifierType type)
    {
        return type.ToString();
    }

    private static TargetIdentifierType ParseIdentifierType(string type)
    {
        return Enum.TryParse<TargetIdentifierType>(type, ignoreCase: true, out var parsed)
            ? parsed
            : TargetIdentifierType.Other;
    }

    private static TargetSummary MapSummary(TargetRecord record, string? globalDisplayName = null)
    {
        return new TargetSummary(
            record.TargetId,
            record.CaseId,
            record.DisplayName,
            record.PrimaryAlias,
            record.Notes,
            record.CreatedAtUtc,
            record.UpdatedAtUtc,
            record.GlobalEntityId,
            globalDisplayName
        );
    }

    private static GlobalPersonSummary MapGlobalSummary(PersonEntityRecord record)
    {
        return new GlobalPersonSummary(
            record.GlobalEntityId,
            record.DisplayName,
            record.CreatedAtUtc,
            record.UpdatedAtUtc
        );
    }

    private static TargetAliasInfo MapAlias(TargetAliasRecord record)
    {
        return new TargetAliasInfo(
            record.AliasId,
            record.TargetId,
            record.CaseId,
            record.Alias,
            record.AliasNormalized
        );
    }

    private static TargetIdentifierInfo MapIdentifier(IdentifierRecord record, bool isPrimary)
    {
        return new TargetIdentifierInfo(
            record.IdentifierId,
            record.CaseId,
            ParseIdentifierType(record.Type),
            record.ValueRaw,
            record.ValueNormalized,
            record.Notes,
            record.CreatedAtUtc,
            isPrimary
        );
    }

    private static GlobalPersonAliasInfo MapGlobalAlias(PersonAliasRecord record)
    {
        return new GlobalPersonAliasInfo(
            record.AliasId,
            record.GlobalEntityId,
            record.Alias,
            record.AliasNormalized,
            record.Notes
        );
    }

    private static GlobalPersonIdentifierInfo MapGlobalIdentifier(PersonIdentifierRecord record)
    {
        return new GlobalPersonIdentifierInfo(
            record.PersonIdentifierId,
            record.GlobalEntityId,
            ParseIdentifierType(record.Type),
            record.ValueDisplay,
            record.ValueNormalized,
            record.IsPrimary,
            record.Notes,
            record.CreatedAtUtc
        );
    }

    private static string NormalizeDisplayName(string? value)
    {
        return value?.Trim() ?? string.Empty;
    }

    private static string? NormalizeOptional(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

    private static string NormalizeAlias(string alias)
    {
        return alias.Trim().ToLowerInvariant();
    }

    private static string NormalizeSourceLocator(string? value, string fallback)
    {
        return string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();
    }
}
