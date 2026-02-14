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

    public async Task<IReadOnlyList<MessageSearchHit>> SearchAsync(
        Guid caseId,
        string query,
        string? platformFilter,
        int take,
        int skip,
        CancellationToken ct
    )
    {
        await _databaseInitializer.EnsureInitializedAsync(ct);

        if (caseId == Guid.Empty || string.IsNullOrWhiteSpace(query))
        {
            return Array.Empty<MessageSearchHit>();
        }

        var boundedTake = Math.Clamp(take, 1, 200);
        var boundedSkip = Math.Max(0, skip);
        var normalizedPlatformFilter = NormalizePlatformFilter(platformFilter);

        try
        {
            return await SearchWithFtsAsync(
                caseId,
                query.Trim(),
                normalizedPlatformFilter,
                boundedTake,
                boundedSkip,
                ct
            );
        }
        catch (SqliteException)
        {
            return await SearchWithLikeFallbackAsync(
                caseId,
                query.Trim(),
                normalizedPlatformFilter,
                boundedTake,
                boundedSkip,
                ct
            );
        }
    }

    private async Task<IReadOnlyList<MessageSearchHit>> SearchWithFtsAsync(
        Guid caseId,
        string query,
        string? platformFilter,
        int take,
        int skip,
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
            WHERE MessageEventFts MATCH $query
            ORDER BY bm25(MessageEventFts), me.TimestampUtc DESC
            LIMIT $maxRows;
            """;
        command.Parameters.AddWithValue("$query", query);
        command.Parameters.AddWithValue("$maxRows", Math.Clamp(take + skip + 500, 50, 2000));

        var rawHits = new List<MessageSearchHit>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            rawHits.Add(ReadHit(reader));
        }

        return rawHits
            .Where(hit => hit.CaseId == caseId)
            .Where(hit => string.IsNullOrWhiteSpace(platformFilter)
                || string.Equals(hit.Platform, platformFilter, StringComparison.OrdinalIgnoreCase))
            .Skip(skip)
            .Take(take)
            .ToList();
    }

    private async Task<IReadOnlyList<MessageSearchHit>> SearchWithLikeFallbackAsync(
        Guid caseId,
        string query,
        string? platformFilter,
        int take,
        int skip,
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
            WHERE (
                    COALESCE(me.Body, '') LIKE '%' || $query || '%'
                 OR COALESCE(me.Sender, '') LIKE '%' || $query || '%'
                 OR COALESCE(me.Recipients, '') LIKE '%' || $query || '%'
                  )
            ORDER BY me.TimestampUtc DESC
            LIMIT $maxRows;
            """;
        command.Parameters.AddWithValue("$query", query);
        command.Parameters.AddWithValue("$maxRows", Math.Clamp(take + skip + 500, 50, 2000));

        var rawHits = new List<MessageSearchHit>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            rawHits.Add(ReadHit(reader));
        }

        return rawHits
            .Where(hit => hit.CaseId == caseId)
            .Where(hit => string.IsNullOrWhiteSpace(platformFilter)
                || string.Equals(hit.Platform, platformFilter, StringComparison.OrdinalIgnoreCase))
            .Skip(skip)
            .Take(take)
            .ToList();
    }

    private static string? NormalizePlatformFilter(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        return string.Equals(value.Trim(), "All", StringComparison.OrdinalIgnoreCase)
            ? null
            : value.Trim();
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
            case string text when DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed):
                return parsed.ToUniversalTime();
            default:
                return null;
        }
    }
}
