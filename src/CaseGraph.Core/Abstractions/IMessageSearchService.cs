using CaseGraph.Core.Models;

namespace CaseGraph.Core.Abstractions;

public interface IMessageSearchService
{
    Task<IReadOnlyList<MessageSearchHit>> SearchAsync(
        Guid caseId,
        string query,
        string? platformFilter,
        string? senderFilter,
        string? recipientFilter,
        int take,
        int skip,
        CancellationToken ct
    );

    Task<IReadOnlyList<MessageSearchHit>> SearchAsync(
        MessageSearchRequest request,
        CancellationToken ct
    );

    Task<TargetPresenceSummary?> GetTargetPresenceSummaryAsync(
        Guid caseId,
        Guid targetId,
        TargetIdentifierType? identifierTypeFilter,
        DateTimeOffset? fromUtc,
        DateTimeOffset? toUtc,
        CancellationToken ct
    );
}
