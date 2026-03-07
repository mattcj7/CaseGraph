using CaseGraph.Infrastructure.Locations;
using CaseGraph.Infrastructure.Timeline;
using System.Globalization;

namespace CaseGraph.Infrastructure.IncidentWindow;

public sealed record IncidentWindowQueryRequest(
    Guid CaseId,
    DateTimeOffset StartUtc,
    DateTimeOffset EndUtc,
    bool RadiusEnabled,
    double? CenterLatitude,
    double? CenterLongitude,
    double? RadiusMeters,
    string? SubjectType,
    Guid? SubjectId,
    bool IncludeCoLocationCandidates,
    int CommsTake,
    int CommsSkip,
    int GeoTake,
    int GeoSkip,
    int CoLocationTake,
    int CoLocationSkip,
    double CoLocationDistanceMeters,
    int CoLocationTimeWindowMinutes,
    string CorrelationId,
    bool WriteAuditEvent = true
);

public sealed record IncidentWindowQueryResult(
    IncidentWindowQueryPage<TimelineRowDto> Comms,
    IncidentWindowQueryPage<LocationRowDto> Geo,
    IncidentWindowQueryPage<IncidentWindowCoLocationCandidateDto> CoLocation
)
{
    public static IncidentWindowQueryResult Empty { get; } = new(
        IncidentWindowQueryPage<TimelineRowDto>.Empty,
        IncidentWindowQueryPage<LocationRowDto>.Empty,
        IncidentWindowQueryPage<IncidentWindowCoLocationCandidateDto>.Empty
    );
}

public sealed record IncidentWindowQueryPage<T>(IReadOnlyList<T> Rows, int TotalCount)
{
    public static IncidentWindowQueryPage<T> Empty { get; } = new(Array.Empty<T>(), 0);
}

public sealed record IncidentWindowCoLocationCandidateDto(
    LocationRowDto FirstObservation,
    LocationRowDto SecondObservation,
    double DistanceMeters,
    double TimeDeltaMinutes
)
{
    public string FirstSubjectDisplayName => ResolveSubjectDisplayName(FirstObservation);

    public string SecondSubjectDisplayName => ResolveSubjectDisplayName(SecondObservation);

    public string FirstTimestampLocalDisplay => FirstObservation.TimestampLocalDisplay;

    public string SecondTimestampLocalDisplay => SecondObservation.TimestampLocalDisplay;

    public string DistanceDisplay => $"{DistanceMeters.ToString("F1", CultureInfo.CurrentCulture)} m";

    public string TimeDeltaDisplay => $"{TimeDeltaMinutes.ToString("F1", CultureInfo.CurrentCulture)} min";

    public string Citation =>
        $"{FirstObservation.Citation}{Environment.NewLine}{SecondObservation.Citation}";

    public string Why =>
        $"{FirstSubjectDisplayName} and {SecondSubjectDisplayName} were {DistanceDisplay} apart within {TimeDeltaDisplay}. Evidence: "
        + $"{FirstObservation.LocationObservationId:D} @ {FirstObservation.SourceLocator}; "
        + $"{SecondObservation.LocationObservationId:D} @ {SecondObservation.SourceLocator}.";

    private static string ResolveSubjectDisplayName(LocationRowDto row)
    {
        if (!string.IsNullOrWhiteSpace(row.SubjectDisplayName))
        {
            return row.SubjectDisplayName!;
        }

        if (!string.IsNullOrWhiteSpace(row.SubjectType) && row.SubjectId.HasValue)
        {
            return $"{row.SubjectType}: {row.SubjectId:D}";
        }

        return "(unknown subject)";
    }
}

public enum IncidentWindowSubjectMode
{
    Any,
    Target,
    GlobalPerson
}
