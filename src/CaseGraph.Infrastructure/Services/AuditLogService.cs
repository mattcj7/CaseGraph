using CaseGraph.Core.Abstractions;
using CaseGraph.Core.Models;
using CaseGraph.Infrastructure.Persistence;
using CaseGraph.Infrastructure.Persistence.Entities;
using Microsoft.EntityFrameworkCore;

namespace CaseGraph.Infrastructure.Services;

public sealed class AuditLogService : IAuditLogService
{
    private readonly IDbContextFactory<WorkspaceDbContext> _dbContextFactory;
    private readonly IWorkspaceDatabaseInitializer _databaseInitializer;
    private readonly IWorkspaceWriteGate _workspaceWriteGate;

    public AuditLogService(
        IDbContextFactory<WorkspaceDbContext> dbContextFactory,
        IWorkspaceDatabaseInitializer databaseInitializer,
        IWorkspaceWriteGate workspaceWriteGate
    )
    {
        _dbContextFactory = dbContextFactory;
        _databaseInitializer = databaseInitializer;
        _workspaceWriteGate = workspaceWriteGate;
    }

    public async Task AddAsync(AuditEvent auditEvent, CancellationToken ct)
    {
        var record = new AuditEventRecord
        {
            AuditEventId = auditEvent.AuditEventId == Guid.Empty ? Guid.NewGuid() : auditEvent.AuditEventId,
            TimestampUtc = auditEvent.TimestampUtc,
            Operator = auditEvent.Operator,
            ActionType = auditEvent.ActionType,
            CaseId = auditEvent.CaseId,
            EvidenceItemId = auditEvent.EvidenceItemId,
            Summary = auditEvent.Summary,
            JsonPayload = auditEvent.JsonPayload
        };

        await _workspaceWriteGate.RunAsync(
            async writeCt =>
            {
                await SqliteWriteRetryPolicy.ExecuteAsync(
                    async retryCt =>
                    {
                        await _databaseInitializer.EnsureInitializedAsync(retryCt);
                        await using var db = await _dbContextFactory.CreateDbContextAsync(retryCt);
                        db.AuditEvents.Add(record);
                        await db.SaveChangesAsync(retryCt);
                    },
                    writeCt
                );
            },
            ct
        );
    }

    public async Task<IReadOnlyList<AuditEvent>> GetRecentAsync(Guid? caseId, int take, CancellationToken ct)
    {
        await _databaseInitializer.EnsureInitializedAsync(ct);

        var boundedTake = take <= 0 ? 20 : take;

        await using var db = await _dbContextFactory.CreateDbContextAsync(ct);
        var query = db.AuditEvents.AsNoTracking().AsQueryable();
        if (caseId.HasValue)
        {
            query = query.Where(a => a.CaseId == caseId.Value);
        }

        var records = await query
            .ToListAsync(ct);

        return records
            .OrderByDescending(r => r.TimestampUtc)
            .Take(boundedTake)
            .Select(r => new AuditEvent
            {
                AuditEventId = r.AuditEventId,
                TimestampUtc = r.TimestampUtc,
                Operator = r.Operator,
                ActionType = r.ActionType,
                CaseId = r.CaseId,
                EvidenceItemId = r.EvidenceItemId,
                Summary = r.Summary,
                JsonPayload = r.JsonPayload
            })
            .ToList();
    }
}
