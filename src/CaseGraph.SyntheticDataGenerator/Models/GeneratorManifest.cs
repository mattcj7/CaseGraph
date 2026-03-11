namespace CaseGraph.SyntheticDataGenerator.Models;

public sealed class GeneratorManifest
{
    public string GeneratorName { get; init; } = "CaseGraph Synthetic Evidence Generator";

    public bool IsSynthetic { get; init; } = true;

    public string FictionalNotice { get; init; } = "All people, identifiers, locations, and events in this dataset are fictional synthetic test data.";

    public int Seed { get; init; }

    public int DatasetIndex { get; init; }

    public string DatasetFolderName { get; init; } = string.Empty;

    public string Profile { get; init; } = GeneratorOptions.OffenseWindowProfile;

    public int PersonCount { get; init; }

    public int RequestedMessageCount { get; init; }

    public int GeneratedMessageCount { get; init; }

    public int RequestedLocationCount { get; init; }

    public int GeneratedLocationCount { get; init; }

    public DateTimeOffset GeneratedAtUtc { get; init; }

    public DateTimeOffset OffenseWindowStartUtc { get; init; }

    public DateTimeOffset OffenseWindowEndUtc { get; init; }

    public IReadOnlyList<string> CentralSubjects { get; init; } = Array.Empty<string>();
}
