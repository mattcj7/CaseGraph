using System.Globalization;
using System.Text.RegularExpressions;

namespace CaseGraph.Infrastructure.Import;

public sealed record CanonicalMessageContext(
    Guid SourceEvidenceItemId,
    string SourceLocator,
    string ParserFamily,
    string ParserVersion,
    string? PlatformHint
);

public sealed record CanonicalMessageParseResult(
    CanonicalMessageRecord? Record,
    string? SkipReason
)
{
    public bool Success => Record is not null;

    public static CanonicalMessageParseResult Skipped(string reason)
    {
        return new CanonicalMessageParseResult(null, reason);
    }
}

public sealed record CanonicalMessageRecord(
    string? MessageExternalId,
    string? ThreadExternalId,
    string? ThreadTitle,
    string Platform,
    DateTimeOffset? SentUtc,
    string Direction,
    string? SenderDisplay,
    string? SenderIdentifier,
    string? RecipientDisplays,
    string? RecipientIdentifiers,
    string? Body,
    int? AttachmentCount,
    bool HasAttachments,
    bool DeletedFlag,
    Guid SourceEvidenceItemId,
    string SourceLocator,
    string ParserFamily,
    string ParserVersion,
    IReadOnlyList<string> ParseWarnings
)
{
    public string? SenderValue => FirstNonEmpty(SenderIdentifier, SenderDisplay);

    public string? RecipientValue => FirstNonEmpty(RecipientIdentifiers, RecipientDisplays);

    public static CanonicalMessageParseResult TryCreate(
        IReadOnlyDictionary<CanonicalMessageField, string?> values,
        CanonicalMessageContext context
    )
    {
        ArgumentNullException.ThrowIfNull(values);
        ArgumentNullException.ThrowIfNull(context);
        ArgumentException.ThrowIfNullOrWhiteSpace(context.SourceLocator);
        ArgumentException.ThrowIfNullOrWhiteSpace(context.ParserFamily);
        ArgumentException.ThrowIfNullOrWhiteSpace(context.ParserVersion);

        var warnings = new List<string>();

        var messageExternalId = Get(values, CanonicalMessageField.MessageExternalId);
        var threadExternalId = Get(values, CanonicalMessageField.ThreadExternalId);
        var threadTitle = Get(values, CanonicalMessageField.ThreadTitle);
        var body = Get(values, CanonicalMessageField.Body);
        var senderDisplay = Get(values, CanonicalMessageField.SenderDisplay);
        var senderIdentifier = Get(values, CanonicalMessageField.SenderIdentifier);
        var recipientDisplays = Get(values, CanonicalMessageField.RecipientDisplays);
        var recipientIdentifiers = Get(values, CanonicalMessageField.RecipientIdentifiers);

        if (string.IsNullOrWhiteSpace(senderIdentifier))
        {
            senderIdentifier = InferIdentifierValue(senderDisplay);
        }

        if (string.IsNullOrWhiteSpace(recipientIdentifiers))
        {
            recipientIdentifiers = InferIdentifierList(recipientDisplays);
        }

        if (!HasMinimumSignal(
                body,
                senderDisplay,
                senderIdentifier,
                recipientDisplays,
                recipientIdentifiers,
                threadExternalId,
                messageExternalId))
        {
            return CanonicalMessageParseResult.Skipped(
                "Row does not contain enough message fields to normalize safely."
            );
        }

        var platformRaw = FirstNonEmpty(
            Get(values, CanonicalMessageField.Platform),
            context.PlatformHint);
        var platform = NormalizePlatform(platformRaw);
        if (string.IsNullOrWhiteSpace(platformRaw))
        {
            warnings.Add("Platform missing; defaulted to OTHER.");
        }

        var timestampRaw = Get(values, CanonicalMessageField.SentUtc);
        var sentUtc = ParseTimestamp(timestampRaw);
        if (string.IsNullOrWhiteSpace(timestampRaw) || sentUtc is null)
        {
            warnings.Add("Timestamp missing or invalid.");
        }

        var directionRaw = Get(values, CanonicalMessageField.Direction);
        var direction = NormalizeDirection(directionRaw);
        if (!string.IsNullOrWhiteSpace(directionRaw) && string.Equals(direction, "Unknown", StringComparison.Ordinal))
        {
            warnings.Add("Direction value was not recognized.");
        }

        var attachmentCount = ParseOptionalInt(Get(values, CanonicalMessageField.AttachmentCount), out var attachmentCountValid);
        if (!attachmentCountValid)
        {
            warnings.Add("Attachment count was invalid and was ignored.");
        }

        var hasAttachments = attachmentCount.GetValueOrDefault() > 0
            || ParseBoolean(Get(values, CanonicalMessageField.AttachmentPresent));

        if (string.IsNullOrWhiteSpace(body))
        {
            warnings.Add("Body missing.");
        }

        if (string.IsNullOrWhiteSpace(senderDisplay) && string.IsNullOrWhiteSpace(senderIdentifier))
        {
            warnings.Add("Sender missing.");
        }

        if (string.IsNullOrWhiteSpace(recipientDisplays) && string.IsNullOrWhiteSpace(recipientIdentifiers))
        {
            warnings.Add("Recipients missing.");
        }

        if (string.IsNullOrWhiteSpace(threadExternalId))
        {
            warnings.Add("Thread identifier missing; ingest will derive a fallback thread key.");
        }

        return new CanonicalMessageParseResult(
            new CanonicalMessageRecord(
                MessageExternalId: messageExternalId,
                ThreadExternalId: threadExternalId,
                ThreadTitle: threadTitle,
                Platform: platform,
                SentUtc: sentUtc,
                Direction: direction,
                SenderDisplay: senderDisplay,
                SenderIdentifier: senderIdentifier,
                RecipientDisplays: recipientDisplays,
                RecipientIdentifiers: recipientIdentifiers,
                Body: body,
                AttachmentCount: attachmentCount,
                HasAttachments: hasAttachments,
                DeletedFlag: ParseBoolean(Get(values, CanonicalMessageField.DeletedFlag)),
                SourceEvidenceItemId: context.SourceEvidenceItemId,
                SourceLocator: context.SourceLocator,
                ParserFamily: context.ParserFamily,
                ParserVersion: context.ParserVersion,
                ParseWarnings: warnings.ToArray()),
            SkipReason: null);
    }

    private static bool HasMinimumSignal(params string?[] values)
    {
        return values.Any(value => !string.IsNullOrWhiteSpace(value));
    }

    private static string? Get(
        IReadOnlyDictionary<CanonicalMessageField, string?> values,
        CanonicalMessageField field
    )
    {
        return values.TryGetValue(field, out var value)
            ? NullIfWhiteSpace(value)
            : null;
    }

    private static string? InferIdentifierValue(string? value)
    {
        var normalized = NullIfWhiteSpace(value);
        if (normalized is null)
        {
            return null;
        }

        if (normalized.Contains('@', StringComparison.Ordinal))
        {
            return normalized;
        }

        if (normalized.StartsWith("@", StringComparison.Ordinal))
        {
            return normalized;
        }

        var digitsOnly = DigitsOnlyRegex.Replace(normalized, string.Empty);
        return digitsOnly.Length >= 7
            ? normalized
            : null;
    }

    private static string? InferIdentifierList(string? value)
    {
        var normalized = NullIfWhiteSpace(value);
        if (normalized is null)
        {
            return null;
        }

        var inferred = normalized
            .Split([';', '|', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(InferIdentifierValue)
            .Where(item => !string.IsNullOrWhiteSpace(item))
            .ToArray();

        return inferred.Length == 0
            ? null
            : string.Join("; ", inferred);
    }

    private static int? ParseOptionalInt(string? value, out bool isValid)
    {
        isValid = true;
        var normalized = NullIfWhiteSpace(value);
        if (normalized is null)
        {
            return null;
        }

        if (int.TryParse(normalized, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
        {
            return parsed;
        }

        var digits = new string(normalized.Where(char.IsDigit).ToArray());
        if (digits.Length > 0
            && int.TryParse(digits, NumberStyles.Integer, CultureInfo.InvariantCulture, out parsed))
        {
            return parsed;
        }

        isValid = false;
        return null;
    }

    private static DateTimeOffset? ParseTimestamp(string? value)
    {
        var normalized = NullIfWhiteSpace(value);
        if (normalized is null)
        {
            return null;
        }

        if (DateTimeOffset.TryParse(
                normalized,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AllowWhiteSpaces,
                out var dto))
        {
            return dto.ToUniversalTime();
        }

        if (double.TryParse(normalized, NumberStyles.Any, CultureInfo.InvariantCulture, out var oaSerial))
        {
            try
            {
                return new DateTimeOffset(
                    DateTime.SpecifyKind(DateTime.FromOADate(oaSerial), DateTimeKind.Utc)
                );
            }
            catch (ArgumentException)
            {
            }
        }

        if (long.TryParse(normalized, NumberStyles.Integer, CultureInfo.InvariantCulture, out var epoch))
        {
            try
            {
                return epoch > 10_000_000_000
                    ? DateTimeOffset.FromUnixTimeMilliseconds(epoch).ToUniversalTime()
                    : DateTimeOffset.FromUnixTimeSeconds(epoch).ToUniversalTime();
            }
            catch (ArgumentOutOfRangeException)
            {
            }
        }

        return null;
    }

    public static string NormalizeDirection(string? value)
    {
        var normalized = NullIfWhiteSpace(value);
        if (normalized is null)
        {
            return "Unknown";
        }

        normalized = normalized.ToLowerInvariant();
        if (normalized.Contains("out", StringComparison.Ordinal))
        {
            return "Outgoing";
        }

        if (normalized.Contains("in", StringComparison.Ordinal))
        {
            return "Incoming";
        }

        return "Unknown";
    }

    public static string NormalizePlatform(string? value)
    {
        var normalized = NullIfWhiteSpace(value);
        if (normalized is null)
        {
            return "OTHER";
        }

        normalized = normalized.ToLowerInvariant();
        if (normalized.Contains("sms", StringComparison.Ordinal))
        {
            return "SMS";
        }

        if (normalized.Contains("imessage", StringComparison.Ordinal))
        {
            return "iMessage";
        }

        if (normalized.Contains("whatsapp", StringComparison.Ordinal))
        {
            return "WhatsApp";
        }

        if (normalized.Contains("signal", StringComparison.Ordinal))
        {
            return "Signal";
        }

        if (normalized.Contains("instagram", StringComparison.Ordinal))
        {
            return "Instagram";
        }

        return "OTHER";
    }

    public static bool ParseBoolean(string? value)
    {
        var normalized = NullIfWhiteSpace(value);
        if (normalized is null)
        {
            return false;
        }

        return normalized.ToLowerInvariant() is "1" or "true" or "yes" or "y" or "deleted";
    }

    private static string? FirstNonEmpty(params string?[] values)
    {
        return values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));
    }

    private static string? NullIfWhiteSpace(string? value)
    {
        return string.IsNullOrWhiteSpace(value)
            ? null
            : value.Trim();
    }

    private static readonly Regex DigitsOnlyRegex = new("[^0-9]", RegexOptions.Compiled);
}
