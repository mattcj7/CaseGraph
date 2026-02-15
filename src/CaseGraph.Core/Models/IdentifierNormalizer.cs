using System.Text;
using System.Text.RegularExpressions;

namespace CaseGraph.Core.Models;

public static partial class IdentifierNormalizer
{
    public static string Normalize(TargetIdentifierType type, string valueRaw)
    {
        var raw = valueRaw?.Trim() ?? string.Empty;
        if (raw.Length == 0)
        {
            return string.Empty;
        }

        return type switch
        {
            TargetIdentifierType.Phone => NormalizePhone(raw),
            TargetIdentifierType.Email => raw.ToLowerInvariant(),
            TargetIdentifierType.SocialHandle => raw.TrimStart('@').ToLowerInvariant(),
            TargetIdentifierType.VehiclePlate or TargetIdentifierType.VIN => CollapseAlphaNumericUpper(raw),
            TargetIdentifierType.IMEI
            or TargetIdentifierType.IMSI
            or TargetIdentifierType.DeviceId => CollapseAlphaNumericUpper(raw),
            TargetIdentifierType.Username => raw.ToLowerInvariant(),
            _ => raw
        };
    }

    public static TargetIdentifierType InferType(string valueRaw)
    {
        var raw = valueRaw?.Trim() ?? string.Empty;
        if (raw.Length == 0)
        {
            return TargetIdentifierType.Other;
        }

        if (raw.StartsWith("@", StringComparison.Ordinal))
        {
            return TargetIdentifierType.SocialHandle;
        }

        if (raw.Contains('@') && raw.Contains('.'))
        {
            return TargetIdentifierType.Email;
        }

        var digitCount = DigitRegex().Matches(raw).Count;
        if (digitCount >= 7)
        {
            return TargetIdentifierType.Phone;
        }

        return TargetIdentifierType.Username;
    }

    private static string NormalizePhone(string raw)
    {
        var hasLeadingPlus = raw.StartsWith('+');
        var digits = new string(raw.Where(char.IsDigit).ToArray());
        if (digits.Length == 0)
        {
            return string.Empty;
        }

        if (hasLeadingPlus)
        {
            return $"+{digits}";
        }

        if (digits.Length == 10)
        {
            return $"+1{digits}";
        }

        if (digits.Length == 11 && digits.StartsWith("1", StringComparison.Ordinal))
        {
            return $"+{digits}";
        }

        return $"+{digits}";
    }

    private static string CollapseAlphaNumericUpper(string raw)
    {
        var sb = new StringBuilder(raw.Length);
        foreach (var ch in raw)
        {
            if (char.IsLetterOrDigit(ch))
            {
                sb.Append(char.ToUpperInvariant(ch));
            }
        }

        return sb.ToString();
    }

    [GeneratedRegex(@"\d", RegexOptions.Compiled)]
    private static partial Regex DigitRegex();
}
