namespace CaseGraph.Core.Abstractions;

public interface ITargetMessagePresenceIndexService
{
    Task RebuildCaseAsync(Guid caseId, CancellationToken ct);

    Task RefreshForEvidenceAsync(Guid caseId, Guid evidenceItemId, CancellationToken ct);

    Task RefreshForIdentifierAsync(Guid caseId, Guid identifierId, CancellationToken ct);
}
