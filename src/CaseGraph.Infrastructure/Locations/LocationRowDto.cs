using System.Globalization;

namespace CaseGraph.Infrastructure.Locations;

public sealed record LocationRowDto(
    Guid LocationObservationId,
    Guid CaseId,
    DateTimeOffset ObservedUtc,
    double Latitude,
    double Longitude,
    double? AccuracyMeters,
    double? AltitudeMeters,
    double? SpeedMps,
    double? HeadingDegrees,
    string SourceType,
    string? SourceLabel,
    string? SubjectType,
    Guid? SubjectId,
    Guid SourceEvidenceItemId,
    string SourceLocator,
    string IngestModuleVersion
)
{
    public string? EvidenceDisplayName { get; init; }

    public string? StoredRelativePath { get; init; }

    public string? SubjectDisplayName { get; init; }

    public double? DistanceFromCenterMeters { get; init; }

    public string TimestampLocalDisplay => ObservedUtc
        .ToLocalTime()
        .ToString("g", CultureInfo.CurrentCulture);

    public string CoordinatesDisplay =>
        $"{Latitude.ToString("F6", CultureInfo.CurrentCulture)}, {Longitude.ToString("F6", CultureInfo.CurrentCulture)}";

    public string Citation => $"{SourceEvidenceItemId:D} | {SourceLocator} | {LocationObservationId:D}";
}
