using CaseGraph.Core.Abstractions;
using CaseGraph.Core.Models;
using CaseGraph.Infrastructure.Diagnostics;
using CaseGraph.Infrastructure.Locations;
using CaseGraph.Infrastructure.Timeline;
using Microsoft.Data.Sqlite;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;

namespace CaseGraph.Infrastructure.IncidentWindow;

public sealed class IncidentWindowQueryService
{
    private const int DefaultPageSize = 100;
    private const int MaxPageSize = 500;
    private const double DefaultCoLocationDistanceMeters = 100d;
    private const int DefaultCoLocationTimeWindowMinutes = 10;

    private readonly IWorkspaceDatabaseInitializer _databaseInitializer;
    private readonly IWorkspacePathProvider _workspacePathProvider;
    private readonly IAuditLogService _auditLogService;
    private readonly IClock _clock;
    private readonly IPerformanceInstrumentation _performanceInstrumentation;

    public IncidentWindowQueryService(
        IWorkspaceDatabaseInitializer databaseInitializer,
        IWorkspacePathProvider workspacePathProvider,
        IAuditLogService auditLogService,
        IClock clock,
        IPerformanceInstrumentation? performanceInstrumentation = null
    )
    {
        _databaseInitializer = databaseInitializer;
        _workspacePathProvider = workspacePathProvider;
        _auditLogService = auditLogService;
        _clock = clock;
        _performanceInstrumentation = performanceInstrumentation
            ?? new PerformanceInstrumentation(new PerformanceBudgetOptions(), TimeProvider.System);
    }

    public async Task<IncidentWindowQueryResult> ExecuteAsync(
        IncidentWindowQueryRequest request,
        CancellationToken ct
    )
    {
        await _databaseInitializer.EnsureInitializedAsync(ct);

        var prepared = PreparedRequest.From(request);
        if (!prepared.CanSearch)
        {
            return IncidentWindowQueryResult.Empty;
        }

        return await _performanceInstrumentation.TrackAsync(
            new PerformanceOperationContext(
                PerformanceOperationKinds.FeatureQuery,
                prepared.WriteAuditEvent ? "Run" : "PageLoad",
                FeatureName: "IncidentWindow",
                CaseId: prepared.CaseId,
                CorrelationId: prepared.CorrelationId,
                Fields: new Dictionary<string, object?>
                {
                    ["radiusEnabled"] = prepared.RadiusEnabled,
                    ["includeCoLocationCandidates"] = prepared.IncludeCoLocationCandidates
                }
            ),
            async innerCt =>
            {
                await using var connection = await OpenConnectionAsync(innerCt);
                var comms = await QueryCommsAsync(connection, prepared, innerCt);
                var geo = await QueryGeoAsync(connection, prepared, innerCt);
                var coLocation = prepared.ShouldQueryCoLocation
                    ? await QueryCoLocationAsync(connection, prepared, innerCt)
                    : IncidentWindowQueryPage<IncidentWindowCoLocationCandidateDto>.Empty;

                var result = new IncidentWindowQueryResult(comms, geo, coLocation);
                if (prepared.WriteAuditEvent)
                {
                    await WriteAuditAsync(prepared, result, innerCt);
                }

                return result;
            },
            ct
        );
    }

    private async Task<SqliteConnection> OpenConnectionAsync(CancellationToken ct)
    {
        var connection = new SqliteConnection($"Data Source={_workspacePathProvider.WorkspaceDbPath}");
        await connection.OpenAsync(ct);
        return connection;
    }

    private static async Task<IncidentWindowQueryPage<TimelineRowDto>> QueryCommsAsync(
        SqliteConnection connection,
        PreparedRequest request,
        CancellationToken ct
    )
    {
        var subjectWhereSql = GetCommsSubjectWhereSql(request);

        await using var countCommand = connection.CreateCommand();
        countCommand.CommandText = GetCommsCountSql(subjectWhereSql);
        AddCommonCommsParameters(countCommand, request);
        var countValue = await countCommand.ExecuteScalarAsync(ct);
        var totalCount = countValue is null or DBNull
            ? 0
            : Convert.ToInt32(countValue, CultureInfo.InvariantCulture);

        await using var rowsCommand = connection.CreateCommand();
        rowsCommand.CommandText = GetCommsRowsSql(subjectWhereSql);
        AddCommonCommsParameters(rowsCommand, request);
        rowsCommand.Parameters.AddWithValue("$take", request.CommsTake);
        rowsCommand.Parameters.AddWithValue("$skip", request.CommsSkip);

        var rows = new List<TimelineRowDto>(request.CommsTake);
        await using var reader = await rowsCommand.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            ct.ThrowIfCancellationRequested();
            if (TryReadCommsRow(reader, out var row))
            {
                rows.Add(row);
            }
        }

        return new IncidentWindowQueryPage<TimelineRowDto>(rows, totalCount);
    }

    private static Task<IncidentWindowQueryPage<LocationRowDto>> QueryGeoAsync(
        SqliteConnection connection,
        PreparedRequest request,
        CancellationToken ct
    )
    {
        return request.RadiusEnabled
            ? QueryGeoWithRadiusAsync(connection, request, ct)
            : QueryGeoWithoutRadiusAsync(connection, request, ct);
    }

    private static async Task<IncidentWindowQueryPage<LocationRowDto>> QueryGeoWithoutRadiusAsync(
        SqliteConnection connection,
        PreparedRequest request,
        CancellationToken ct
    )
    {
        var subjectWhereSql = GetGeoSubjectWhereSql(request);

        await using var countCommand = connection.CreateCommand();
        countCommand.CommandText = GetGeoCountSql(subjectWhereSql, bboxWhereSql: string.Empty);
        AddCommonGeoParameters(countCommand, request);
        var countValue = await countCommand.ExecuteScalarAsync(ct);
        var totalCount = countValue is null or DBNull
            ? 0
            : Convert.ToInt32(countValue, CultureInfo.InvariantCulture);

        await using var rowsCommand = connection.CreateCommand();
        rowsCommand.CommandText = GetGeoRowsSql(subjectWhereSql, bboxWhereSql: string.Empty, includeLimit: true);
        AddCommonGeoParameters(rowsCommand, request);
        rowsCommand.Parameters.AddWithValue("$take", request.GeoTake);
        rowsCommand.Parameters.AddWithValue("$skip", request.GeoSkip);

        var rows = new List<LocationRowDto>(request.GeoTake);
        await using var reader = await rowsCommand.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            ct.ThrowIfCancellationRequested();
            if (TryReadLocationRow(reader, out var row))
            {
                rows.Add(row);
            }
        }

        return new IncidentWindowQueryPage<LocationRowDto>(rows, totalCount);
    }

    private static async Task<IncidentWindowQueryPage<LocationRowDto>> QueryGeoWithRadiusAsync(
        SqliteConnection connection,
        PreparedRequest request,
        CancellationToken ct
    )
    {
        var subjectWhereSql = GetGeoSubjectWhereSql(request);
        var bboxWhereSql = GetBoundingBoxWhereSql("lo", request.BoundingBox);

        await using var command = connection.CreateCommand();
        command.CommandText = GetGeoRowsSql(subjectWhereSql, bboxWhereSql, includeLimit: false);
        AddCommonGeoParameters(command, request);
        AddBoundingBoxParameters(command, request.BoundingBox);

        var rows = new List<LocationRowDto>(request.GeoTake);
        var totalCount = 0;
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            ct.ThrowIfCancellationRequested();
            if (!TryReadLocationRow(reader, out var row))
            {
                continue;
            }

            var distance = GeoMath.HaversineDistanceMeters(
                request.CenterLatitude!.Value,
                request.CenterLongitude!.Value,
                row.Latitude,
                row.Longitude
            );
            if (distance > request.RadiusMeters!.Value)
            {
                continue;
            }

            totalCount++;
            if (totalCount <= request.GeoSkip || rows.Count >= request.GeoTake)
            {
                continue;
            }

            rows.Add(row with { DistanceFromCenterMeters = distance });
        }

        return new IncidentWindowQueryPage<LocationRowDto>(rows, totalCount);
    }

    private static async Task<IncidentWindowQueryPage<IncidentWindowCoLocationCandidateDto>> QueryCoLocationAsync(
        SqliteConnection connection,
        PreparedRequest request,
        CancellationToken ct
    )
    {
        var bboxWhereSql = GetBoundingBoxWhereSql("lo", request.BoundingBox);

        await using var command = connection.CreateCommand();
        command.CommandText = GetCoLocationRowsSql(bboxWhereSql);
        AddCommonGeoParameters(command, request);
        AddBoundingBoxParameters(command, request.BoundingBox);

        var rows = new List<IncidentWindowCoLocationCandidateDto>(request.CoLocationTake);
        var recent = new List<LocationRowDto>();
        var totalCount = 0;

        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            ct.ThrowIfCancellationRequested();
            if (!TryReadLocationRow(reader, out var current))
            {
                continue;
            }

            var sceneDistance = GeoMath.HaversineDistanceMeters(
                request.CenterLatitude!.Value,
                request.CenterLongitude!.Value,
                current.Latitude,
                current.Longitude
            );
            if (sceneDistance > request.RadiusMeters!.Value)
            {
                continue;
            }

            current = current with { DistanceFromCenterMeters = sceneDistance };
            recent.RemoveAll(item => (item.ObservedUtc - current.ObservedUtc) > request.CoLocationWindow);

            foreach (var newer in recent)
            {
                if (AreSameSubject(newer, current))
                {
                    continue;
                }

                if (!PairMatchesSubjectFocus(newer, current, request))
                {
                    continue;
                }

                var pairDistance = GeoMath.HaversineDistanceMeters(
                    newer.Latitude,
                    newer.Longitude,
                    current.Latitude,
                    current.Longitude
                );
                if (pairDistance > request.CoLocationDistanceMeters)
                {
                    continue;
                }

                totalCount++;
                if (totalCount <= request.CoLocationSkip || rows.Count >= request.CoLocationTake)
                {
                    continue;
                }

                rows.Add(new IncidentWindowCoLocationCandidateDto(
                    FirstObservation: newer,
                    SecondObservation: current,
                    DistanceMeters: pairDistance,
                    TimeDeltaMinutes: Math.Abs((newer.ObservedUtc - current.ObservedUtc).TotalMinutes)
                ));
            }

            recent.Add(current);
        }

        return new IncidentWindowQueryPage<IncidentWindowCoLocationCandidateDto>(rows, totalCount);
    }

    private async Task WriteAuditAsync(
        PreparedRequest request,
        IncidentWindowQueryResult result,
        CancellationToken ct
    )
    {
        var payload = JsonSerializer.Serialize(new
        {
            request.CorrelationId,
            request.StartUtc,
            request.EndUtc,
            request.RadiusEnabled,
            request.CenterLatitude,
            request.CenterLongitude,
            request.RadiusMeters,
            SubjectType = request.SubjectMode switch
            {
                IncidentWindowSubjectMode.Target => "Target",
                IncidentWindowSubjectMode.GlobalPerson => "GlobalPerson",
                _ => null
            },
            request.SubjectId,
            request.IncludeCoLocationCandidates,
            request.CoLocationDistanceMeters,
            CoLocationTimeWindowMinutes = request.CoLocationWindow.TotalMinutes,
            CommsResultCount = result.Comms.TotalCount,
            GeoResultCount = result.Geo.TotalCount,
            CoLocationResultCount = result.CoLocation.TotalCount
        });

        await _auditLogService.AddAsync(
            new AuditEvent
            {
                TimestampUtc = _clock.UtcNow.ToUniversalTime(),
                Operator = Environment.UserName,
                ActionType = "IncidentWindowExecuted",
                CaseId = request.CaseId,
                Summary =
                    $"Incident window returned {result.Comms.TotalCount:0} comms hit(s), "
                    + $"{result.Geo.TotalCount:0} geo hit(s), "
                    + $"{result.CoLocation.TotalCount:0} co-location candidate(s).",
                JsonPayload = payload
            },
            ct
        );
    }

    private static void AddCommonCommsParameters(SqliteCommand command, PreparedRequest request)
    {
        command.Parameters.AddWithValue("$caseId", request.CaseId);
        command.Parameters.AddWithValue("$startUtc", request.StartUtc.ToString("O", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$endUtc", request.EndUtc.ToString("O", CultureInfo.InvariantCulture));
        AddOptionalGuidParameter(command, "$subjectId", request.SubjectId);
    }

    private static void AddCommonGeoParameters(SqliteCommand command, PreparedRequest request)
    {
        command.Parameters.AddWithValue("$caseId", request.CaseId);
        command.Parameters.AddWithValue("$startUtc", request.StartUtc.ToString("O", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$endUtc", request.EndUtc.ToString("O", CultureInfo.InvariantCulture));
        AddOptionalGuidParameter(command, "$subjectId", request.SubjectId);
    }

    private static void AddBoundingBoxParameters(SqliteCommand command, GeoBoundingBox? box)
    {
        if (box is null)
        {
            return;
        }

        command.Parameters.AddWithValue("$minLatitude", box.MinLatitude);
        command.Parameters.AddWithValue("$maxLatitude", box.MaxLatitude);
        command.Parameters.AddWithValue("$minLongitude", box.MinLongitude);
        command.Parameters.AddWithValue("$maxLongitude", box.MaxLongitude);
    }

    private static void AddOptionalGuidParameter(SqliteCommand command, string parameterName, Guid? value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = parameterName;
        parameter.Value = value.HasValue ? value.Value : DBNull.Value;
        command.Parameters.Add(parameter);
    }

    private static bool PairMatchesSubjectFocus(
        LocationRowDto first,
        LocationRowDto second,
        PreparedRequest request
    )
    {
        if (request.SubjectMode == IncidentWindowSubjectMode.Any)
        {
            return true;
        }

        return MatchesSubjectFocus(first, request) || MatchesSubjectFocus(second, request);
    }

    private static bool MatchesSubjectFocus(LocationRowDto row, PreparedRequest request)
    {
        if (request.SubjectMode == IncidentWindowSubjectMode.Any)
        {
            return true;
        }

        if (request.SubjectMode == IncidentWindowSubjectMode.Target)
        {
            if (!string.Equals(row.SubjectType, "Target", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            return !request.SubjectId.HasValue || row.SubjectId == request.SubjectId;
        }

        if (!string.Equals(row.SubjectType, "GlobalPerson", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return !request.SubjectId.HasValue || row.SubjectId == request.SubjectId;
    }

    private static bool AreSameSubject(LocationRowDto left, LocationRowDto right)
    {
        return left.SubjectId.HasValue
            && right.SubjectId.HasValue
            && left.SubjectId == right.SubjectId
            && string.Equals(left.SubjectType, right.SubjectType, StringComparison.OrdinalIgnoreCase);
    }

    private static bool TryReadCommsRow(SqliteDataReader reader, out TimelineRowDto row)
    {
        try
        {
            var timestampUtc = TryParseDateTimeOffset(reader.GetValue(3));
            if (!timestampUtc.HasValue)
            {
                throw new InvalidOperationException("Message timestamp could not be parsed.");
            }

            var senderRaw = reader.IsDBNull(5) ? null : reader.GetString(5);
            var recipientsRaw = reader.IsDBNull(6) ? null : reader.GetString(6);
            var resolvedSender = NormalizeGroupConcat(reader.IsDBNull(14) ? null : reader.GetString(14));
            var resolvedRecipients = NormalizeGroupConcat(reader.IsDBNull(15) ? null : reader.GetString(15));
            var senderDisplay = !string.IsNullOrWhiteSpace(resolvedSender)
                ? resolvedSender
                : senderRaw;
            var recipientsDisplay = !string.IsNullOrWhiteSpace(resolvedRecipients)
                ? resolvedRecipients
                : recipientsRaw;

            row = new TimelineRowDto(
                MessageEventId: ReadGuid(reader.GetValue(0)),
                CaseId: ReadGuid(reader.GetValue(1)),
                SourceEvidenceItemId: ReadGuid(reader.GetValue(2)),
                EventType: "Message",
                TimestampUtc: timestampUtc,
                Direction: reader.IsDBNull(4) ? "Unknown" : reader.GetString(4),
                ParticipantsSummary: BuildParticipantsSummary(senderDisplay, recipientsDisplay),
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
            return true;
        }
        catch (Exception ex)
        {
            Trace.TraceWarning($"IncidentWindow skipped message row: {ex.Message}");
            row = null!;
            return false;
        }
    }

    private static bool TryReadLocationRow(SqliteDataReader reader, out LocationRowDto row)
    {
        try
        {
            var observedUtc = TryParseDateTimeOffset(reader.GetValue(2));
            if (!observedUtc.HasValue)
            {
                throw new InvalidOperationException("Location observation timestamp could not be parsed.");
            }

            row = new LocationRowDto(
                LocationObservationId: ReadGuid(reader.GetValue(0)),
                CaseId: ReadGuid(reader.GetValue(1)),
                ObservedUtc: observedUtc.Value,
                Latitude: reader.GetDouble(3),
                Longitude: reader.GetDouble(4),
                AccuracyMeters: reader.IsDBNull(5) ? null : reader.GetDouble(5),
                AltitudeMeters: reader.IsDBNull(6) ? null : reader.GetDouble(6),
                SpeedMps: reader.IsDBNull(7) ? null : reader.GetDouble(7),
                HeadingDegrees: reader.IsDBNull(8) ? null : reader.GetDouble(8),
                SourceType: reader.IsDBNull(9) ? string.Empty : reader.GetString(9),
                SourceLabel: reader.IsDBNull(10) ? null : reader.GetString(10),
                SubjectType: reader.IsDBNull(11) ? null : reader.GetString(11),
                SubjectId: reader.IsDBNull(12) ? null : ReadGuid(reader.GetValue(12)),
                SourceEvidenceItemId: ReadGuid(reader.GetValue(13)),
                SourceLocator: reader.IsDBNull(14) ? string.Empty : reader.GetString(14),
                IngestModuleVersion: reader.IsDBNull(15) ? string.Empty : reader.GetString(15)
            )
            {
                EvidenceDisplayName = reader.IsDBNull(16) ? null : reader.GetString(16),
                StoredRelativePath = reader.IsDBNull(17) ? null : reader.GetString(17),
                SubjectDisplayName = reader.IsDBNull(18) ? null : reader.GetString(18)
            };
            return true;
        }
        catch (Exception ex)
        {
            Trace.TraceWarning($"IncidentWindow skipped location row: {ex.Message}");
            row = null!;
            return false;
        }
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
        return value switch
        {
            DateTimeOffset dto => dto.ToUniversalTime(),
            DateTime dt => new DateTimeOffset(dt.ToUniversalTime()),
            string text when DateTimeOffset.TryParse(
                text,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal,
                out var parsed) => parsed.ToUniversalTime(),
            _ => null
        };
    }

    private static string BuildParticipantsSummary(string? sender, string? recipients)
    {
        var left = string.IsNullOrWhiteSpace(sender) ? "(unknown sender)" : sender.Trim();
        var right = string.IsNullOrWhiteSpace(recipients)
            ? "(unknown recipients)"
            : recipients.Trim();
        return $"{left} -> {right}";
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

    private static string GetCommsCountSql(string subjectWhereSql)
    {
        return
            $$"""
            SELECT COUNT(1)
            FROM MessageEventRecord me
            WHERE me.CaseId = $caseId
              AND me.TimestampUtc IS NOT NULL
              AND julianday(me.TimestampUtc) >= julianday($startUtc)
              AND julianday(me.TimestampUtc) <= julianday($endUtc)
            {{subjectWhereSql}};
            """;
    }

    private static string GetCommsRowsSql(string subjectWhereSql)
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
              AND me.TimestampUtc IS NOT NULL
              AND julianday(me.TimestampUtc) >= julianday($startUtc)
              AND julianday(me.TimestampUtc) <= julianday($endUtc)
            {{subjectWhereSql}}
            ORDER BY me.TimestampUtc DESC, me.MessageEventId DESC
            LIMIT $take OFFSET $skip;
            """;
    }

    private static string GetGeoCountSql(string subjectWhereSql, string bboxWhereSql)
    {
        return
            $$"""
            SELECT COUNT(1)
            FROM LocationObservationRecord lo
            WHERE lo.CaseId = $caseId
              AND julianday(lo.ObservedUtc) >= julianday($startUtc)
              AND julianday(lo.ObservedUtc) <= julianday($endUtc)
            {{subjectWhereSql}}
            {{bboxWhereSql}};
            """;
    }

    private static string GetGeoRowsSql(
        string subjectWhereSql,
        string bboxWhereSql,
        bool includeLimit
    )
    {
        var limitSql = includeLimit
            ? "LIMIT $take OFFSET $skip"
            : string.Empty;

        return
            $$"""
            SELECT
                lo.LocationObservationId,
                lo.CaseId,
                lo.ObservedUtc,
                lo.Latitude,
                lo.Longitude,
                lo.AccuracyMeters,
                lo.AltitudeMeters,
                lo.SpeedMps,
                lo.HeadingDegrees,
                lo.SourceType,
                lo.SourceLabel,
                lo.SubjectType,
                lo.SubjectId,
                lo.SourceEvidenceItemId,
                lo.SourceLocator,
                lo.IngestModuleVersion,
                ei.DisplayName,
                ei.StoredRelativePath,
                CASE
                    WHEN LOWER(COALESCE(lo.SubjectType, '')) = 'target'
                        THEN COALESCE(NULLIF(peTarget.DisplayName, ''), tr.DisplayName)
                    WHEN LOWER(COALESCE(lo.SubjectType, '')) = 'globalperson'
                        THEN peDirect.DisplayName
                    ELSE NULL
                END AS SubjectDisplayName
            FROM LocationObservationRecord lo
            LEFT JOIN EvidenceItemRecord ei
                ON ei.EvidenceItemId = lo.SourceEvidenceItemId
            LEFT JOIN TargetRecord tr
                ON tr.CaseId = lo.CaseId
               AND tr.TargetId = lo.SubjectId
               AND LOWER(COALESCE(lo.SubjectType, '')) = 'target'
            LEFT JOIN PersonEntity peTarget
                ON peTarget.GlobalEntityId = tr.GlobalEntityId
            LEFT JOIN PersonEntity peDirect
                ON peDirect.GlobalEntityId = lo.SubjectId
               AND LOWER(COALESCE(lo.SubjectType, '')) = 'globalperson'
            WHERE lo.CaseId = $caseId
              AND julianday(lo.ObservedUtc) >= julianday($startUtc)
              AND julianday(lo.ObservedUtc) <= julianday($endUtc)
            {{subjectWhereSql}}
            {{bboxWhereSql}}
            ORDER BY lo.ObservedUtc DESC, lo.LocationObservationId DESC
            {{limitSql}};
            """;
    }

    private static string GetCoLocationRowsSql(string bboxWhereSql)
    {
        return
            $$"""
            SELECT
                lo.LocationObservationId,
                lo.CaseId,
                lo.ObservedUtc,
                lo.Latitude,
                lo.Longitude,
                lo.AccuracyMeters,
                lo.AltitudeMeters,
                lo.SpeedMps,
                lo.HeadingDegrees,
                lo.SourceType,
                lo.SourceLabel,
                lo.SubjectType,
                lo.SubjectId,
                lo.SourceEvidenceItemId,
                lo.SourceLocator,
                lo.IngestModuleVersion,
                ei.DisplayName,
                ei.StoredRelativePath,
                CASE
                    WHEN LOWER(COALESCE(lo.SubjectType, '')) = 'target'
                        THEN COALESCE(NULLIF(peTarget.DisplayName, ''), tr.DisplayName)
                    WHEN LOWER(COALESCE(lo.SubjectType, '')) = 'globalperson'
                        THEN peDirect.DisplayName
                    ELSE NULL
                END AS SubjectDisplayName
            FROM LocationObservationRecord lo
            LEFT JOIN EvidenceItemRecord ei
                ON ei.EvidenceItemId = lo.SourceEvidenceItemId
            LEFT JOIN TargetRecord tr
                ON tr.CaseId = lo.CaseId
               AND tr.TargetId = lo.SubjectId
               AND LOWER(COALESCE(lo.SubjectType, '')) = 'target'
            LEFT JOIN PersonEntity peTarget
                ON peTarget.GlobalEntityId = tr.GlobalEntityId
            LEFT JOIN PersonEntity peDirect
                ON peDirect.GlobalEntityId = lo.SubjectId
               AND LOWER(COALESCE(lo.SubjectType, '')) = 'globalperson'
            WHERE lo.CaseId = $caseId
              AND julianday(lo.ObservedUtc) >= julianday($startUtc)
              AND julianday(lo.ObservedUtc) <= julianday($endUtc)
              AND lo.SubjectType IS NOT NULL
              AND lo.SubjectId IS NOT NULL
            {{bboxWhereSql}}
            ORDER BY lo.ObservedUtc DESC, lo.LocationObservationId DESC;
            """;
    }

    private static string GetCommsSubjectWhereSql(PreparedRequest request)
    {
        return request.SubjectMode switch
        {
            IncidentWindowSubjectMode.Target when request.SubjectId.HasValue =>
                """
                  AND EXISTS (
                        SELECT 1
                        FROM TargetMessagePresenceRecord tmp
                        WHERE tmp.CaseId = me.CaseId
                          AND tmp.MessageEventId = me.MessageEventId
                          AND tmp.TargetId = $subjectId
                    )
                """,
            IncidentWindowSubjectMode.Target =>
                """
                  AND EXISTS (
                        SELECT 1
                        FROM TargetMessagePresenceRecord tmp
                        WHERE tmp.CaseId = me.CaseId
                          AND tmp.MessageEventId = me.MessageEventId
                    )
                """,
            IncidentWindowSubjectMode.GlobalPerson when request.SubjectId.HasValue =>
                """
                  AND EXISTS (
                        SELECT 1
                        FROM TargetMessagePresenceRecord tmp
                        INNER JOIN TargetRecord tr
                            ON tr.CaseId = tmp.CaseId
                           AND tr.TargetId = tmp.TargetId
                        WHERE tmp.CaseId = me.CaseId
                          AND tmp.MessageEventId = me.MessageEventId
                          AND tr.GlobalEntityId = $subjectId
                    )
                """,
            IncidentWindowSubjectMode.GlobalPerson =>
                """
                  AND EXISTS (
                        SELECT 1
                        FROM TargetMessagePresenceRecord tmp
                        INNER JOIN TargetRecord tr
                            ON tr.CaseId = tmp.CaseId
                           AND tr.TargetId = tmp.TargetId
                        WHERE tmp.CaseId = me.CaseId
                          AND tmp.MessageEventId = me.MessageEventId
                          AND tr.GlobalEntityId IS NOT NULL
                    )
                """,
            _ => string.Empty
        };
    }

    private static string GetGeoSubjectWhereSql(PreparedRequest request)
    {
        return request.SubjectMode switch
        {
            IncidentWindowSubjectMode.Target when request.SubjectId.HasValue =>
                """
                  AND LOWER(COALESCE(lo.SubjectType, '')) = 'target'
                  AND lo.SubjectId = $subjectId
                """,
            IncidentWindowSubjectMode.Target =>
                """
                  AND LOWER(COALESCE(lo.SubjectType, '')) = 'target'
                """,
            IncidentWindowSubjectMode.GlobalPerson when request.SubjectId.HasValue =>
                """
                  AND LOWER(COALESCE(lo.SubjectType, '')) = 'globalperson'
                  AND lo.SubjectId = $subjectId
                """,
            IncidentWindowSubjectMode.GlobalPerson =>
                """
                  AND LOWER(COALESCE(lo.SubjectType, '')) = 'globalperson'
                """,
            _ => string.Empty
        };
    }

    private static string GetBoundingBoxWhereSql(string alias, GeoBoundingBox? box)
    {
        if (box is null)
        {
            return string.Empty;
        }

        return box.CrossesAntiMeridian
            ? $"""
                  AND {alias}.Latitude BETWEEN $minLatitude AND $maxLatitude
                  AND ({alias}.Longitude >= $minLongitude OR {alias}.Longitude <= $maxLongitude)
                """
            : $"""
                  AND {alias}.Latitude BETWEEN $minLatitude AND $maxLatitude
                  AND {alias}.Longitude BETWEEN $minLongitude AND $maxLongitude
                """;
    }

    private sealed record PreparedRequest(
        Guid CaseId,
        DateTimeOffset StartUtc,
        DateTimeOffset EndUtc,
        bool RadiusEnabled,
        double? CenterLatitude,
        double? CenterLongitude,
        double? RadiusMeters,
        GeoBoundingBox? BoundingBox,
        IncidentWindowSubjectMode SubjectMode,
        Guid? SubjectId,
        bool IncludeCoLocationCandidates,
        int CommsTake,
        int CommsSkip,
        int GeoTake,
        int GeoSkip,
        int CoLocationTake,
        int CoLocationSkip,
        double CoLocationDistanceMeters,
        TimeSpan CoLocationWindow,
        string CorrelationId,
        bool WriteAuditEvent,
        bool CanSearch
    )
    {
        public bool ShouldQueryCoLocation => RadiusEnabled && IncludeCoLocationCandidates;

        public static PreparedRequest From(IncidentWindowQueryRequest request)
        {
            if (request.CaseId == Guid.Empty)
            {
                return new PreparedRequest(
                    Guid.Empty,
                    DateTimeOffset.MinValue,
                    DateTimeOffset.MinValue,
                    RadiusEnabled: false,
                    CenterLatitude: null,
                    CenterLongitude: null,
                    RadiusMeters: null,
                    BoundingBox: null,
                    SubjectMode: IncidentWindowSubjectMode.Any,
                    SubjectId: null,
                    IncludeCoLocationCandidates: false,
                    CommsTake: 0,
                    CommsSkip: 0,
                    GeoTake: 0,
                    GeoSkip: 0,
                    CoLocationTake: 0,
                    CoLocationSkip: 0,
                    CoLocationDistanceMeters: DefaultCoLocationDistanceMeters,
                    CoLocationWindow: TimeSpan.FromMinutes(DefaultCoLocationTimeWindowMinutes),
                    CorrelationId: request.CorrelationId,
                    WriteAuditEvent: request.WriteAuditEvent,
                    CanSearch: false
                );
            }

            var startUtc = request.StartUtc.ToUniversalTime();
            var endUtc = request.EndUtc.ToUniversalTime();
            if (startUtc > endUtc)
            {
                (startUtc, endUtc) = (endUtc, startUtc);
            }

            var (subjectMode, subjectId) = NormalizeSubject(request.SubjectType, request.SubjectId);
            var correlationId = string.IsNullOrWhiteSpace(request.CorrelationId)
                ? Guid.NewGuid().ToString("N")
                : request.CorrelationId.Trim();

            if (!request.RadiusEnabled)
            {
                return new PreparedRequest(
                    request.CaseId,
                    startUtc,
                    endUtc,
                    RadiusEnabled: false,
                    CenterLatitude: null,
                    CenterLongitude: null,
                    RadiusMeters: null,
                    BoundingBox: null,
                    SubjectMode: subjectMode,
                    SubjectId: subjectId,
                    IncludeCoLocationCandidates: request.IncludeCoLocationCandidates,
                    CommsTake: ClampTake(request.CommsTake),
                    CommsSkip: Math.Max(0, request.CommsSkip),
                    GeoTake: ClampTake(request.GeoTake),
                    GeoSkip: Math.Max(0, request.GeoSkip),
                    CoLocationTake: ClampTake(request.CoLocationTake),
                    CoLocationSkip: Math.Max(0, request.CoLocationSkip),
                    CoLocationDistanceMeters: NormalizeCoLocationDistance(request.CoLocationDistanceMeters),
                    CoLocationWindow: NormalizeCoLocationWindow(request.CoLocationTimeWindowMinutes),
                    CorrelationId: correlationId,
                    WriteAuditEvent: request.WriteAuditEvent,
                    CanSearch: true
                );
            }

            if (!request.CenterLatitude.HasValue || !request.CenterLongitude.HasValue)
            {
                throw new ArgumentException(
                    "Center latitude and longitude are required when radius filtering is enabled.",
                    nameof(request)
                );
            }

            if (!request.RadiusMeters.HasValue || request.RadiusMeters.Value <= 0d)
            {
                throw new ArgumentException(
                    "Radius meters must be greater than zero when radius filtering is enabled.",
                    nameof(request)
                );
            }

            if (request.CenterLatitude.Value is < -90d or > 90d)
            {
                throw new ArgumentException("Center latitude must be between -90 and 90.", nameof(request));
            }

            if (request.CenterLongitude.Value is < -180d or > 180d)
            {
                throw new ArgumentException("Center longitude must be between -180 and 180.", nameof(request));
            }

            var boundingBox = GeoMath.GetBoundingBox(
                request.CenterLatitude.Value,
                request.CenterLongitude.Value,
                request.RadiusMeters.Value
            );

            return new PreparedRequest(
                request.CaseId,
                startUtc,
                endUtc,
                RadiusEnabled: true,
                CenterLatitude: request.CenterLatitude.Value,
                CenterLongitude: request.CenterLongitude.Value,
                RadiusMeters: request.RadiusMeters.Value,
                BoundingBox: boundingBox,
                SubjectMode: subjectMode,
                SubjectId: subjectId,
                IncludeCoLocationCandidates: request.IncludeCoLocationCandidates,
                CommsTake: ClampTake(request.CommsTake),
                CommsSkip: Math.Max(0, request.CommsSkip),
                GeoTake: ClampTake(request.GeoTake),
                GeoSkip: Math.Max(0, request.GeoSkip),
                CoLocationTake: ClampTake(request.CoLocationTake),
                CoLocationSkip: Math.Max(0, request.CoLocationSkip),
                CoLocationDistanceMeters: NormalizeCoLocationDistance(request.CoLocationDistanceMeters),
                CoLocationWindow: NormalizeCoLocationWindow(request.CoLocationTimeWindowMinutes),
                CorrelationId: correlationId,
                WriteAuditEvent: request.WriteAuditEvent,
                CanSearch: true
            );
        }

        private static (IncidentWindowSubjectMode Mode, Guid? SubjectId) NormalizeSubject(
            string? subjectType,
            Guid? subjectId
        )
        {
            var normalizedId = subjectId.GetValueOrDefault() == Guid.Empty
                ? null
                : subjectId;

            if (string.IsNullOrWhiteSpace(subjectType))
            {
                return (IncidentWindowSubjectMode.Any, null);
            }

            return subjectType.Trim().ToLowerInvariant() switch
            {
                "target" => (IncidentWindowSubjectMode.Target, normalizedId),
                "globalperson" or "global_person" or "global" => (
                    IncidentWindowSubjectMode.GlobalPerson,
                    normalizedId
                ),
                _ => (IncidentWindowSubjectMode.Any, null)
            };
        }

        private static int ClampTake(int requestedTake)
        {
            return Math.Clamp(requestedTake <= 0 ? DefaultPageSize : requestedTake, 1, MaxPageSize);
        }

        private static double NormalizeCoLocationDistance(double requestedDistance)
        {
            return requestedDistance <= 0d
                ? DefaultCoLocationDistanceMeters
                : Math.Clamp(requestedDistance, 1d, 10_000d);
        }

        private static TimeSpan NormalizeCoLocationWindow(int requestedMinutes)
        {
            var minutes = requestedMinutes <= 0
                ? DefaultCoLocationTimeWindowMinutes
                : Math.Clamp(requestedMinutes, 1, 240);
            return TimeSpan.FromMinutes(minutes);
        }
    }
}
