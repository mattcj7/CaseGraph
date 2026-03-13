namespace CaseGraph.Infrastructure.Persistence.Entities;

public sealed class IncidentPinnedResultRecord
{
    public Guid IncidentPinnedResultId { get; set; }

    public Guid IncidentId { get; set; }

    public string ResultType { get; set; } = string.Empty;

    public Guid SourceRecordId { get; set; }

    public Guid SourceEvidenceItemId { get; set; }

    public string SourceLocator { get; set; } = string.Empty;

    public string Citation { get; set; } = string.Empty;

    public string Title { get; set; } = string.Empty;

    public string Summary { get; set; } = string.Empty;

    public DateTimeOffset? EventUtc { get; set; }

    public double? Latitude { get; set; }

    public double? Longitude { get; set; }

    public string? SceneLabel { get; set; }

    public DateTimeOffset PinnedUtc { get; set; }

    public IncidentRecordEntity? Incident { get; set; }
}
