namespace CaseGraph.SyntheticDataGenerator.Models;

public sealed class GeneratorOptions
{
    public const string OffenseWindowProfile = "offense_window";

    public static IReadOnlyList<string> SupportedProfiles { get; } = [OffenseWindowProfile];

    public int Seed { get; init; } = 424242;

    public int DatasetCount { get; init; } = 1;

    public int PersonCount { get; init; } = 6;

    public int ApproximateMessageCount { get; init; } = 120;

    public int ApproximateLocationCount { get; init; } = 48;

    public string Profile { get; init; } = OffenseWindowProfile;

    public string OutputFolder { get; init; } = string.Empty;

    public void Validate()
    {
        if (DatasetCount < 1 || DatasetCount > 25)
        {
            throw new ArgumentOutOfRangeException(
                nameof(DatasetCount),
                DatasetCount,
                "Dataset count must be between 1 and 25."
            );
        }

        if (PersonCount < 2 || PersonCount > 40)
        {
            throw new ArgumentOutOfRangeException(
                nameof(PersonCount),
                PersonCount,
                "Person count must be between 2 and 40."
            );
        }

        if (ApproximateMessageCount < 12 || ApproximateMessageCount > 5000)
        {
            throw new ArgumentOutOfRangeException(
                nameof(ApproximateMessageCount),
                ApproximateMessageCount,
                "Approximate message count must be between 12 and 5000."
            );
        }

        if (ApproximateLocationCount < 6 || ApproximateLocationCount > 5000)
        {
            throw new ArgumentOutOfRangeException(
                nameof(ApproximateLocationCount),
                ApproximateLocationCount,
                "Approximate location count must be between 6 and 5000."
            );
        }

        if (!SupportedProfiles.Contains(Profile, StringComparer.Ordinal))
        {
            throw new ArgumentException(
                $"Profile '{Profile}' is not supported. Supported profiles: {string.Join(", ", SupportedProfiles)}.",
                nameof(Profile)
            );
        }

        if (string.IsNullOrWhiteSpace(OutputFolder))
        {
            throw new ArgumentException("An output folder is required.", nameof(OutputFolder));
        }
    }
}
