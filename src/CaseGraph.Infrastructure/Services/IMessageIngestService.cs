using CaseGraph.Infrastructure.Persistence.Entities;

namespace CaseGraph.Infrastructure.Services;

public interface IMessageIngestService
{
    Task<int> IngestMessagesFromEvidenceAsync(
        Guid caseId,
        EvidenceItemRecord evidence,
        IProgress<double>? progress,
        CancellationToken ct
    );
}
