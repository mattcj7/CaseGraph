using CaseGraph.Core.Models;

namespace CaseGraph.Core.Abstractions;

public interface IAuditLogService
{
    Task AddAsync(AuditEvent auditEvent, CancellationToken ct);

    Task<IReadOnlyList<AuditEvent>> GetRecentAsync(Guid? caseId, int take, CancellationToken ct);
}
