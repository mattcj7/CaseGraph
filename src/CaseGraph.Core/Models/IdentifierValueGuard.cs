namespace CaseGraph.Core.Models;

public static class IdentifierValueGuard
{
    public const string RequiredMessage = "Identifier value is required.";

    public static bool TryPrepare(
        TargetIdentifierType type,
        string? valueRaw,
        out string preparedIdentifierValue
    )
    {
        preparedIdentifierValue = valueRaw?.Trim() ?? string.Empty;
        if (preparedIdentifierValue.Length == 0)
        {
            return false;
        }

        var normalized = IdentifierNormalizer.Normalize(type, preparedIdentifierValue);
        return !string.IsNullOrWhiteSpace(normalized);
    }
}
