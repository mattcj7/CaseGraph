using System.Globalization;

namespace CaseGraph.Infrastructure.Timeline;

public sealed record TimelineRowDto(
    Guid MessageEventId,
    Guid CaseId,
    Guid SourceEvidenceItemId,
    string EventType,
    DateTimeOffset? TimestampUtc,
    string Direction,
    string ParticipantsSummary,
    string Preview,
    string SourceLocator,
    string IngestModuleVersion
)
{
    public string Platform { get; init; } = string.Empty;

    public string? EvidenceDisplayName { get; init; }

    public string? StoredRelativePath { get; init; }

    public string? ThreadKey { get; init; }

    public string? SenderRaw { get; init; }

    public string? RecipientsRaw { get; init; }

    public string? SenderDisplay { get; init; }

    public string? RecipientsDisplay { get; init; }

    public string? Body { get; init; }

    public string TimestampLocalDisplay => TimestampUtc.HasValue
        ? TimestampUtc.Value.ToLocalTime().ToString("g", CultureInfo.CurrentCulture)
        : "(no timestamp)";

    public string Citation => $"{SourceEvidenceItemId:D} | {SourceLocator} | {MessageEventId:D}";
}
