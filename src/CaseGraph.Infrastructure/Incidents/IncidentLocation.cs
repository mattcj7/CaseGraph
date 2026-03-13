namespace CaseGraph.Infrastructure.Incidents;

public sealed record IncidentLocation(
    Guid IncidentLocationId,
    int SortOrder,
    string Label,
    double Latitude,
    double Longitude,
    double RadiusMeters,
    string Notes
);
