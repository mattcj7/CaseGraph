using CaseGraph.Core.Models;

namespace CaseGraph.Core.Abstractions;

public interface ICaseWorkspaceService
{
    Task<IReadOnlyList<CaseInfo>> ListCasesAsync(CancellationToken ct);

    Task<CaseInfo> CreateCaseAsync(string name, CancellationToken ct);

    Task<CaseInfo> OpenCaseAsync(Guid caseId, CancellationToken ct);

    Task SaveCaseAsync(CaseInfo caseInfo, IReadOnlyList<EvidenceItem> evidence, CancellationToken ct);

    Task<(CaseInfo caseInfo, List<EvidenceItem> evidence)> LoadCaseAsync(Guid caseId, CancellationToken ct);
}
