namespace CaseGraph.Infrastructure.Persistence.Entities;

public sealed class GangDocumentationStatusHistoryRecord
{
    public Guid HistoryEntryId { get; set; }

    public Guid DocumentationId { get; set; }

    public string ActionType { get; set; } = string.Empty;

    public string Summary { get; set; } = string.Empty;

    public string? PreviousWorkflowStatus { get; set; }

    public string? NewWorkflowStatus { get; set; }

    public string? DecisionNote { get; set; }

    public string? ChangedBy { get; set; }

    public string? ChangedByIdentity { get; set; }

    public DateTimeOffset ChangedAtUtc { get; set; }

    public GangDocumentationRecordEntity? Documentation { get; set; }
}
