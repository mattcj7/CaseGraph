using CaseGraph.Core.Models;

namespace CaseGraph.Core.Abstractions;

public interface IMessageSearchService
{
    Task<IReadOnlyList<MessageSearchHit>> SearchAsync(
        Guid caseId,
        string query,
        int take,
        int skip,
        CancellationToken ct
    );
}
