namespace CaseGraph.Infrastructure.Persistence.Entities;

public sealed class PersonIdentifierRecord
{
    public Guid PersonIdentifierId { get; set; }

    public Guid GlobalEntityId { get; set; }

    public string Type { get; set; } = string.Empty;

    public string ValueNormalized { get; set; } = string.Empty;

    public string ValueDisplay { get; set; } = string.Empty;

    public bool IsPrimary { get; set; }

    public string? Notes { get; set; }

    public DateTimeOffset CreatedAtUtc { get; set; }

    public PersonEntityRecord? Person { get; set; }
}
