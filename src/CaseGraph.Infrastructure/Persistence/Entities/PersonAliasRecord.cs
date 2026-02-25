namespace CaseGraph.Infrastructure.Persistence.Entities;

public sealed class PersonAliasRecord
{
    public Guid AliasId { get; set; }

    public Guid GlobalEntityId { get; set; }

    public string Alias { get; set; } = string.Empty;

    public string AliasNormalized { get; set; } = string.Empty;

    public string? Notes { get; set; }

    public PersonEntityRecord? Person { get; set; }
}
