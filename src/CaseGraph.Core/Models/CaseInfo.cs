namespace CaseGraph.Core.Models;

public sealed class CaseInfo
{
    public Guid CaseId { get; set; }

    public string Name { get; set; } = string.Empty;

    public DateTimeOffset CreatedAtUtc { get; set; }

    public DateTimeOffset? LastOpenedAtUtc { get; set; }
}
