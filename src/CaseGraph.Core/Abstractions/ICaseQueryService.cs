using CaseGraph.Core.Models;

namespace CaseGraph.Core.Abstractions;

public interface ICaseQueryService
{
    Task<IReadOnlyList<CaseInfo>> GetRecentCasesAsync(CancellationToken ct);

    Task<CaseInfo?> GetCaseAsync(Guid caseId, CancellationToken ct);

    Task<IReadOnlyList<EvidenceItem>> GetEvidenceForCaseAsync(Guid caseId, CancellationToken ct);
}
