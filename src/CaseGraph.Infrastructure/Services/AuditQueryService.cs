using CaseGraph.Core.Abstractions;
using CaseGraph.Core.Models;
using CaseGraph.Infrastructure.Persistence;
using CaseGraph.Infrastructure.Persistence.Entities;
using Microsoft.EntityFrameworkCore;

namespace CaseGraph.Infrastructure.Services;

public sealed class AuditQueryService : IAuditQueryService
{
    private readonly IDbContextFactory<WorkspaceDbContext> _dbContextFactory;
    private readonly IWorkspaceDatabaseInitializer _databaseInitializer;

    public AuditQueryService(
        IDbContextFactory<WorkspaceDbContext> dbContextFactory,
        IWorkspaceDatabaseInitializer databaseInitializer
    )
    {
        _dbContextFactory = dbContextFactory;
        _databaseInitializer = databaseInitializer;
    }

    public async Task<IReadOnlyList<AuditEvent>> GetRecentAuditAsync(
        Guid? caseId,
        int take,
        CancellationToken ct
    )
    {
        await _databaseInitializer.EnsureInitializedAsync(ct);

        var boundedTake = take <= 0 ? 20 : take;

        await using var db = await _dbContextFactory.CreateDbContextAsync(ct);
        var query = db.AuditOrderKeys.AsNoTracking().AsQueryable();
        if (caseId.HasValue)
        {
            query = query.Where(record => record.CaseId == caseId.Value);
        }

        var orderedAuditIds = await query
            .OrderByDescending(
                record => EF.Property<string>(record, nameof(AuditOrderKeyRecord.TimestampUtc))
            )
            .ThenByDescending(record => record.AuditEventId)
            .Take(boundedTake)
            .Select(record => record.AuditEventId)
            .ToListAsync(ct);

        if (orderedAuditIds.Count == 0)
        {
            return Array.Empty<AuditEvent>();
        }

        var records = await db.AuditEvents
            .AsNoTracking()
            .Where(record => orderedAuditIds.Contains(record.AuditEventId))
            .ToListAsync(ct);
        var recordsById = records.ToDictionary(record => record.AuditEventId);

        var orderedAudit = new List<AuditEvent>(orderedAuditIds.Count);
        foreach (var auditEventId in orderedAuditIds)
        {
            if (recordsById.TryGetValue(auditEventId, out var record))
            {
                orderedAudit.Add(MapAuditEvent(record));
            }
        }

        return orderedAudit;
    }

    private static AuditEvent MapAuditEvent(AuditEventRecord record)
    {
        return new AuditEvent
        {
            AuditEventId = record.AuditEventId,
            TimestampUtc = record.TimestampUtc,
            Operator = record.Operator,
            ActionType = record.ActionType,
            CaseId = record.CaseId,
            EvidenceItemId = record.EvidenceItemId,
            Summary = record.Summary,
            JsonPayload = record.JsonPayload
        };
    }
}
