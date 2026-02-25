namespace CaseGraph.Infrastructure.Persistence.Entities;

public sealed class PersonEntityRecord
{
    public Guid GlobalEntityId { get; set; }

    public string DisplayName { get; set; } = string.Empty;

    public DateTimeOffset CreatedAtUtc { get; set; }

    public DateTimeOffset UpdatedAtUtc { get; set; }

    public ICollection<PersonAliasRecord> Aliases { get; set; } = new List<PersonAliasRecord>();

    public ICollection<PersonIdentifierRecord> Identifiers { get; set; } = new List<PersonIdentifierRecord>();
}
