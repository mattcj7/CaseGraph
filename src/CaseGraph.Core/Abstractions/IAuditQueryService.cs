using CaseGraph.Core.Models;

namespace CaseGraph.Core.Abstractions;

public interface IAuditQueryService
{
    Task<IReadOnlyList<AuditEvent>> GetRecentAuditAsync(Guid? caseId, int take, CancellationToken ct);
}
