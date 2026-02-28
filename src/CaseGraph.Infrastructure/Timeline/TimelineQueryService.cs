using CaseGraph.Core.Abstractions;
using CaseGraph.Core.Models;
using Microsoft.Data.Sqlite;
using System.Globalization;
using System.Text.Json;

namespace CaseGraph.Infrastructure.Timeline;

public sealed class TimelineQueryService
{
    private readonly IWorkspaceDatabaseInitializer _databaseInitializer;
    private readonly IWorkspacePathProvider _workspacePathProvider;
    private readonly IAuditLogService _auditLogService;

    public TimelineQueryService(
        IWorkspaceDatabaseInitializer databaseInitializer,
        IWorkspacePathProvider workspacePathProvider,
        IAuditLogService auditLogService
    )
    {
        _databaseInitializer = databaseInitializer;
        _workspacePathProvider = workspacePathProvider;
        _auditLogService = auditLogService;
    }

    public async Task<TimelineQueryPage> SearchAsync(
        TimelineQueryRequest request,
        CancellationToken ct
    )
    {
        await _databaseInitializer.EnsureInitializedAsync(ct);

        var prepared = TimelineQueryRequestPrepared.From(request);
        if (!prepared.CanSearch)
        {
            return TimelineQueryPage.Empty;
        }

        TimelineQueryPage page;
        if (prepared.QueryText is null)
        {
            page = await SearchWithoutKeywordAsync(prepared, ct);
        }
        else
        {
            try
            {
                page = await SearchWithFtsAsync(prepared, ct);
            }
            catch (SqliteException)
            {
                page = await SearchWithLikeAsync(prepared, ct);
            }
        }

        await WriteAuditAsync(prepared, page.TotalCount, ct);
        return page;
    }

    private async Task<TimelineQueryPage> SearchWithFtsAsync(
        TimelineQueryRequestPrepared request,
        CancellationToken ct
    )
    {
        await using var connection = await OpenConnectionAsync(ct);
        var totalCount = await ExecuteCountAsync(connection, request, useFts: true, ct);
        var rows = await ExecuteRowsAsync(connection, request, useFts: true, ct);
        return new TimelineQueryPage(rows, totalCount);
    }

    private async Task<TimelineQueryPage> SearchWithLikeAsync(
        TimelineQueryRequestPrepared request,
        CancellationToken ct
    )
    {
        await using var connection = await OpenConnectionAsync(ct);
        var totalCount = await ExecuteCountAsync(connection, request, useFts: false, ct);
        var rows = await ExecuteRowsAsync(connection, request, useFts: false, ct);
        return new TimelineQueryPage(rows, totalCount);
    }

    private async Task<TimelineQueryPage> SearchWithoutKeywordAsync(
        TimelineQueryRequestPrepared request,
        CancellationToken ct
    )
    {
        await using var connection = await OpenConnectionAsync(ct);
        var totalCount = await ExecuteCountWithoutKeywordAsync(connection, request, ct);
        var rows = await ExecuteRowsWithoutKeywordAsync(connection, request, ct);
        return new TimelineQueryPage(rows, totalCount);
    }

    private async Task<SqliteConnection> OpenConnectionAsync(CancellationToken ct)
    {
        var connection = new SqliteConnection(
            $"Data Source={_workspacePathProvider.WorkspaceDbPath}"
        );
        await connection.OpenAsync(ct);
        return connection;
    }

    private static async Task<int> ExecuteCountAsync(
        SqliteConnection connection,
        TimelineQueryRequestPrepared request,
        bool useFts,
        CancellationToken ct
    )
    {
        await using var command = connection.CreateCommand();
        command.CommandText = useFts
            ? GetFtsCountSql(request.DirectionMode)
            : GetLikeCountSql(request.DirectionMode);
        AddCommonParameters(command, request);
        command.Parameters.AddWithValue("$query", request.QueryText!);

        var value = await command.ExecuteScalarAsync(ct);
        return value is null or DBNull
            ? 0
            : Convert.ToInt32(value, CultureInfo.InvariantCulture);
    }

    private static async Task<int> ExecuteCountWithoutKeywordAsync(
        SqliteConnection connection,
        TimelineQueryRequestPrepared request,
        CancellationToken ct
    )
    {
        await using var command = connection.CreateCommand();
        command.CommandText = GetNoKeywordCountSql(request.DirectionMode);
        AddCommonParameters(command, request);

        var value = await command.ExecuteScalarAsync(ct);
        return value is null or DBNull
            ? 0
            : Convert.ToInt32(value, CultureInfo.InvariantCulture);
    }

    private static async Task<IReadOnlyList<TimelineRowDto>> ExecuteRowsAsync(
        SqliteConnection connection,
        TimelineQueryRequestPrepared request,
        bool useFts,
        CancellationToken ct
    )
    {
        await using var command = connection.CreateCommand();
        command.CommandText = useFts
            ? GetFtsRowsSql(request.DirectionMode)
            : GetLikeRowsSql(request.DirectionMode);
        AddCommonParameters(command, request);
        command.Parameters.AddWithValue("$query", request.QueryText!);
        command.Parameters.AddWithValue("$take", request.Take);
        command.Parameters.AddWithValue("$skip", request.Skip);

        var rows = new List<TimelineRowDto>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            rows.Add(ReadRow(reader));
        }

        return rows;
    }

    private static async Task<IReadOnlyList<TimelineRowDto>> ExecuteRowsWithoutKeywordAsync(
        SqliteConnection connection,
        TimelineQueryRequestPrepared request,
        CancellationToken ct
    )
    {
        await using var command = connection.CreateCommand();
        command.CommandText = GetNoKeywordRowsSql(request.DirectionMode);
        AddCommonParameters(command, request);
        command.Parameters.AddWithValue("$take", request.Take);
        command.Parameters.AddWithValue("$skip", request.Skip);

        var rows = new List<TimelineRowDto>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            rows.Add(ReadRow(reader));
        }

        return rows;
    }

    private async Task WriteAuditAsync(
        TimelineQueryRequestPrepared request,
        int totalCount,
        CancellationToken ct
    )
    {
        var payload = JsonSerializer.Serialize(new
        {
            request.CorrelationId,
            request.QueryText,
            request.DirectionMode,
            request.TargetId,
            request.GlobalEntityId,
            request.FromUtc,
            request.ToUtc,
            request.Take,
            request.Skip,
            ResultCount = totalCount
        });

        await _auditLogService.AddAsync(
            new AuditEvent
            {
                TimestampUtc = DateTimeOffset.UtcNow,
                Operator = Environment.UserName,
                ActionType = "TimelineSearchExecuted",
                CaseId = request.CaseId,
                Summary = $"Timeline search returned {totalCount:0} row(s).",
                JsonPayload = payload
            },
            ct
        );
    }

    private static void AddCommonParameters(
        SqliteCommand command,
        TimelineQueryRequestPrepared request
    )
    {
        command.Parameters.AddWithValue("$caseId", request.CaseId);
        AddOptionalGuidParameter(command, "$targetId", request.TargetId);
        AddOptionalGuidParameter(command, "$globalEntityId", request.GlobalEntityId);
        AddOptionalDateParameter(command, "$fromUtc", request.FromUtc);
        AddOptionalDateParameter(command, "$toUtc", request.ToUtc);
        if (!string.IsNullOrWhiteSpace(request.DirectionValue))
        {
            command.Parameters.AddWithValue("$direction", request.DirectionValue!);
        }
    }

    private static void AddOptionalGuidParameter(
        SqliteCommand command,
        string parameterName,
        Guid? value
    )
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = parameterName;
        parameter.Value = value.HasValue ? value.Value : DBNull.Value;
        command.Parameters.Add(parameter);
    }

    private static void AddOptionalDateParameter(
        SqliteCommand command,
        string parameterName,
        DateTimeOffset? value
    )
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = parameterName;
        parameter.Value = value.HasValue
            ? value.Value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture)
            : DBNull.Value;
        command.Parameters.Add(parameter);
    }

    private static TimelineRowDto ReadRow(SqliteDataReader reader)
    {
        var timestampUtc = reader.IsDBNull(3) ? null : TryParseDateTimeOffset(reader.GetValue(3));
        var senderRaw = reader.IsDBNull(5) ? null : reader.GetString(5);
        var recipientsRaw = reader.IsDBNull(6) ? null : reader.GetString(6);
        var resolvedSender = NormalizeGroupConcat(
            reader.IsDBNull(14) ? null : reader.GetString(14)
        );
        var resolvedRecipients = NormalizeGroupConcat(
            reader.IsDBNull(15) ? null : reader.GetString(15)
        );
        var senderDisplay = !string.IsNullOrWhiteSpace(resolvedSender)
            ? resolvedSender
            : senderRaw;
        var recipientsDisplay = !string.IsNullOrWhiteSpace(resolvedRecipients)
            ? resolvedRecipients
            : recipientsRaw;
        var participantsSummary = BuildParticipantsSummary(senderDisplay, recipientsDisplay);

        return new TimelineRowDto(
            MessageEventId: ReadGuid(reader.GetValue(0)),
            CaseId: ReadGuid(reader.GetValue(1)),
            SourceEvidenceItemId: ReadGuid(reader.GetValue(2)),
            EventType: "Message",
            TimestampUtc: timestampUtc,
            Direction: reader.IsDBNull(4) ? "Unknown" : reader.GetString(4),
            ParticipantsSummary: participantsSummary,
            Preview: BuildPreview(
                snippet: reader.IsDBNull(7) ? null : reader.GetString(7),
                body: reader.IsDBNull(13) ? null : reader.GetString(13)
            ),
            SourceLocator: reader.GetString(8),
            IngestModuleVersion: reader.IsDBNull(9) ? string.Empty : reader.GetString(9)
        )
        {
            EvidenceDisplayName = reader.IsDBNull(10) ? null : reader.GetString(10),
            StoredRelativePath = reader.IsDBNull(11) ? null : reader.GetString(11),
            ThreadKey = reader.IsDBNull(12) ? null : reader.GetString(12),
            Body = reader.IsDBNull(13) ? null : reader.GetString(13),
            SenderRaw = senderRaw,
            RecipientsRaw = recipientsRaw,
            SenderDisplay = senderDisplay,
            RecipientsDisplay = recipientsDisplay,
            Platform = reader.IsDBNull(16) ? string.Empty : reader.GetString(16)
        };
    }

    private static string BuildPreview(string? snippet, string? body)
    {
        var value = !string.IsNullOrWhiteSpace(snippet)
            ? snippet
            : body;
        if (string.IsNullOrWhiteSpace(value))
        {
            return "(no preview)";
        }

        var trimmed = value.Trim();
        return trimmed.Length <= 240
            ? trimmed
            : trimmed[..237] + "...";
    }

    private static string BuildParticipantsSummary(string? sender, string? recipients)
    {
        var left = string.IsNullOrWhiteSpace(sender) ? "(unknown sender)" : sender.Trim();
        var right = string.IsNullOrWhiteSpace(recipients)
            ? "(unknown recipients)"
            : recipients.Trim();
        return $"{left} -> {right}";
    }

    private static string? NormalizeGroupConcat(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        return string.Join(
            ", ",
            value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Distinct(StringComparer.OrdinalIgnoreCase)
        );
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
                return dto.ToUniversalTime();
            case DateTime dt:
                return new DateTimeOffset(dt).ToUniversalTime();
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

    private static string GetFtsCountSql(TimelineDirectionMode directionMode)
    {
        return
            $$"""
            SELECT COUNT(1)
            FROM MessageEventFts
            INNER JOIN MessageEventRecord me ON me.MessageEventId = MessageEventFts.MessageEventId
            WHERE me.CaseId = $caseId
              AND MessageEventFts MATCH $query
              {{GetSharedWhereSql(directionMode)}};
            """;
    }

    private static string GetLikeCountSql(TimelineDirectionMode directionMode)
    {
        return
            $$"""
            SELECT COUNT(1)
            FROM MessageEventRecord me
            WHERE me.CaseId = $caseId
              AND (
                    LOWER(COALESCE(me.Body, '')) LIKE '%' || $query || '%'
                 OR LOWER(COALESCE(me.Sender, '')) LIKE '%' || $query || '%'
                 OR LOWER(COALESCE(me.Recipients, '')) LIKE '%' || $query || '%'
                  )
              {{GetSharedWhereSql(directionMode)}};
            """;
    }

    private static string GetNoKeywordCountSql(TimelineDirectionMode directionMode)
    {
        return
            $$"""
            SELECT COUNT(1)
            FROM MessageEventRecord me
            WHERE me.CaseId = $caseId
              {{GetSharedWhereSql(directionMode)}};
            """;
    }

    private static string GetFtsRowsSql(TimelineDirectionMode directionMode)
    {
        return
            $$"""
            SELECT
                me.MessageEventId,
                me.CaseId,
                me.EvidenceItemId,
                me.TimestampUtc,
                me.Direction,
                me.Sender,
                me.Recipients,
                snippet(MessageEventFts, 5, '[', ']', '...', 18) AS Preview,
                me.SourceLocator,
                me.IngestModuleVersion,
                ei.DisplayName,
                ei.StoredRelativePath,
                mt.ThreadKey,
                me.Body,
                (
                    SELECT group_concat(DISTINCT COALESCE(NULLIF(pe.DisplayName, ''), tr.DisplayName))
                    FROM TargetMessagePresenceRecord tmp
                    INNER JOIN TargetRecord tr
                        ON tr.CaseId = tmp.CaseId
                       AND tr.TargetId = tmp.TargetId
                    LEFT JOIN PersonEntity pe
                        ON pe.GlobalEntityId = tr.GlobalEntityId
                    WHERE tmp.CaseId = me.CaseId
                      AND tmp.MessageEventId = me.MessageEventId
                      AND LOWER(COALESCE(tmp.Role, '')) = 'sender'
                ) AS SenderResolved,
                (
                    SELECT group_concat(DISTINCT COALESCE(NULLIF(pe.DisplayName, ''), tr.DisplayName))
                    FROM TargetMessagePresenceRecord tmp
                    INNER JOIN TargetRecord tr
                        ON tr.CaseId = tmp.CaseId
                       AND tr.TargetId = tmp.TargetId
                    LEFT JOIN PersonEntity pe
                        ON pe.GlobalEntityId = tr.GlobalEntityId
                    WHERE tmp.CaseId = me.CaseId
                      AND tmp.MessageEventId = me.MessageEventId
                      AND LOWER(COALESCE(tmp.Role, '')) = 'recipient'
                ) AS RecipientsResolved,
                me.Platform
            FROM MessageEventFts
            INNER JOIN MessageEventRecord me ON me.MessageEventId = MessageEventFts.MessageEventId
            LEFT JOIN MessageThreadRecord mt ON mt.ThreadId = me.ThreadId
            LEFT JOIN EvidenceItemRecord ei ON ei.EvidenceItemId = me.EvidenceItemId
            WHERE me.CaseId = $caseId
              AND MessageEventFts MATCH $query
              {{GetSharedWhereSql(directionMode)}}
            ORDER BY
                CASE WHEN me.TimestampUtc IS NULL THEN 1 ELSE 0 END,
                me.TimestampUtc DESC,
                me.MessageEventId DESC
            LIMIT $take OFFSET $skip;
            """;
    }

    private static string GetLikeRowsSql(TimelineDirectionMode directionMode)
    {
        return
            $$"""
            SELECT
                me.MessageEventId,
                me.CaseId,
                me.EvidenceItemId,
                me.TimestampUtc,
                me.Direction,
                me.Sender,
                me.Recipients,
                substr(COALESCE(me.Body, ''), 1, 240) AS Preview,
                me.SourceLocator,
                me.IngestModuleVersion,
                ei.DisplayName,
                ei.StoredRelativePath,
                mt.ThreadKey,
                me.Body,
                (
                    SELECT group_concat(DISTINCT COALESCE(NULLIF(pe.DisplayName, ''), tr.DisplayName))
                    FROM TargetMessagePresenceRecord tmp
                    INNER JOIN TargetRecord tr
                        ON tr.CaseId = tmp.CaseId
                       AND tr.TargetId = tmp.TargetId
                    LEFT JOIN PersonEntity pe
                        ON pe.GlobalEntityId = tr.GlobalEntityId
                    WHERE tmp.CaseId = me.CaseId
                      AND tmp.MessageEventId = me.MessageEventId
                      AND LOWER(COALESCE(tmp.Role, '')) = 'sender'
                ) AS SenderResolved,
                (
                    SELECT group_concat(DISTINCT COALESCE(NULLIF(pe.DisplayName, ''), tr.DisplayName))
                    FROM TargetMessagePresenceRecord tmp
                    INNER JOIN TargetRecord tr
                        ON tr.CaseId = tmp.CaseId
                       AND tr.TargetId = tmp.TargetId
                    LEFT JOIN PersonEntity pe
                        ON pe.GlobalEntityId = tr.GlobalEntityId
                    WHERE tmp.CaseId = me.CaseId
                      AND tmp.MessageEventId = me.MessageEventId
                      AND LOWER(COALESCE(tmp.Role, '')) = 'recipient'
                ) AS RecipientsResolved,
                me.Platform
            FROM MessageEventRecord me
            LEFT JOIN MessageThreadRecord mt ON mt.ThreadId = me.ThreadId
            LEFT JOIN EvidenceItemRecord ei ON ei.EvidenceItemId = me.EvidenceItemId
            WHERE me.CaseId = $caseId
              AND (
                    LOWER(COALESCE(me.Body, '')) LIKE '%' || $query || '%'
                 OR LOWER(COALESCE(me.Sender, '')) LIKE '%' || $query || '%'
                 OR LOWER(COALESCE(me.Recipients, '')) LIKE '%' || $query || '%'
                  )
              {{GetSharedWhereSql(directionMode)}}
            ORDER BY
                CASE WHEN me.TimestampUtc IS NULL THEN 1 ELSE 0 END,
                me.TimestampUtc DESC,
                me.MessageEventId DESC
            LIMIT $take OFFSET $skip;
            """;
    }

    private static string GetNoKeywordRowsSql(TimelineDirectionMode directionMode)
    {
        return
            $$"""
            SELECT
                me.MessageEventId,
                me.CaseId,
                me.EvidenceItemId,
                me.TimestampUtc,
                me.Direction,
                me.Sender,
                me.Recipients,
                substr(COALESCE(me.Body, ''), 1, 240) AS Preview,
                me.SourceLocator,
                me.IngestModuleVersion,
                ei.DisplayName,
                ei.StoredRelativePath,
                mt.ThreadKey,
                me.Body,
                (
                    SELECT group_concat(DISTINCT COALESCE(NULLIF(pe.DisplayName, ''), tr.DisplayName))
                    FROM TargetMessagePresenceRecord tmp
                    INNER JOIN TargetRecord tr
                        ON tr.CaseId = tmp.CaseId
                       AND tr.TargetId = tmp.TargetId
                    LEFT JOIN PersonEntity pe
                        ON pe.GlobalEntityId = tr.GlobalEntityId
                    WHERE tmp.CaseId = me.CaseId
                      AND tmp.MessageEventId = me.MessageEventId
                      AND LOWER(COALESCE(tmp.Role, '')) = 'sender'
                ) AS SenderResolved,
                (
                    SELECT group_concat(DISTINCT COALESCE(NULLIF(pe.DisplayName, ''), tr.DisplayName))
                    FROM TargetMessagePresenceRecord tmp
                    INNER JOIN TargetRecord tr
                        ON tr.CaseId = tmp.CaseId
                       AND tr.TargetId = tmp.TargetId
                    LEFT JOIN PersonEntity pe
                        ON pe.GlobalEntityId = tr.GlobalEntityId
                    WHERE tmp.CaseId = me.CaseId
                      AND tmp.MessageEventId = me.MessageEventId
                      AND LOWER(COALESCE(tmp.Role, '')) = 'recipient'
                ) AS RecipientsResolved,
                me.Platform
            FROM MessageEventRecord me
            LEFT JOIN MessageThreadRecord mt ON mt.ThreadId = me.ThreadId
            LEFT JOIN EvidenceItemRecord ei ON ei.EvidenceItemId = me.EvidenceItemId
            WHERE me.CaseId = $caseId
              {{GetSharedWhereSql(directionMode)}}
            ORDER BY
                CASE WHEN me.TimestampUtc IS NULL THEN 1 ELSE 0 END,
                me.TimestampUtc DESC,
                me.MessageEventId DESC
            LIMIT $take OFFSET $skip;
            """;
    }

    private static string GetSharedWhereSql(TimelineDirectionMode directionMode)
    {
        return
            $$"""
              AND ($fromUtc IS NULL OR (me.TimestampUtc IS NOT NULL AND julianday(me.TimestampUtc) >= julianday($fromUtc)))
              AND ($toUtc IS NULL OR (me.TimestampUtc IS NOT NULL AND julianday(me.TimestampUtc) <= julianday($toUtc)))
              AND (
                    $targetId IS NULL
                    OR EXISTS (
                        SELECT 1
                        FROM TargetMessagePresenceRecord tmp
                        WHERE tmp.CaseId = me.CaseId
                          AND tmp.MessageEventId = me.MessageEventId
                          AND tmp.TargetId = $targetId
                    )
                  )
              AND (
                    $globalEntityId IS NULL
                    OR EXISTS (
                        SELECT 1
                        FROM TargetMessagePresenceRecord tmp
                        INNER JOIN TargetRecord tr
                            ON tr.CaseId = tmp.CaseId
                           AND tr.TargetId = tmp.TargetId
                        WHERE tmp.CaseId = me.CaseId
                          AND tmp.MessageEventId = me.MessageEventId
                          AND tr.GlobalEntityId = $globalEntityId
                    )
                  )
            {{GetDirectionFilterSql(directionMode)}}
            """;
    }

    private static string GetDirectionFilterSql(TimelineDirectionMode directionMode)
    {
        return directionMode switch
        {
            TimelineDirectionMode.Incoming or TimelineDirectionMode.Outgoing =>
                "  AND LOWER(COALESCE(me.Direction, '')) = $direction",
            TimelineDirectionMode.Unknown =>
                "  AND (me.Direction IS NULL OR TRIM(me.Direction) = '' OR LOWER(TRIM(me.Direction)) = 'unknown')",
            _ => string.Empty
        };
    }

    private sealed record TimelineQueryRequestPrepared(
        Guid CaseId,
        string? QueryText,
        Guid? TargetId,
        Guid? GlobalEntityId,
        TimelineDirectionMode DirectionMode,
        string? DirectionValue,
        DateTimeOffset? FromUtc,
        DateTimeOffset? ToUtc,
        int Take,
        int Skip,
        string CorrelationId,
        bool CanSearch
    )
    {
        public static TimelineQueryRequestPrepared From(TimelineQueryRequest request)
        {
            var caseId = request.CaseId;
            if (caseId == Guid.Empty)
            {
                return new TimelineQueryRequestPrepared(
                    Guid.Empty,
                    QueryText: null,
                    TargetId: null,
                    GlobalEntityId: null,
                    DirectionMode: TimelineDirectionMode.Any,
                    DirectionValue: null,
                    FromUtc: null,
                    ToUtc: null,
                    Take: 0,
                    Skip: 0,
                    CorrelationId: request.CorrelationId,
                    CanSearch: false
                );
            }

            var normalizedQuery = string.IsNullOrWhiteSpace(request.QueryText)
                ? null
                : request.QueryText.Trim();
            var normalizedTargetId = request.TargetId.GetValueOrDefault() == Guid.Empty
                ? null
                : request.TargetId;
            var normalizedGlobalEntityId = request.GlobalEntityId.GetValueOrDefault() == Guid.Empty
                ? null
                : request.GlobalEntityId;
            var (fromUtc, toUtc) = NormalizeDateRange(request.FromUtc, request.ToUtc);
            var directionMode = ParseDirectionMode(request.Direction);
            var directionValue = directionMode switch
            {
                TimelineDirectionMode.Incoming => "incoming",
                TimelineDirectionMode.Outgoing => "outgoing",
                _ => null
            };

            return new TimelineQueryRequestPrepared(
                caseId,
                normalizedQuery is null ? null : normalizedQuery.ToLowerInvariant(),
                normalizedTargetId,
                normalizedGlobalEntityId,
                directionMode,
                directionValue,
                fromUtc,
                toUtc,
                Take: Math.Clamp(request.Take, 1, 500),
                Skip: Math.Max(0, request.Skip),
                CorrelationId: string.IsNullOrWhiteSpace(request.CorrelationId)
                    ? Guid.NewGuid().ToString("N")
                    : request.CorrelationId.Trim(),
                CanSearch: true
            );
        }

        private static TimelineDirectionMode ParseDirectionMode(string? direction)
        {
            if (string.IsNullOrWhiteSpace(direction))
            {
                return TimelineDirectionMode.Any;
            }

            return direction.Trim().ToLowerInvariant() switch
            {
                "incoming" => TimelineDirectionMode.Incoming,
                "outgoing" => TimelineDirectionMode.Outgoing,
                "unknown" => TimelineDirectionMode.Unknown,
                _ => TimelineDirectionMode.Any
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
    }
}

public sealed record TimelineQueryRequest(
    Guid CaseId,
    string? QueryText,
    Guid? TargetId,
    Guid? GlobalEntityId,
    string? Direction,
    DateTimeOffset? FromUtc,
    DateTimeOffset? ToUtc,
    int Take,
    int Skip,
    string CorrelationId
);

public sealed record TimelineQueryPage(IReadOnlyList<TimelineRowDto> Rows, int TotalCount)
{
    public static TimelineQueryPage Empty { get; } = new(Array.Empty<TimelineRowDto>(), 0);
}

public enum TimelineDirectionMode
{
    Any,
    Incoming,
    Outgoing,
    Unknown
}
