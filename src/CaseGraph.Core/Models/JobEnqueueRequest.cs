namespace CaseGraph.Core.Models;

public sealed record JobEnqueueRequest(
    string JobType,
    Guid? CaseId,
    Guid? EvidenceItemId,
    string JsonPayload
);
