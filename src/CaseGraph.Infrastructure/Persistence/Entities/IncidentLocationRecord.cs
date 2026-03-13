namespace CaseGraph.Infrastructure.Persistence.Entities;

public sealed class IncidentLocationRecord
{
    public Guid IncidentLocationId { get; set; }

    public Guid IncidentId { get; set; }

    public int SortOrder { get; set; }

    public string Label { get; set; } = string.Empty;

    public double Latitude { get; set; }

    public double Longitude { get; set; }

    public double RadiusMeters { get; set; }

    public string Notes { get; set; } = string.Empty;

    public IncidentRecordEntity? Incident { get; set; }
}
