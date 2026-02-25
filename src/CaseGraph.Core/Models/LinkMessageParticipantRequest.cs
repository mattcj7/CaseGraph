namespace CaseGraph.Core.Models;

public sealed record LinkMessageParticipantRequest(
    Guid CaseId,
    Guid MessageEventId,
    MessageParticipantRole Role,
    string ParticipantRaw,
    TargetIdentifierType? RequestedIdentifierType,
    Guid? TargetId,
    string? NewTargetDisplayName,
    IdentifierConflictResolution ConflictResolution = IdentifierConflictResolution.Cancel,
    GlobalPersonIdentifierConflictResolution GlobalConflictResolution = GlobalPersonIdentifierConflictResolution.Cancel
);
