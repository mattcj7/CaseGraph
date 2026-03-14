using CaseGraph.Core.Abstractions;
using CaseGraph.Core.Diagnostics;
using CaseGraph.Core.Models;
using CaseGraph.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using System.Text.Json;

namespace CaseGraph.Infrastructure.Organizations;

public sealed class OrganizationService : IOrganizationService
{
    private readonly IDbContextFactory<WorkspaceDbContext> _dbContextFactory;
    private readonly IWorkspaceDatabaseInitializer _databaseInitializer;
    private readonly IWorkspaceWriteGate _workspaceWriteGate;
    private readonly IAuditLogService _auditLogService;
    private readonly IClock _clock;

    public OrganizationService(
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

    public async Task<IReadOnlyList<OrganizationSummaryDto>> GetOrganizationsAsync(
        string? search,
        CancellationToken ct
    )
    {
        await _databaseInitializer.EnsureInitializedAsync(ct);
        await using var db = await _dbContextFactory.CreateDbContextAsync(ct);

        var organizations = await db.Organizations.AsNoTracking().ToListAsync(ct);
        var aliases = await db.OrganizationAliases.AsNoTracking().ToListAsync(ct);
        var membershipCounts = await db.OrganizationMemberships
            .AsNoTracking()
            .GroupBy(membership => membership.OrganizationId)
            .Select(group => new { group.Key, Count = group.Count() })
            .ToDictionaryAsync(item => item.Key, item => item.Count, ct);
        var childCounts = organizations
            .Where(record => record.ParentOrganizationId.HasValue)
            .GroupBy(record => record.ParentOrganizationId!.Value)
            .ToDictionary(group => group.Key, group => group.Count());
        var parentNames = organizations.ToDictionary(record => record.OrganizationId, record => record.Name);

        var normalizedSearch = NormalizeSearch(search);
        var filtered = organizations.Where(record =>
            normalizedSearch.Length == 0
            || record.NameNormalized.Contains(normalizedSearch, StringComparison.Ordinal)
            || aliases.Any(alias =>
                alias.OrganizationId == record.OrganizationId
                && alias.AliasNormalized.Contains(normalizedSearch, StringComparison.Ordinal)
            )
        );

        return filtered
            .OrderBy(record => record.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(record => record.OrganizationId)
            .Select(record => MapSummary(
                record,
                aliases.Count(alias => alias.OrganizationId == record.OrganizationId),
                membershipCounts.GetValueOrDefault(record.OrganizationId),
                childCounts.GetValueOrDefault(record.OrganizationId),
                record.ParentOrganizationId.HasValue
                    ? parentNames.GetValueOrDefault(record.ParentOrganizationId.Value)
                    : null
            ))
            .ToList();
    }

    public async Task<OrganizationDetailsDto?> GetOrganizationDetailsAsync(
        Guid organizationId,
        CancellationToken ct
    )
    {
        await _databaseInitializer.EnsureInitializedAsync(ct);
        await using var db = await _dbContextFactory.CreateDbContextAsync(ct);

        var organization = await db.Organizations
            .AsNoTracking()
            .FirstOrDefaultAsync(record => record.OrganizationId == organizationId, ct);
        if (organization is null)
        {
            return null;
        }

        var aliases = await db.OrganizationAliases
            .AsNoTracking()
            .Where(alias => alias.OrganizationId == organizationId)
            .OrderBy(alias => alias.Alias)
            .ToListAsync(ct);
        var memberships = await db.OrganizationMemberships
            .AsNoTracking()
            .Where(membership => membership.OrganizationId == organizationId)
            .ToListAsync(ct);
        var membershipGlobalIds = memberships.Select(membership => membership.GlobalEntityId).Distinct().ToList();
        var people = membershipGlobalIds.Count == 0
            ? new Dictionary<Guid, string>()
            : await db.PersonEntities
                .AsNoTracking()
                .Where(person => membershipGlobalIds.Contains(person.GlobalEntityId))
                .ToDictionaryAsync(person => person.GlobalEntityId, person => person.DisplayName, ct);
        var children = await db.Organizations
            .AsNoTracking()
            .Where(record => record.ParentOrganizationId == organizationId)
            .OrderBy(record => record.Name)
            .ToListAsync(ct);
        var parentName = organization.ParentOrganizationId.HasValue
            ? await db.Organizations
                .AsNoTracking()
                .Where(record => record.OrganizationId == organization.ParentOrganizationId.Value)
                .Select(record => record.Name)
                .FirstOrDefaultAsync(ct)
            : null;
        var childIds = children.Select(child => child.OrganizationId).ToList();
        var childAliasCounts = childIds.Count == 0
            ? new Dictionary<Guid, int>()
            : await db.OrganizationAliases
                .AsNoTracking()
                .Where(alias => childIds.Contains(alias.OrganizationId))
                .GroupBy(alias => alias.OrganizationId)
                .Select(group => new { group.Key, Count = group.Count() })
                .ToDictionaryAsync(item => item.Key, item => item.Count, ct);
        var childMembershipCounts = childIds.Count == 0
            ? new Dictionary<Guid, int>()
            : await db.OrganizationMemberships
                .AsNoTracking()
                .Where(membership => childIds.Contains(membership.OrganizationId))
                .GroupBy(membership => membership.OrganizationId)
                .Select(group => new { group.Key, Count = group.Count() })
                .ToDictionaryAsync(item => item.Key, item => item.Count, ct);

        var summary = MapSummary(organization, aliases.Count, memberships.Count, children.Count, parentName);

        return new OrganizationDetailsDto(
            summary,
            aliases.Select(MapAlias).ToList(),
            memberships
                .OrderBy(
                    membership => people.GetValueOrDefault(membership.GlobalEntityId),
                    StringComparer.OrdinalIgnoreCase
                )
                .ThenBy(membership => membership.CreatedAtUtc)
                .Select(membership => MapMembership(
                    membership,
                    people.GetValueOrDefault(membership.GlobalEntityId)
                        ?? $"Global Person {membership.GlobalEntityId:D}"
                ))
                .ToList(),
            children.Select(child => MapSummary(
                child,
                childAliasCounts.GetValueOrDefault(child.OrganizationId),
                childMembershipCounts.GetValueOrDefault(child.OrganizationId),
                childCount: 0,
                parentOrganizationName: organization.Name
            )).ToList()
        );
    }

    public async Task<OrganizationSummaryDto> CreateOrganizationAsync(
        CreateOrganizationRequest request,
        CancellationToken ct
    )
    {
        var normalizedName = NormalizeRequired(request.Name, "Organization name is required.");
        var type = NormalizeOrganizationType(request.Type);
        var status = NormalizeOrganizationStatus(request.Status);

        await _databaseInitializer.EnsureInitializedAsync(ct);
        await using var db = await _dbContextFactory.CreateDbContextAsync(ct);

        if (request.ParentOrganizationId.HasValue)
        {
            var parentExists = await db.Organizations.AnyAsync(
                record => record.OrganizationId == request.ParentOrganizationId.Value,
                ct
            );
            if (!parentExists)
            {
                throw new InvalidOperationException("Parent organization not found.");
            }
        }

        var now = _clock.UtcNow.ToUniversalTime();
        var organization = new OrganizationRecord
        {
            OrganizationId = Guid.NewGuid(),
            Name = normalizedName,
            NameNormalized = NormalizeSearch(normalizedName),
            Type = type,
            Status = status,
            ParentOrganizationId = request.ParentOrganizationId,
            Summary = NormalizeOptional(request.Summary),
            CreatedAtUtc = now,
            UpdatedAtUtc = now
        };

        db.Organizations.Add(organization);
        await SaveChangesWithWritePolicyAsync(
            db,
            "OrganizationRegistry.CreateOrganization",
            organization.OrganizationId,
            ct
        );

        LogDiagnostic(
            "OrganizationCreated",
            $"Organization created: {organization.Name}",
            organization.OrganizationId,
            new Dictionary<string, object?>
            {
                ["type"] = organization.Type,
                ["status"] = organization.Status,
                ["parentOrganizationId"] = organization.ParentOrganizationId?.ToString("D")
            }
        );

        await WriteAuditAsync(
            "OrganizationCreated",
            $"Organization created: {organization.Name}",
            new
            {
                organization.OrganizationId,
                organization.Name,
                organization.Type,
                organization.Status,
                organization.ParentOrganizationId,
                organization.Summary
            },
            ct
        );

        var parentName = request.ParentOrganizationId.HasValue
            ? await db.Organizations
                .AsNoTracking()
                .Where(record => record.OrganizationId == request.ParentOrganizationId.Value)
                .Select(record => record.Name)
                .FirstOrDefaultAsync(ct)
            : null;

        return MapSummary(organization, 0, 0, 0, parentName);
    }

    public async Task<OrganizationSummaryDto> UpdateOrganizationAsync(
        UpdateOrganizationRequest request,
        CancellationToken ct
    )
    {
        var normalizedName = NormalizeRequired(request.Name, "Organization name is required.");
        var type = NormalizeOrganizationType(request.Type);
        var status = NormalizeOrganizationStatus(request.Status);

        await _databaseInitializer.EnsureInitializedAsync(ct);
        await using var db = await _dbContextFactory.CreateDbContextAsync(ct);

        var organization = await db.Organizations.FirstOrDefaultAsync(
            record => record.OrganizationId == request.OrganizationId,
            ct
        );
        if (organization is null)
        {
            throw new InvalidOperationException("Organization not found.");
        }

        if (request.ParentOrganizationId == request.OrganizationId)
        {
            throw new InvalidOperationException("An organization cannot parent itself.");
        }

        if (request.ParentOrganizationId.HasValue)
        {
            var parent = await db.Organizations
                .AsNoTracking()
                .FirstOrDefaultAsync(
                    record => record.OrganizationId == request.ParentOrganizationId.Value,
                    ct
                );
            if (parent is null)
            {
                throw new InvalidOperationException("Parent organization not found.");
            }

            await EnsureNotDescendantAsync(db, organization.OrganizationId, parent.OrganizationId, ct);
        }

        organization.Name = normalizedName;
        organization.NameNormalized = NormalizeSearch(normalizedName);
        organization.Type = type;
        organization.Status = status;
        organization.ParentOrganizationId = request.ParentOrganizationId;
        organization.Summary = NormalizeOptional(request.Summary);
        organization.UpdatedAtUtc = _clock.UtcNow.ToUniversalTime();

        await SaveChangesWithWritePolicyAsync(
            db,
            "OrganizationRegistry.UpdateOrganization",
            organization.OrganizationId,
            ct
        );

        LogDiagnostic(
            "OrganizationUpdated",
            $"Organization updated: {organization.Name}",
            organization.OrganizationId,
            new Dictionary<string, object?>
            {
                ["type"] = organization.Type,
                ["status"] = organization.Status,
                ["parentOrganizationId"] = organization.ParentOrganizationId?.ToString("D")
            }
        );

        await WriteAuditAsync(
            "OrganizationUpdated",
            $"Organization updated: {organization.Name}",
            new
            {
                organization.OrganizationId,
                organization.Name,
                organization.Type,
                organization.Status,
                organization.ParentOrganizationId,
                organization.Summary
            },
            ct
        );

        var aliasCount = await db.OrganizationAliases.CountAsync(
            alias => alias.OrganizationId == organization.OrganizationId,
            ct
        );
        var membershipCount = await db.OrganizationMemberships.CountAsync(
            membership => membership.OrganizationId == organization.OrganizationId,
            ct
        );
        var childCount = await db.Organizations.CountAsync(
            record => record.ParentOrganizationId == organization.OrganizationId,
            ct
        );
        var parentName = request.ParentOrganizationId.HasValue
            ? await db.Organizations
                .AsNoTracking()
                .Where(record => record.OrganizationId == request.ParentOrganizationId.Value)
                .Select(record => record.Name)
                .FirstOrDefaultAsync(ct)
            : null;

        return MapSummary(organization, aliasCount, membershipCount, childCount, parentName);
    }

    public async Task<OrganizationAliasDto> AddAliasAsync(
        AddOrganizationAliasRequest request,
        CancellationToken ct
    )
    {
        var alias = NormalizeRequired(request.Alias, "Organization alias is required.");
        var aliasNormalized = NormalizeSearch(alias);

        await _databaseInitializer.EnsureInitializedAsync(ct);
        await using var db = await _dbContextFactory.CreateDbContextAsync(ct);

        var organization = await db.Organizations.FirstOrDefaultAsync(
            record => record.OrganizationId == request.OrganizationId,
            ct
        );
        if (organization is null)
        {
            throw new InvalidOperationException("Organization not found.");
        }

        var existing = await db.OrganizationAliases.FirstOrDefaultAsync(
            record =>
                record.OrganizationId == request.OrganizationId
                && record.AliasNormalized == aliasNormalized,
            ct
        );
        if (existing is not null)
        {
            return MapAlias(existing);
        }

        var aliasRecord = new OrganizationAlias
        {
            AliasId = Guid.NewGuid(),
            OrganizationId = request.OrganizationId,
            Alias = alias,
            AliasNormalized = aliasNormalized,
            Notes = NormalizeOptional(request.Notes),
            CreatedAtUtc = _clock.UtcNow.ToUniversalTime()
        };

        db.OrganizationAliases.Add(aliasRecord);
        organization.UpdatedAtUtc = _clock.UtcNow.ToUniversalTime();
        await SaveChangesWithWritePolicyAsync(
            db,
            "OrganizationRegistry.AddAlias",
            request.OrganizationId,
            ct
        );

        LogDiagnostic(
            "OrganizationAliasAdded",
            $"Organization alias added: {alias}",
            request.OrganizationId,
            new Dictionary<string, object?>
            {
                ["aliasId"] = aliasRecord.AliasId.ToString("D"),
                ["alias"] = aliasRecord.Alias
            }
        );

        await WriteAuditAsync(
            "OrganizationAliasAdded",
            $"Organization alias added: {alias}",
            new
            {
                request.OrganizationId,
                aliasRecord.AliasId,
                aliasRecord.Alias,
                aliasRecord.Notes
            },
            ct
        );

        return MapAlias(aliasRecord);
    }

    public async Task RemoveAliasAsync(Guid aliasId, CancellationToken ct)
    {
        await _databaseInitializer.EnsureInitializedAsync(ct);
        await using var db = await _dbContextFactory.CreateDbContextAsync(ct);

        var alias = await db.OrganizationAliases
            .Include(record => record.Organization)
            .FirstOrDefaultAsync(record => record.AliasId == aliasId, ct);
        if (alias is null)
        {
            return;
        }

        var organizationId = alias.OrganizationId;
        var aliasValue = alias.Alias;
        if (alias.Organization is not null)
        {
            alias.Organization.UpdatedAtUtc = _clock.UtcNow.ToUniversalTime();
        }

        db.OrganizationAliases.Remove(alias);
        await SaveChangesWithWritePolicyAsync(
            db,
            "OrganizationRegistry.RemoveAlias",
            organizationId,
            ct
        );

        LogDiagnostic(
            "OrganizationAliasRemoved",
            $"Organization alias removed: {aliasValue}",
            organizationId,
            new Dictionary<string, object?>
            {
                ["aliasId"] = aliasId.ToString("D"),
                ["alias"] = aliasValue
            }
        );

        await WriteAuditAsync(
            "OrganizationAliasRemoved",
            $"Organization alias removed: {aliasValue}",
            new { organizationId, aliasId, Alias = aliasValue },
            ct
        );
    }

    public async Task<OrganizationMembershipDto> AddMembershipAsync(
        AddOrganizationMembershipRequest request,
        CancellationToken ct
    )
    {
        await _databaseInitializer.EnsureInitializedAsync(ct);
        await using var db = await _dbContextFactory.CreateDbContextAsync(ct);

        var organization = await db.Organizations.FirstOrDefaultAsync(
            record => record.OrganizationId == request.OrganizationId,
            ct
        );
        if (organization is null)
        {
            throw new InvalidOperationException("Organization not found.");
        }

        var globalPerson = await db.PersonEntities
            .AsNoTracking()
            .FirstOrDefaultAsync(person => person.GlobalEntityId == request.GlobalEntityId, ct);
        if (globalPerson is null)
        {
            throw new InvalidOperationException("Global person not found.");
        }

        var existing = await db.OrganizationMemberships.FirstOrDefaultAsync(
            record =>
                record.OrganizationId == request.OrganizationId
                && record.GlobalEntityId == request.GlobalEntityId,
            ct
        );
        if (existing is not null)
        {
            throw new InvalidOperationException("Membership already exists for that global person.");
        }

        var timestamp = _clock.UtcNow.ToUniversalTime();
        var membership = new OrganizationMembership
        {
            MembershipId = Guid.NewGuid(),
            OrganizationId = request.OrganizationId,
            GlobalEntityId = request.GlobalEntityId,
            Role = NormalizeOptional(request.Role),
            Status = NormalizeMembershipStatus(request.Status),
            Confidence = NormalizeConfidence(request.Confidence),
            BasisSummary = NormalizeOptional(request.BasisSummary),
            StartDateUtc = NormalizeDate(request.StartDateUtc),
            EndDateUtc = NormalizeDate(request.EndDateUtc),
            LastConfirmedDateUtc = NormalizeDate(request.LastConfirmedDateUtc),
            Reviewer = NormalizeOptional(request.Reviewer),
            ReviewNotes = NormalizeOptional(request.ReviewNotes),
            CreatedAtUtc = timestamp,
            UpdatedAtUtc = timestamp
        };

        ValidateDateRange(membership.StartDateUtc, membership.EndDateUtc);

        db.OrganizationMemberships.Add(membership);
        organization.UpdatedAtUtc = timestamp;
        await SaveChangesWithWritePolicyAsync(
            db,
            "OrganizationRegistry.AddMembership",
            request.OrganizationId,
            ct
        );

        LogDiagnostic(
            "OrganizationMembershipCreated",
            "Organization membership created.",
            request.OrganizationId,
            new Dictionary<string, object?>
            {
                ["membershipId"] = membership.MembershipId.ToString("D"),
                ["globalEntityId"] = membership.GlobalEntityId.ToString("D"),
                ["status"] = membership.Status,
                ["confidence"] = membership.Confidence
            }
        );

        await WriteAuditAsync(
            "OrganizationMembershipCreated",
            $"Membership added for {globalPerson.DisplayName}.",
            new
            {
                request.OrganizationId,
                membership.MembershipId,
                membership.GlobalEntityId,
                globalPerson.DisplayName,
                membership.Role,
                membership.Status,
                membership.Confidence,
                membership.BasisSummary,
                membership.StartDateUtc,
                membership.EndDateUtc,
                membership.LastConfirmedDateUtc,
                membership.Reviewer,
                membership.ReviewNotes
            },
            ct
        );

        return MapMembership(membership, globalPerson.DisplayName);
    }

    public async Task<OrganizationMembershipDto> UpdateMembershipAsync(
        UpdateOrganizationMembershipRequest request,
        CancellationToken ct
    )
    {
        await _databaseInitializer.EnsureInitializedAsync(ct);
        await using var db = await _dbContextFactory.CreateDbContextAsync(ct);

        var membership = await db.OrganizationMemberships.FirstOrDefaultAsync(
            record => record.MembershipId == request.MembershipId,
            ct
        );
        if (membership is null)
        {
            throw new InvalidOperationException("Membership not found.");
        }

        membership.Role = NormalizeOptional(request.Role);
        membership.Status = NormalizeMembershipStatus(request.Status);
        membership.Confidence = NormalizeConfidence(request.Confidence);
        membership.BasisSummary = NormalizeOptional(request.BasisSummary);
        membership.StartDateUtc = NormalizeDate(request.StartDateUtc);
        membership.EndDateUtc = NormalizeDate(request.EndDateUtc);
        membership.LastConfirmedDateUtc = NormalizeDate(request.LastConfirmedDateUtc);
        membership.Reviewer = NormalizeOptional(request.Reviewer);
        membership.ReviewNotes = NormalizeOptional(request.ReviewNotes);
        membership.UpdatedAtUtc = _clock.UtcNow.ToUniversalTime();

        ValidateDateRange(membership.StartDateUtc, membership.EndDateUtc);

        var organization = await db.Organizations.FirstOrDefaultAsync(
            record => record.OrganizationId == membership.OrganizationId,
            ct
        );
        if (organization is not null)
        {
            organization.UpdatedAtUtc = membership.UpdatedAtUtc;
        }

        await SaveChangesWithWritePolicyAsync(
            db,
            "OrganizationRegistry.UpdateMembership",
            membership.OrganizationId,
            ct
        );

        var globalDisplayName = await db.PersonEntities
            .AsNoTracking()
            .Where(person => person.GlobalEntityId == membership.GlobalEntityId)
            .Select(person => person.DisplayName)
            .FirstOrDefaultAsync(ct)
            ?? $"Global Person {membership.GlobalEntityId:D}";

        LogDiagnostic(
            "OrganizationMembershipUpdated",
            "Organization membership updated.",
            membership.OrganizationId,
            new Dictionary<string, object?>
            {
                ["membershipId"] = membership.MembershipId.ToString("D"),
                ["globalEntityId"] = membership.GlobalEntityId.ToString("D"),
                ["status"] = membership.Status,
                ["confidence"] = membership.Confidence
            }
        );

        await WriteAuditAsync(
            "OrganizationMembershipUpdated",
            $"Membership updated for {globalDisplayName}.",
            new
            {
                membership.OrganizationId,
                membership.MembershipId,
                membership.GlobalEntityId,
                globalDisplayName,
                membership.Role,
                membership.Status,
                membership.Confidence,
                membership.BasisSummary,
                membership.StartDateUtc,
                membership.EndDateUtc,
                membership.LastConfirmedDateUtc,
                membership.Reviewer,
                membership.ReviewNotes
            },
            ct
        );

        return MapMembership(membership, globalDisplayName);
    }

    public async Task RemoveMembershipAsync(Guid membershipId, CancellationToken ct)
    {
        await _databaseInitializer.EnsureInitializedAsync(ct);
        await using var db = await _dbContextFactory.CreateDbContextAsync(ct);

        var membership = await db.OrganizationMemberships.FirstOrDefaultAsync(
            record => record.MembershipId == membershipId,
            ct
        );
        if (membership is null)
        {
            return;
        }

        var organizationId = membership.OrganizationId;
        var globalEntityId = membership.GlobalEntityId;
        var globalDisplayName = await db.PersonEntities
            .AsNoTracking()
            .Where(person => person.GlobalEntityId == membership.GlobalEntityId)
            .Select(person => person.DisplayName)
            .FirstOrDefaultAsync(ct)
            ?? $"Global Person {membership.GlobalEntityId:D}";

        db.OrganizationMemberships.Remove(membership);
        var organization = await db.Organizations.FirstOrDefaultAsync(
            record => record.OrganizationId == organizationId,
            ct
        );
        if (organization is not null)
        {
            organization.UpdatedAtUtc = _clock.UtcNow.ToUniversalTime();
        }

        await SaveChangesWithWritePolicyAsync(
            db,
            "OrganizationRegistry.RemoveMembership",
            organizationId,
            ct
        );

        LogDiagnostic(
            "OrganizationMembershipRemoved",
            "Organization membership removed.",
            organizationId,
            new Dictionary<string, object?>
            {
                ["membershipId"] = membershipId.ToString("D"),
                ["globalEntityId"] = globalEntityId.ToString("D")
            }
        );

        await WriteAuditAsync(
            "OrganizationMembershipRemoved",
            $"Membership removed for {globalDisplayName}.",
            new { organizationId, membershipId, globalEntityId, globalDisplayName },
            ct
        );
    }

    private static OrganizationSummaryDto MapSummary(
        OrganizationRecord record,
        int aliasCount,
        int membershipCount,
        int childCount,
        string? parentOrganizationName
    )
    {
        return new OrganizationSummaryDto(
            record.OrganizationId,
            record.Name,
            record.Type,
            record.Status,
            record.Summary,
            record.ParentOrganizationId,
            parentOrganizationName,
            aliasCount,
            membershipCount,
            childCount,
            record.CreatedAtUtc,
            record.UpdatedAtUtc
        );
    }

    private static OrganizationAliasDto MapAlias(OrganizationAlias alias)
    {
        return new OrganizationAliasDto(
            alias.AliasId,
            alias.OrganizationId,
            alias.Alias,
            alias.Notes,
            alias.CreatedAtUtc
        );
    }

    private static OrganizationMembershipDto MapMembership(
        OrganizationMembership membership,
        string globalDisplayName
    )
    {
        return new OrganizationMembershipDto(
            membership.MembershipId,
            membership.OrganizationId,
            membership.GlobalEntityId,
            globalDisplayName,
            membership.Role,
            membership.Status,
            membership.Confidence,
            membership.BasisSummary,
            membership.StartDateUtc,
            membership.EndDateUtc,
            membership.LastConfirmedDateUtc,
            membership.Reviewer,
            membership.ReviewNotes,
            membership.CreatedAtUtc,
            membership.UpdatedAtUtc
        );
    }

    private async Task EnsureNotDescendantAsync(
        WorkspaceDbContext db,
        Guid organizationId,
        Guid candidateParentOrganizationId,
        CancellationToken ct
    )
    {
        var parentById = await db.Organizations
            .AsNoTracking()
            .ToDictionaryAsync(record => record.OrganizationId, ct);
        var currentParentId = candidateParentOrganizationId;

        while (parentById.TryGetValue(currentParentId, out var parent))
        {
            if (parent.OrganizationId == organizationId)
            {
                throw new InvalidOperationException("Parent organization cannot create a cycle.");
            }

            if (!parent.ParentOrganizationId.HasValue)
            {
                return;
            }

            currentParentId = parent.ParentOrganizationId.Value;
        }
    }

    private Task SaveChangesWithWritePolicyAsync(
        WorkspaceDbContext db,
        string operationName,
        Guid organizationId,
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
                ["organizationId"] = organizationId.ToString("D")
            }
        );
    }

    private async Task WriteAuditAsync(
        string actionType,
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
                Summary = summary,
                JsonPayload = JsonSerializer.Serialize(payload)
            },
            ct
        );
    }

    private static void LogDiagnostic(
        string eventName,
        string message,
        Guid organizationId,
        IReadOnlyDictionary<string, object?> fields
    )
    {
        var eventFields = new Dictionary<string, object?>(fields)
        {
            ["organizationId"] = organizationId.ToString("D"),
            ["correlationId"] = AppFileLogger.GetScopeValue("correlationId")
        };

        AppFileLogger.LogEvent(
            eventName: eventName,
            level: "INFO",
            message: message,
            fields: eventFields
        );
    }

    private static string NormalizeRequired(string value, string errorMessage)
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

    private static string NormalizeSearch(string? value)
    {
        return NormalizeOptional(value)?.ToLowerInvariant() ?? string.Empty;
    }

    private static string NormalizeOrganizationType(string value)
    {
        var normalized = NormalizeRequired(value, "Organization type is required.").ToLowerInvariant();
        if (!OrganizationRegistryCatalog.OrganizationTypes.Contains(normalized, StringComparer.Ordinal))
        {
            throw new ArgumentException("Organization type is not supported.", nameof(value));
        }

        return normalized;
    }

    private static string NormalizeOrganizationStatus(string value)
    {
        var normalized = NormalizeRequired(value, "Organization status is required.").ToLowerInvariant();
        if (!OrganizationRegistryCatalog.OrganizationStatuses.Contains(normalized, StringComparer.Ordinal))
        {
            throw new ArgumentException("Organization status is not supported.", nameof(value));
        }

        return normalized;
    }

    private static string NormalizeMembershipStatus(string value)
    {
        var normalized = NormalizeRequired(value, "Membership status is required.").ToLowerInvariant();
        if (!OrganizationRegistryCatalog.MembershipStatuses.Contains(normalized, StringComparer.Ordinal))
        {
            throw new ArgumentException("Membership status is not supported.", nameof(value));
        }

        return normalized;
    }

    private static int NormalizeConfidence(int value)
    {
        if (value < 0 || value > 100)
        {
            throw new ArgumentOutOfRangeException(nameof(value), "Confidence must be between 0 and 100.");
        }

        return value;
    }

    private static DateTimeOffset? NormalizeDate(DateTimeOffset? value)
    {
        return value?.ToUniversalTime();
    }

    private static void ValidateDateRange(DateTimeOffset? startDateUtc, DateTimeOffset? endDateUtc)
    {
        if (startDateUtc.HasValue && endDateUtc.HasValue && endDateUtc.Value < startDateUtc.Value)
        {
            throw new ArgumentException("End date cannot be earlier than start date.");
        }
    }
}
