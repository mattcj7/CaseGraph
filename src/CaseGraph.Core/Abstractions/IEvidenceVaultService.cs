using CaseGraph.Core.Models;

namespace CaseGraph.Core.Abstractions;

public interface IEvidenceVaultService
{
    Task<EvidenceItem> ImportEvidenceFileAsync(
        CaseInfo caseInfo,
        string filePath,
        IProgress<double>? progress,
        CancellationToken ct
    );

    Task<(bool ok, string message)> VerifyEvidenceAsync(
        CaseInfo caseInfo,
        EvidenceItem item,
        IProgress<double>? progress,
        CancellationToken ct
    );
}
