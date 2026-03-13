namespace CaseGraph.Infrastructure.Incidents;

public sealed record IncidentRecord(
    Guid IncidentId,
    Guid CaseId,
    string Title,
    string IncidentType,
    string Status,
    string SummaryNotes,
    DateTimeOffset? PrimaryOccurrenceUtc,
    DateTimeOffset OffenseWindowStartUtc,
    DateTimeOffset OffenseWindowEndUtc,
    DateTimeOffset CreatedUtc,
    DateTimeOffset UpdatedUtc,
    IReadOnlyList<IncidentLocation> Locations,
    IReadOnlyList<IncidentPinnedResult> PinnedResults
);

public sealed record IncidentPinnedResult(
    Guid IncidentPinnedResultId,
    string ResultType,
    Guid SourceRecordId,
    Guid SourceEvidenceItemId,
    string SourceLocator,
    string Citation,
    string Title,
    string Summary,
    DateTimeOffset? EventUtc,
    double? Latitude,
    double? Longitude,
    string? SceneLabel,
    DateTimeOffset PinnedUtc
);
