using CaseGraph.Core.Models;

namespace CaseGraph.Core.Abstractions;

public interface ITargetRegistryService
{
    Task<IReadOnlyList<TargetSummary>> GetTargetsAsync(Guid caseId, string? search, CancellationToken ct);

    Task<TargetDetails?> GetTargetDetailsAsync(Guid caseId, Guid targetId, CancellationToken ct);

    Task<TargetSummary> CreateTargetAsync(CreateTargetRequest request, CancellationToken ct);

    Task<TargetSummary> UpdateTargetAsync(UpdateTargetRequest request, CancellationToken ct);

    Task<TargetAliasInfo> AddAliasAsync(AddTargetAliasRequest request, CancellationToken ct);

    Task RemoveAliasAsync(Guid caseId, Guid aliasId, CancellationToken ct);

    Task<TargetIdentifierMutationResult> AddIdentifierAsync(
        AddTargetIdentifierRequest request,
        CancellationToken ct
    );

    Task<TargetIdentifierMutationResult> UpdateIdentifierAsync(
        UpdateTargetIdentifierRequest request,
        CancellationToken ct
    );

    Task RemoveIdentifierAsync(RemoveTargetIdentifierRequest request, CancellationToken ct);

    Task<MessageParticipantLinkResult> LinkMessageParticipantAsync(
        LinkMessageParticipantRequest request,
        CancellationToken ct
    );
}
