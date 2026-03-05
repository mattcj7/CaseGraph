namespace CaseGraph.Infrastructure.Persistence.Entities;

public sealed class LocationObservationRecord
{
    public Guid LocationObservationId { get; set; }

    public Guid CaseId { get; set; }

    public DateTimeOffset ObservedUtc { get; set; }

    public double Latitude { get; set; }

    public double Longitude { get; set; }

    public double? AccuracyMeters { get; set; }

    public double? AltitudeMeters { get; set; }

    public double? SpeedMps { get; set; }

    public double? HeadingDegrees { get; set; }

    public string SourceType { get; set; } = string.Empty;

    public string? SourceLabel { get; set; }

    public string? SubjectType { get; set; }

    public Guid? SubjectId { get; set; }

    public Guid SourceEvidenceItemId { get; set; }

    public string SourceLocator { get; set; } = string.Empty;

    public string IngestModuleVersion { get; set; } = string.Empty;

    public DateTimeOffset CreatedUtc { get; set; }
}
