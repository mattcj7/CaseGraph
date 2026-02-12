using CaseGraph.Core.Abstractions;

namespace CaseGraph.Infrastructure.Services;

public sealed class SystemClock : IClock
{
    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
}
