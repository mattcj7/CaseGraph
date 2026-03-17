namespace CaseGraph.Infrastructure.GangDocumentation;

public sealed record GangDocumentationStatusHistoryEntry(
    Guid HistoryEntryId,
    Guid DocumentationId,
    string ActionType,
    string Summary,
    string? PreviousWorkflowStatus,
    string? NewWorkflowStatus,
    string? DecisionNote,
    string? ChangedBy,
    string? ChangedByIdentity,
    DateTimeOffset ChangedAtUtc
)
{
    public string? NewWorkflowStatusDisplay => string.IsNullOrWhiteSpace(NewWorkflowStatus)
        ? null
        : GangDocumentationCatalog.GetWorkflowStatusDisplayName(NewWorkflowStatus);
}
