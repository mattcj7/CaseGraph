namespace CaseGraph.Infrastructure.Persistence.Entities;

public sealed class IncidentRecordEntity
{
    public Guid IncidentId { get; set; }

    public Guid CaseId { get; set; }

    public string Title { get; set; } = string.Empty;

    public string IncidentType { get; set; } = string.Empty;

    public string Status { get; set; } = string.Empty;

    public string SummaryNotes { get; set; } = string.Empty;

    public DateTimeOffset? PrimaryOccurrenceUtc { get; set; }

    public DateTimeOffset OffenseWindowStartUtc { get; set; }

    public DateTimeOffset OffenseWindowEndUtc { get; set; }

    public DateTimeOffset CreatedUtc { get; set; }

    public DateTimeOffset UpdatedUtc { get; set; }

    public CaseRecord? Case { get; set; }

    public ICollection<IncidentLocationRecord> Locations { get; set; } = new List<IncidentLocationRecord>();

    public ICollection<IncidentPinnedResultRecord> PinnedResults { get; set; } = new List<IncidentPinnedResultRecord>();
}
