namespace CaseGraph.Core.Models;

public sealed record JobInfo
{
    public Guid JobId { get; init; }

    public DateTimeOffset CreatedAtUtc { get; init; }

    public DateTimeOffset? StartedAtUtc { get; init; }

    public DateTimeOffset? CompletedAtUtc { get; init; }

    public JobStatus Status { get; init; }

    public string JobType { get; init; } = string.Empty;

    public Guid? CaseId { get; init; }

    public Guid? EvidenceItemId { get; init; }

    public double Progress { get; init; }

    public string StatusMessage { get; init; } = string.Empty;

    public string? ErrorMessage { get; init; }

    public string JsonPayload { get; init; } = string.Empty;

    public string CorrelationId { get; init; } = string.Empty;

    public string Operator { get; init; } = string.Empty;
}
