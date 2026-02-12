namespace CaseGraph.Core.Abstractions;

public interface IClock
{
    DateTimeOffset UtcNow { get; }
}
