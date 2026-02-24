using CaseGraph.Core.Abstractions;
using CaseGraph.Core.Models;
using Microsoft.Data.Sqlite;
using System.Globalization;

namespace CaseGraph.Infrastructure.Services;

public sealed class MessageSearchService : IMessageSearchService
{
    private readonly IWorkspaceDatabaseInitializer _databaseInitializer;
    private readonly IWorkspacePathProvider _workspacePathProvider;

    public MessageSearchService(
        IWorkspaceDatabaseInitializer databaseInitializer,
        IWorkspacePathProvider workspacePathProvider
    )
    {
        _databaseInitializer = databaseInitializer;
        _workspacePathProvider = workspacePathProvider;
    }

    public Task<IReadOnlyList<MessageSearchHit>> SearchAsync(
        Guid caseId,
        string query,
        string? platformFilter,
        string? senderFilter,
        string? recipientFilter,
        int take,
        int skip,
        CancellationToken ct
    )
    {
        return SearchAsync(
            new MessageSearchRequest(
                caseId,
                query,
                platformFilter,
                senderFilter,
                recipientFilter,
                TargetId: null,
                IdentifierTypeFilter: null,
                DirectionFilter: MessageDirectionFilter.Any,
                FromUtc: null,
                ToUtc: null,
                Take: take,
                Skip: skip
            ),
            ct
        );
    }

    public async Task<IReadOnlyList<MessageSearchHit>> SearchAsync(
        MessageSearchRequest request,
        CancellationToken ct
    )
    {
        await _databaseInitializer.EnsureInitializedAsync(ct);

        var prepared = PrepareRequest(request);
        if (!prepared.CanSearch)
        {
            return Array.Empty<MessageSearchHit>();
        }

        if (prepared.Query is null)
        {
            return await SearchWithoutKeywordAsync(prepared, ct);
        }

        try
        {
            return await SearchWithFtsAsync(prepared, ct);
        }
        catch (SqliteException)
        {
            return await SearchWithLikeFallbackAsync(prepared, ct);
        }
    }

    public async Task<TargetPresenceSummary?> GetTargetPresenceSummaryAsync(
        Guid caseId,
        Guid targetId,
        TargetIdentifierType? identifierTypeFilter,
        DateTimeOffset? fromUtc,
        DateTimeOffset? toUtc,
        CancellationToken ct
    )
    {
        await _databaseInitializer.EnsureInitializedAsync(ct);

        if (caseId == Guid.Empty || targetId == Guid.Empty)
        {
            return null;
        }

        var (normalizedFromUtc, normalizedToUtc) = NormalizeDateRange(fromUtc, toUtc);
        var identifierTypeText = identifierTypeFilter.HasValue
            ? ToIdentifierTypeText(identifierTypeFilter.Value)
            : null;

        await using var connection = new SqliteConnection($"Data Source={_workspacePathProvider.WorkspaceDbPath}");
        await connection.OpenAsync(ct);

        var targetExists = await TargetExistsAsync(connection, caseId, targetId, ct);
        if (!targetExists)
        {
            return null;
        }

        var byIdentifier = await GetPresenceByIdentifierAsync(
            connection,
            caseId,
            targetId,
            identifierTypeText,
            normalizedFromUtc,
            normalizedToUtc,
            ct
        );

        var (totalCount, lastSeenUtc) = await GetPresenceTotalsAsync(
            connection,
            caseId,
            targetId,
            identifierTypeText,
            normalizedFromUtc,
            normalizedToUtc,
            ct
        );

        return new TargetPresenceSummary(
            targetId,
            totalCount,
            lastSeenUtc,
            byIdentifier
        );
    }

    private static PreparedSearchRequest PrepareRequest(MessageSearchRequest request)
    {
        var caseId = request.CaseId;
        if (caseId == Guid.Empty)
        {
            return PreparedSearchRequest.Empty;
        }

        var query = NormalizeQuery(request.Query);
        var platformFilter = NormalizePlatformFilter(request.PlatformFilter);
        var senderFilter = NormalizeSubstringFilter(request.SenderFilter);
        var recipientFilter = NormalizeSubstringFilter(request.RecipientFilter);
        var targetId = request.TargetId.GetValueOrDefault() == Guid.Empty
            ? null
            : request.TargetId;
        var identifierTypeFilter = targetId.HasValue && request.IdentifierTypeFilter.HasValue
            ? ToIdentifierTypeText(request.IdentifierTypeFilter.Value)
            : null;
        var directionFilter = NormalizeDirectionFilter(request.DirectionFilter);
        var (fromUtc, toUtc) = NormalizeDateRange(request.FromUtc, request.ToUtc);
        var take = Math.Clamp(request.Take, 1, 200);
        var skip = Math.Max(0, request.Skip);
        var maxRows = Math.Clamp(take + skip + 500, 50, 2000);

        var hasStructuredFilter =
            platformFilter is not null
            || senderFilter is not null
            || recipientFilter is not null
            || targetId.HasValue
            || identifierTypeFilter is not null
            || directionFilter is not null
            || fromUtc.HasValue
            || toUtc.HasValue;

        if (query is null && !hasStructuredFilter)
        {
            return PreparedSearchRequest.Empty;
        }

        return new PreparedSearchRequest(
            caseId,
            query,
            platformFilter,
            senderFilter,
            recipientFilter,
            targetId,
            identifierTypeFilter,
            directionFilter,
            fromUtc,
            toUtc,
            take,
            skip,
            maxRows,
            CanSearch: true
        );
    }

    private async Task<IReadOnlyList<MessageSearchHit>> SearchWithFtsAsync(
        PreparedSearchRequest request,
        CancellationToken ct
    )
    {
        await using var connection = new SqliteConnection($"Data Source={_workspacePathProvider.WorkspaceDbPath}");
        await connection.OpenAsync(ct);

        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT
                me.MessageEventId,
                me.CaseId,
                me.EvidenceItemId,
                me.Platform,
                me.TimestampUtc,
                me.Sender,
                snippet(MessageEventFts, 5, '[', ']', '...', 14) AS Snippet,
                me.SourceLocator,
                me.Body,
                me.Recipients,
                mt.ThreadKey,
                ei.DisplayName,
                ei.StoredRelativePath
            FROM MessageEventFts
            INNER JOIN MessageEventRecord me ON me.MessageEventId = MessageEventFts.MessageEventId
            LEFT JOIN MessageThreadRecord mt ON mt.ThreadId = me.ThreadId
            LEFT JOIN EvidenceItemRecord ei ON ei.EvidenceItemId = me.EvidenceItemId
            WHERE me.CaseId = $caseId
              AND MessageEventFts MATCH $query
              AND ($platformFilter IS NULL OR LOWER(COALESCE(me.Platform, '')) = $platformFilter)
              AND ($senderFilter IS NULL OR LOWER(COALESCE(me.Sender, '')) LIKE '%' || $senderFilter || '%')
              AND ($recipientFilter IS NULL OR LOWER(COALESCE(me.Recipients, '')) LIKE '%' || $recipientFilter || '%')
              AND ($directionFilter IS NULL OR me.Direction = $directionFilter)
              AND ($fromUtc IS NULL OR (me.TimestampUtc IS NOT NULL AND julianday(me.TimestampUtc) >= julianday($fromUtc)))
              AND ($toUtc IS NULL OR (me.TimestampUtc IS NOT NULL AND julianday(me.TimestampUtc) <= julianday($toUtc)))
              AND (
                    $targetId IS NULL
                    OR EXISTS (
                        SELECT 1
                        FROM MessageParticipantLinkRecord mpl
                        INNER JOIN TargetIdentifierLinkRecord til
                            ON til.CaseId = mpl.CaseId
                           AND til.IdentifierId = mpl.IdentifierId
                        INNER JOIN IdentifierRecord id
                            ON id.CaseId = til.CaseId
                           AND id.IdentifierId = til.IdentifierId
                        WHERE mpl.CaseId = me.CaseId
                          AND mpl.MessageEventId = me.MessageEventId
                          AND til.TargetId = $targetId
                          AND ($identifierTypeFilter IS NULL OR id.Type = $identifierTypeFilter)
                    )
              )
            ORDER BY bm25(MessageEventFts), me.TimestampUtc DESC
            LIMIT $maxRows;
            """;
        command.Parameters.AddWithValue("$caseId", request.CaseId);
        command.Parameters.AddWithValue("$query", request.Query!);
        command.Parameters.AddWithValue("$maxRows", request.MaxRows);
        AddOptionalStringParameter(command, "$platformFilter", request.PlatformFilter);
        AddOptionalStringParameter(command, "$senderFilter", request.SenderFilter);
        AddOptionalStringParameter(command, "$recipientFilter", request.RecipientFilter);
        AddOptionalStringParameter(command, "$identifierTypeFilter", request.IdentifierTypeFilter);
        AddOptionalStringParameter(command, "$directionFilter", request.DirectionFilter);
        AddOptionalGuidParameter(command, "$targetId", request.TargetId);
        AddOptionalDateParameter(command, "$fromUtc", request.FromUtc);
        AddOptionalDateParameter(command, "$toUtc", request.ToUtc);

        var rawHits = new List<MessageSearchHit>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            rawHits.Add(ReadHit(reader));
        }

        return rawHits
            .Skip(request.Skip)
            .Take(request.Take)
            .ToList();
    }

    private async Task<IReadOnlyList<MessageSearchHit>> SearchWithLikeFallbackAsync(
        PreparedSearchRequest request,
        CancellationToken ct
    )
    {
        await using var connection = new SqliteConnection($"Data Source={_workspacePathProvider.WorkspaceDbPath}");
        await connection.OpenAsync(ct);

        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT
                me.MessageEventId,
                me.CaseId,
                me.EvidenceItemId,
                me.Platform,
                me.TimestampUtc,
                me.Sender,
                substr(COALESCE(me.Body, ''), 1, 280) AS Snippet,
                me.SourceLocator,
                me.Body,
                me.Recipients,
                mt.ThreadKey,
                ei.DisplayName,
                ei.StoredRelativePath
            FROM MessageEventRecord me
            LEFT JOIN MessageThreadRecord mt ON mt.ThreadId = me.ThreadId
            LEFT JOIN EvidenceItemRecord ei ON ei.EvidenceItemId = me.EvidenceItemId
            WHERE me.CaseId = $caseId
              AND (
                    COALESCE(me.Body, '') LIKE '%' || $query || '%'
                 OR COALESCE(me.Sender, '') LIKE '%' || $query || '%'
                 OR COALESCE(me.Recipients, '') LIKE '%' || $query || '%'
                  )
              AND ($platformFilter IS NULL OR LOWER(COALESCE(me.Platform, '')) = $platformFilter)
              AND ($senderFilter IS NULL OR LOWER(COALESCE(me.Sender, '')) LIKE '%' || $senderFilter || '%')
              AND ($recipientFilter IS NULL OR LOWER(COALESCE(me.Recipients, '')) LIKE '%' || $recipientFilter || '%')
              AND ($directionFilter IS NULL OR me.Direction = $directionFilter)
              AND ($fromUtc IS NULL OR (me.TimestampUtc IS NOT NULL AND julianday(me.TimestampUtc) >= julianday($fromUtc)))
              AND ($toUtc IS NULL OR (me.TimestampUtc IS NOT NULL AND julianday(me.TimestampUtc) <= julianday($toUtc)))
              AND (
                    $targetId IS NULL
                    OR EXISTS (
                        SELECT 1
                        FROM MessageParticipantLinkRecord mpl
                        INNER JOIN TargetIdentifierLinkRecord til
                            ON til.CaseId = mpl.CaseId
                           AND til.IdentifierId = mpl.IdentifierId
                        INNER JOIN IdentifierRecord id
                            ON id.CaseId = til.CaseId
                           AND id.IdentifierId = til.IdentifierId
                        WHERE mpl.CaseId = me.CaseId
                          AND mpl.MessageEventId = me.MessageEventId
                          AND til.TargetId = $targetId
                          AND ($identifierTypeFilter IS NULL OR id.Type = $identifierTypeFilter)
                    )
              )
            ORDER BY me.TimestampUtc DESC
            LIMIT $maxRows;
            """;
        command.Parameters.AddWithValue("$caseId", request.CaseId);
        command.Parameters.AddWithValue("$query", request.Query!);
        command.Parameters.AddWithValue("$maxRows", request.MaxRows);
        AddOptionalStringParameter(command, "$platformFilter", request.PlatformFilter);
        AddOptionalStringParameter(command, "$senderFilter", request.SenderFilter);
        AddOptionalStringParameter(command, "$recipientFilter", request.RecipientFilter);
        AddOptionalStringParameter(command, "$identifierTypeFilter", request.IdentifierTypeFilter);
        AddOptionalStringParameter(command, "$directionFilter", request.DirectionFilter);
        AddOptionalGuidParameter(command, "$targetId", request.TargetId);
        AddOptionalDateParameter(command, "$fromUtc", request.FromUtc);
        AddOptionalDateParameter(command, "$toUtc", request.ToUtc);

        var rawHits = new List<MessageSearchHit>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            rawHits.Add(ReadHit(reader));
        }

        return rawHits
            .Skip(request.Skip)
            .Take(request.Take)
            .ToList();
    }

    private async Task<IReadOnlyList<MessageSearchHit>> SearchWithoutKeywordAsync(
        PreparedSearchRequest request,
        CancellationToken ct
    )
    {
        await using var connection = new SqliteConnection($"Data Source={_workspacePathProvider.WorkspaceDbPath}");
        await connection.OpenAsync(ct);

        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT
                me.MessageEventId,
                me.CaseId,
                me.EvidenceItemId,
                me.Platform,
                me.TimestampUtc,
                me.Sender,
                substr(COALESCE(me.Body, ''), 1, 280) AS Snippet,
                me.SourceLocator,
                me.Body,
                me.Recipients,
                mt.ThreadKey,
                ei.DisplayName,
                ei.StoredRelativePath
            FROM MessageEventRecord me
            LEFT JOIN MessageThreadRecord mt ON mt.ThreadId = me.ThreadId
            LEFT JOIN EvidenceItemRecord ei ON ei.EvidenceItemId = me.EvidenceItemId
            WHERE me.CaseId = $caseId
              AND ($platformFilter IS NULL OR LOWER(COALESCE(me.Platform, '')) = $platformFilter)
              AND ($senderFilter IS NULL OR LOWER(COALESCE(me.Sender, '')) LIKE '%' || $senderFilter || '%')
              AND ($recipientFilter IS NULL OR LOWER(COALESCE(me.Recipients, '')) LIKE '%' || $recipientFilter || '%')
              AND ($directionFilter IS NULL OR me.Direction = $directionFilter)
              AND ($fromUtc IS NULL OR (me.TimestampUtc IS NOT NULL AND julianday(me.TimestampUtc) >= julianday($fromUtc)))
              AND ($toUtc IS NULL OR (me.TimestampUtc IS NOT NULL AND julianday(me.TimestampUtc) <= julianday($toUtc)))
              AND (
                    $targetId IS NULL
                    OR EXISTS (
                        SELECT 1
                        FROM MessageParticipantLinkRecord mpl
                        INNER JOIN TargetIdentifierLinkRecord til
                            ON til.CaseId = mpl.CaseId
                           AND til.IdentifierId = mpl.IdentifierId
                        INNER JOIN IdentifierRecord id
                            ON id.CaseId = til.CaseId
                           AND id.IdentifierId = til.IdentifierId
                        WHERE mpl.CaseId = me.CaseId
                          AND mpl.MessageEventId = me.MessageEventId
                          AND til.TargetId = $targetId
                          AND ($identifierTypeFilter IS NULL OR id.Type = $identifierTypeFilter)
                    )
              )
            ORDER BY me.TimestampUtc DESC
            LIMIT $maxRows;
            """;
        command.Parameters.AddWithValue("$caseId", request.CaseId);
        command.Parameters.AddWithValue("$maxRows", request.MaxRows);
        AddOptionalStringParameter(command, "$platformFilter", request.PlatformFilter);
        AddOptionalStringParameter(command, "$senderFilter", request.SenderFilter);
        AddOptionalStringParameter(command, "$recipientFilter", request.RecipientFilter);
        AddOptionalStringParameter(command, "$identifierTypeFilter", request.IdentifierTypeFilter);
        AddOptionalStringParameter(command, "$directionFilter", request.DirectionFilter);
        AddOptionalGuidParameter(command, "$targetId", request.TargetId);
        AddOptionalDateParameter(command, "$fromUtc", request.FromUtc);
        AddOptionalDateParameter(command, "$toUtc", request.ToUtc);

        var rawHits = new List<MessageSearchHit>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            rawHits.Add(ReadHit(reader));
        }

        return rawHits
            .Skip(request.Skip)
            .Take(request.Take)
            .ToList();
    }

    private async Task<bool> TargetExistsAsync(
        SqliteConnection connection,
        Guid caseId,
        Guid targetId,
        CancellationToken ct
    )
    {
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT 1
            FROM TargetRecord
            WHERE CaseId = $caseId
              AND TargetId = $targetId
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("$caseId", caseId);
        command.Parameters.AddWithValue("$targetId", targetId);
        var exists = await command.ExecuteScalarAsync(ct);
        return exists is not null;
    }

    private async Task<IReadOnlyList<TargetPresenceIdentifierSummary>> GetPresenceByIdentifierAsync(
        SqliteConnection connection,
        Guid caseId,
        Guid targetId,
        string? identifierTypeFilter,
        DateTimeOffset? fromUtc,
        DateTimeOffset? toUtc,
        CancellationToken ct
    )
    {
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT
                id.IdentifierId,
                id.Type,
                id.ValueRaw,
                COUNT(DISTINCT me.MessageEventId) AS MatchCount,
                MAX(me.TimestampUtc) AS LastSeenUtc
            FROM TargetIdentifierLinkRecord til
            INNER JOIN IdentifierRecord id
                ON id.CaseId = til.CaseId
               AND id.IdentifierId = til.IdentifierId
            LEFT JOIN MessageParticipantLinkRecord mpl
                ON mpl.CaseId = til.CaseId
               AND mpl.IdentifierId = til.IdentifierId
            LEFT JOIN MessageEventRecord me
                ON me.CaseId = mpl.CaseId
               AND me.MessageEventId = mpl.MessageEventId
               AND ($fromUtc IS NULL OR (me.TimestampUtc IS NOT NULL AND julianday(me.TimestampUtc) >= julianday($fromUtc)))
               AND ($toUtc IS NULL OR (me.TimestampUtc IS NOT NULL AND julianday(me.TimestampUtc) <= julianday($toUtc)))
            WHERE til.CaseId = $caseId
              AND til.TargetId = $targetId
              AND ($identifierTypeFilter IS NULL OR id.Type = $identifierTypeFilter)
            GROUP BY id.IdentifierId, id.Type, id.ValueRaw
            ORDER BY MatchCount DESC, id.Type ASC, id.ValueRaw ASC;
            """;
        command.Parameters.AddWithValue("$caseId", caseId);
        command.Parameters.AddWithValue("$targetId", targetId);
        AddOptionalStringParameter(command, "$identifierTypeFilter", identifierTypeFilter);
        AddOptionalDateParameter(command, "$fromUtc", fromUtc);
        AddOptionalDateParameter(command, "$toUtc", toUtc);

        var rows = new List<TargetPresenceIdentifierSummary>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var identifierId = ReadGuid(reader.GetValue(0));
            var typeText = reader.IsDBNull(1) ? null : reader.GetString(1);
            var valueRaw = reader.IsDBNull(2) ? string.Empty : reader.GetString(2);
            var count = reader.IsDBNull(3) ? 0 : Convert.ToInt32(reader.GetValue(3), CultureInfo.InvariantCulture);
            var lastSeenUtc = reader.IsDBNull(4) ? null : TryParseDateTimeOffset(reader.GetValue(4));

            rows.Add(new TargetPresenceIdentifierSummary(
                identifierId,
                ParseIdentifierType(typeText),
                valueRaw,
                count,
                lastSeenUtc
            ));
        }

        return rows;
    }

    private async Task<(int Count, DateTimeOffset? LastSeenUtc)> GetPresenceTotalsAsync(
        SqliteConnection connection,
        Guid caseId,
        Guid targetId,
        string? identifierTypeFilter,
        DateTimeOffset? fromUtc,
        DateTimeOffset? toUtc,
        CancellationToken ct
    )
    {
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT
                COUNT(DISTINCT me.MessageEventId) AS MatchCount,
                MAX(me.TimestampUtc) AS LastSeenUtc
            FROM TargetIdentifierLinkRecord til
            INNER JOIN IdentifierRecord id
                ON id.CaseId = til.CaseId
               AND id.IdentifierId = til.IdentifierId
            LEFT JOIN MessageParticipantLinkRecord mpl
                ON mpl.CaseId = til.CaseId
               AND mpl.IdentifierId = til.IdentifierId
            LEFT JOIN MessageEventRecord me
                ON me.CaseId = mpl.CaseId
               AND me.MessageEventId = mpl.MessageEventId
               AND ($fromUtc IS NULL OR (me.TimestampUtc IS NOT NULL AND julianday(me.TimestampUtc) >= julianday($fromUtc)))
               AND ($toUtc IS NULL OR (me.TimestampUtc IS NOT NULL AND julianday(me.TimestampUtc) <= julianday($toUtc)))
            WHERE til.CaseId = $caseId
              AND til.TargetId = $targetId
              AND ($identifierTypeFilter IS NULL OR id.Type = $identifierTypeFilter);
            """;
        command.Parameters.AddWithValue("$caseId", caseId);
        command.Parameters.AddWithValue("$targetId", targetId);
        AddOptionalStringParameter(command, "$identifierTypeFilter", identifierTypeFilter);
        AddOptionalDateParameter(command, "$fromUtc", fromUtc);
        AddOptionalDateParameter(command, "$toUtc", toUtc);

        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
        {
            return (0, null);
        }

        var count = reader.IsDBNull(0) ? 0 : Convert.ToInt32(reader.GetValue(0), CultureInfo.InvariantCulture);
        var lastSeenUtc = reader.IsDBNull(1) ? null : TryParseDateTimeOffset(reader.GetValue(1));
        return (count, lastSeenUtc);
    }

    private static string? NormalizeQuery(string? value)
    {
        return string.IsNullOrWhiteSpace(value)
            ? null
            : value.Trim();
    }

    private static string? NormalizePlatformFilter(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        return string.Equals(value.Trim(), "All", StringComparison.OrdinalIgnoreCase)
            ? null
            : value.Trim().ToLowerInvariant();
    }

    private static string? NormalizeSubstringFilter(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim().ToLowerInvariant();
    }

    private static string? NormalizeDirectionFilter(MessageDirectionFilter directionFilter)
    {
        return directionFilter switch
        {
            MessageDirectionFilter.Incoming => "Incoming",
            MessageDirectionFilter.Outgoing => "Outgoing",
            _ => null
        };
    }

    private static (DateTimeOffset? FromUtc, DateTimeOffset? ToUtc) NormalizeDateRange(
        DateTimeOffset? fromUtc,
        DateTimeOffset? toUtc
    )
    {
        var normalizedFrom = fromUtc?.ToUniversalTime();
        var normalizedTo = toUtc?.ToUniversalTime();
        if (normalizedFrom.HasValue
            && normalizedTo.HasValue
            && normalizedFrom.Value > normalizedTo.Value)
        {
            (normalizedFrom, normalizedTo) = (normalizedTo, normalizedFrom);
        }

        return (normalizedFrom, normalizedTo);
    }

    private static void AddOptionalStringParameter(SqliteCommand command, string parameterName, string? value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = parameterName;
        parameter.Value = value is null ? DBNull.Value : value;
        command.Parameters.Add(parameter);
    }

    private static void AddOptionalGuidParameter(SqliteCommand command, string parameterName, Guid? value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = parameterName;
        parameter.Value = value.HasValue ? value.Value : DBNull.Value;
        command.Parameters.Add(parameter);
    }

    private static void AddOptionalDateParameter(SqliteCommand command, string parameterName, DateTimeOffset? value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = parameterName;
        parameter.Value = value.HasValue
            ? value.Value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture)
            : DBNull.Value;
        command.Parameters.Add(parameter);
    }

    private static MessageSearchHit ReadHit(SqliteDataReader reader)
    {
        var timestamp = reader.IsDBNull(4) ? null : TryParseDateTimeOffset(reader.GetValue(4));
        var hit = new MessageSearchHit(
            MessageEventId: ReadGuid(reader.GetValue(0)),
            CaseId: ReadGuid(reader.GetValue(1)),
            EvidenceItemId: ReadGuid(reader.GetValue(2)),
            Platform: reader.GetString(3),
            TimestampUtc: timestamp,
            Sender: reader.IsDBNull(5) ? null : reader.GetString(5),
            Snippet: reader.IsDBNull(6) ? null : reader.GetString(6),
            SourceLocator: reader.GetString(7)
        )
        {
            Body = reader.IsDBNull(8) ? null : reader.GetString(8),
            Recipients = reader.IsDBNull(9) ? null : reader.GetString(9),
            ThreadKey = reader.IsDBNull(10) ? null : reader.GetString(10),
            EvidenceDisplayName = reader.IsDBNull(11) ? null : reader.GetString(11),
            StoredRelativePath = reader.IsDBNull(12) ? null : reader.GetString(12)
        };

        return hit;
    }

    private static Guid ReadGuid(object value)
    {
        return value switch
        {
            Guid guid => guid,
            string text => Guid.Parse(text),
            byte[] bytes when bytes.Length == 16 => new Guid(bytes),
            _ => Guid.Parse(Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty)
        };
    }

    private static DateTimeOffset? TryParseDateTimeOffset(object value)
    {
        switch (value)
        {
            case DateTimeOffset dto:
                return dto;
            case DateTime dt:
                return new DateTimeOffset(dt);
            case string text when DateTimeOffset.TryParse(
                text,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal,
                out var parsed):
                return parsed.ToUniversalTime();
            default:
                return null;
        }
    }

    private static string ToIdentifierTypeText(TargetIdentifierType type)
    {
        return type.ToString();
    }

    private static TargetIdentifierType ParseIdentifierType(string? type)
    {
        return Enum.TryParse<TargetIdentifierType>(type, ignoreCase: true, out var parsed)
            ? parsed
            : TargetIdentifierType.Other;
    }

    private sealed record PreparedSearchRequest(
        Guid CaseId,
        string? Query,
        string? PlatformFilter,
        string? SenderFilter,
        string? RecipientFilter,
        Guid? TargetId,
        string? IdentifierTypeFilter,
        string? DirectionFilter,
        DateTimeOffset? FromUtc,
        DateTimeOffset? ToUtc,
        int Take,
        int Skip,
        int MaxRows,
        bool CanSearch
    )
    {
        public static PreparedSearchRequest Empty { get; } = new(
            Guid.Empty,
            Query: null,
            PlatformFilter: null,
            SenderFilter: null,
            RecipientFilter: null,
            TargetId: null,
            IdentifierTypeFilter: null,
            DirectionFilter: null,
            FromUtc: null,
            ToUtc: null,
            Take: 0,
            Skip: 0,
            MaxRows: 0,
            CanSearch: false
        );
    }
}
