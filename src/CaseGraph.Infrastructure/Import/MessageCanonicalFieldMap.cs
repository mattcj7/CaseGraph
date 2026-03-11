namespace CaseGraph.Infrastructure.Import;

public enum CanonicalMessageField
{
    MessageExternalId,
    ThreadExternalId,
    ThreadTitle,
    Platform,
    SentUtc,
    Direction,
    SenderDisplay,
    SenderIdentifier,
    RecipientDisplays,
    RecipientIdentifiers,
    Body,
    AttachmentCount,
    AttachmentPresent,
    DeletedFlag
}

public sealed record MessageFieldMatch(
    int ColumnIndex,
    string Header,
    string NormalizedHeader,
    CanonicalMessageField Field
);

public sealed class MessageCanonicalFieldMap
{
    private static readonly IReadOnlyDictionary<string, CanonicalMessageField> AliasLookup = BuildAliasLookup();

    public MessageCanonicalFieldMap(
        IReadOnlyDictionary<int, CanonicalMessageField> columns,
        IReadOnlyList<MessageFieldMatch> matches,
        IReadOnlyList<string> unmappedHeaders,
        bool looksLikeMessageExport
    )
    {
        Columns = columns;
        Matches = matches;
        UnmappedHeaders = unmappedHeaders;
        LooksLikeMessageExport = looksLikeMessageExport;
        MatchedFields = matches
            .Select(match => match.Field)
            .Distinct()
            .OrderBy(field => field.ToString(), StringComparer.Ordinal)
            .ToArray();
    }

    public IReadOnlyDictionary<int, CanonicalMessageField> Columns { get; }

    public IReadOnlyList<MessageFieldMatch> Matches { get; }

    public IReadOnlyList<string> UnmappedHeaders { get; }

    public IReadOnlyList<CanonicalMessageField> MatchedFields { get; }

    public bool LooksLikeMessageExport { get; }

    public static MessageCanonicalFieldMap Create(IReadOnlyList<string?> headers)
    {
        ArgumentNullException.ThrowIfNull(headers);

        var columns = new Dictionary<int, CanonicalMessageField>();
        var matches = new List<MessageFieldMatch>();
        var unmappedHeaders = new List<string>();

        for (var index = 0; index < headers.Count; index++)
        {
            var header = headers[index];
            if (string.IsNullOrWhiteSpace(header))
            {
                continue;
            }

            var normalized = NormalizeHeader(header);
            if (TryResolve(header, out var field))
            {
                columns[index] = field;
                matches.Add(new MessageFieldMatch(index, header.Trim(), normalized, field));
                continue;
            }

            unmappedHeaders.Add(header.Trim());
        }

        var looksLikeMessageExport = IsLikelyMessageExport(matches.Select(match => match.Field));
        return new MessageCanonicalFieldMap(
            columns,
            matches,
            unmappedHeaders.OrderBy(value => value, StringComparer.Ordinal).ToArray(),
            looksLikeMessageExport
        );
    }

    public IReadOnlyDictionary<CanonicalMessageField, string?> ReadRow(IReadOnlyList<string?> rowValues)
    {
        ArgumentNullException.ThrowIfNull(rowValues);

        var values = new Dictionary<CanonicalMessageField, string?>();
        foreach (var match in Matches)
        {
            values[match.Field] = match.ColumnIndex < rowValues.Count
                ? NullIfWhiteSpace(rowValues[match.ColumnIndex])
                : null;
        }

        return values;
    }

    public bool LooksPopulated(IReadOnlyList<string?> rowValues)
    {
        ArgumentNullException.ThrowIfNull(rowValues);

        foreach (var match in Matches)
        {
            if (match.ColumnIndex >= rowValues.Count)
            {
                continue;
            }

            if (!string.IsNullOrWhiteSpace(rowValues[match.ColumnIndex]))
            {
                return true;
            }
        }

        return false;
    }

    public static bool TryResolve(string? header, out CanonicalMessageField field)
    {
        var normalized = NormalizeHeader(header);
        return AliasLookup.TryGetValue(normalized, out field);
    }

    public static string NormalizeHeader(string? header)
    {
        if (string.IsNullOrWhiteSpace(header))
        {
            return string.Empty;
        }

        return new string(header
            .Trim()
            .ToLowerInvariant()
            .Where(char.IsLetterOrDigit)
            .ToArray());
    }

    private static IReadOnlyDictionary<string, CanonicalMessageField> BuildAliasLookup()
    {
        var lookup = new Dictionary<string, CanonicalMessageField>(StringComparer.Ordinal);

        AddAliases(
            lookup,
            CanonicalMessageField.MessageExternalId,
            "messageid",
            "messageexternalid",
            "msgid",
            "externalid",
            "recordid",
            "eventid");
        AddAliases(
            lookup,
            CanonicalMessageField.ThreadExternalId,
            "threadid",
            "conversationid",
            "chatid",
            "threadkey",
            "thread",
            "conversation",
            "chat",
            "chatthreadid");
        AddAliases(
            lookup,
            CanonicalMessageField.ThreadTitle,
            "threadtitle",
            "threadname",
            "conversationtitle",
            "conversationname",
            "chattitle",
            "chatname",
            "title");
        AddAliases(
            lookup,
            CanonicalMessageField.Platform,
            "platform",
            "app",
            "application",
            "service",
            "sourceapp");
        AddAliases(
            lookup,
            CanonicalMessageField.SentUtc,
            "timestamp",
            "time",
            "date",
            "datetime",
            "sentat",
            "sentutc",
            "senttime",
            "messagedate",
            "messagetime",
            "messagedatetime",
            "createdat");
        AddAliases(
            lookup,
            CanonicalMessageField.Direction,
            "direction",
            "type",
            "incomingoutgoing",
            "messagedirection");
        AddAliases(
            lookup,
            CanonicalMessageField.SenderDisplay,
            "sender",
            "senderdisplay",
            "sendername",
            "from",
            "author",
            "source",
            "sourceuser");
        AddAliases(
            lookup,
            CanonicalMessageField.SenderIdentifier,
            "senderidentifier",
            "senderid",
            "senderphone",
            "senderemail",
            "fromid",
            "fromidentifier",
            "sourceidentifier");
        AddAliases(
            lookup,
            CanonicalMessageField.RecipientDisplays,
            "recipient",
            "recipients",
            "to",
            "targets",
            "destination",
            "participantnames");
        AddAliases(
            lookup,
            CanonicalMessageField.RecipientIdentifiers,
            "recipientidentifier",
            "recipientidentifiers",
            "recipientid",
            "recipientids",
            "toid",
            "toidentifier",
            "destinationidentifier",
            "participants");
        AddAliases(
            lookup,
            CanonicalMessageField.Body,
            "body",
            "message",
            "text",
            "content",
            "messagebody",
            "messagecontent");
        AddAliases(
            lookup,
            CanonicalMessageField.AttachmentCount,
            "attachmentcount",
            "attachments",
            "mediacount");
        AddAliases(
            lookup,
            CanonicalMessageField.AttachmentPresent,
            "hasattachments",
            "attachmentpresent",
            "attachmentspresent",
            "hasmedia");
        AddAliases(
            lookup,
            CanonicalMessageField.DeletedFlag,
            "deleted",
            "isdeleted",
            "removed");

        return lookup;
    }

    private static void AddAliases(
        IDictionary<string, CanonicalMessageField> lookup,
        CanonicalMessageField field,
        params string[] aliases
    )
    {
        foreach (var alias in aliases)
        {
            var normalized = NormalizeHeader(alias);
            if (normalized.Length == 0)
            {
                continue;
            }

            lookup[normalized] = field;
        }
    }

    private static bool IsLikelyMessageExport(IEnumerable<CanonicalMessageField> fields)
    {
        var fieldSet = fields.Distinct().ToHashSet();
        if (fieldSet.Count < 3)
        {
            return false;
        }

        var hasActor = fieldSet.Contains(CanonicalMessageField.SenderDisplay)
            || fieldSet.Contains(CanonicalMessageField.SenderIdentifier)
            || fieldSet.Contains(CanonicalMessageField.RecipientDisplays)
            || fieldSet.Contains(CanonicalMessageField.RecipientIdentifiers);
        var hasMessageSignal = fieldSet.Contains(CanonicalMessageField.Body)
            || fieldSet.Contains(CanonicalMessageField.ThreadExternalId)
            || fieldSet.Contains(CanonicalMessageField.MessageExternalId)
            || fieldSet.Contains(CanonicalMessageField.SentUtc)
            || fieldSet.Contains(CanonicalMessageField.Direction);

        return hasActor && hasMessageSignal;
    }

    private static string? NullIfWhiteSpace(string? value)
    {
        return string.IsNullOrWhiteSpace(value)
            ? null
            : value.Trim();
    }
}
