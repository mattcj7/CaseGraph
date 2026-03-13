using CaseGraph.Infrastructure.Locations;
using CaseGraph.Infrastructure.Timeline;
using System.Globalization;

namespace CaseGraph.Infrastructure.Incidents;

public sealed record IncidentCrossReferenceResult(
    IncidentRecord Incident,
    string CorrelationId,
    DateTimeOffset ExecutedUtc,
    IReadOnlyList<TimelineRowDto> MessageResults,
    IReadOnlyList<IncidentLocationHit> LocationResults,
    IReadOnlyList<IncidentTimelineItem> TimelineItems
);

public sealed record IncidentLocationHit(
    Guid IncidentLocationId,
    string SceneLabel,
    double DistanceMeters,
    LocationRowDto Location
)
{
    public string DistanceDisplay => $"{DistanceMeters.ToString("F1", CultureInfo.CurrentCulture)} m";
}

public sealed record IncidentTimelineItem(
    string MarkerType,
    DateTimeOffset TimestampUtc,
    string Title,
    string Summary,
    string? Citation,
    bool IsAnchor,
    Guid? MessageEventId,
    Guid? LocationObservationId,
    Guid? SourceEvidenceItemId,
    string? SourceLocator
)
{
    public string TimestampLocalDisplay => TimestampUtc.ToLocalTime().ToString("g", CultureInfo.CurrentCulture);
}
