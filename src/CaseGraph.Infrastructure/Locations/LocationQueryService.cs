using CaseGraph.Core.Abstractions;
using CaseGraph.Core.Models;
using Microsoft.Data.Sqlite;
using System.Globalization;
using System.Text.Json;

namespace CaseGraph.Infrastructure.Locations;

public sealed class LocationQueryService
{
    private readonly IWorkspaceDatabaseInitializer _databaseInitializer;
    private readonly IWorkspacePathProvider _workspacePathProvider;
    private readonly IAuditLogService _auditLogService;

    public LocationQueryService(
        IWorkspaceDatabaseInitializer databaseInitializer,
        IWorkspacePathProvider workspacePathProvider,
        IAuditLogService auditLogService
    )
    {
        _databaseInitializer = databaseInitializer;
        _workspacePathProvider = workspacePathProvider;
        _auditLogService = auditLogService;
    }

    public async Task<LocationQueryPage> SearchAsync(
        LocationQueryRequest request,
        CancellationToken ct
    )
    {
        await _databaseInitializer.EnsureInitializedAsync(ct);

        var prepared = LocationQueryRequestPrepared.From(request);
        if (!prepared.CanSearch)
        {
            return LocationQueryPage.Empty;
        }

        await using var connection = await OpenConnectionAsync(ct);
        var totalCount = await ExecuteCountAsync(connection, prepared, ct);
        var rows = await ExecuteRowsAsync(connection, prepared, ct);
        var page = new LocationQueryPage(rows, totalCount);

        await WriteAuditAsync(prepared, totalCount, ct);
        return page;
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
        LocationQueryRequestPrepared request,
        CancellationToken ct
    )
    {
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT COUNT(1)
            FROM LocationObservationRecord lo
            WHERE lo.CaseId = $caseId
              AND ($fromUtc IS NULL OR julianday(lo.ObservedUtc) >= julianday($fromUtc))
              AND ($toUtc IS NULL OR julianday(lo.ObservedUtc) <= julianday($toUtc))
              AND ($sourceType IS NULL OR UPPER(COALESCE(lo.SourceType, '')) = $sourceType)
              AND ($subjectType IS NULL OR LOWER(COALESCE(lo.SubjectType, '')) = $subjectType)
              AND ($subjectId IS NULL OR lo.SubjectId = $subjectId)
              AND ($minAccuracy IS NULL OR (lo.AccuracyMeters IS NOT NULL AND lo.AccuracyMeters >= $minAccuracy))
              AND ($maxAccuracy IS NULL OR (lo.AccuracyMeters IS NOT NULL AND lo.AccuracyMeters <= $maxAccuracy));
            """;
        AddCommonParameters(command, request);

        var value = await command.ExecuteScalarAsync(ct);
        return value is null or DBNull
            ? 0
            : Convert.ToInt32(value, CultureInfo.InvariantCulture);
    }

    private static async Task<IReadOnlyList<LocationRowDto>> ExecuteRowsAsync(
        SqliteConnection connection,
        LocationQueryRequestPrepared request,
        CancellationToken ct
    )
    {
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
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
              AND ($fromUtc IS NULL OR julianday(lo.ObservedUtc) >= julianday($fromUtc))
              AND ($toUtc IS NULL OR julianday(lo.ObservedUtc) <= julianday($toUtc))
              AND ($sourceType IS NULL OR UPPER(COALESCE(lo.SourceType, '')) = $sourceType)
              AND ($subjectType IS NULL OR LOWER(COALESCE(lo.SubjectType, '')) = $subjectType)
              AND ($subjectId IS NULL OR lo.SubjectId = $subjectId)
              AND ($minAccuracy IS NULL OR (lo.AccuracyMeters IS NOT NULL AND lo.AccuracyMeters >= $minAccuracy))
              AND ($maxAccuracy IS NULL OR (lo.AccuracyMeters IS NOT NULL AND lo.AccuracyMeters <= $maxAccuracy))
            ORDER BY lo.ObservedUtc DESC, lo.LocationObservationId DESC
            LIMIT $take OFFSET $skip;
            """;
        AddCommonParameters(command, request);
        command.Parameters.AddWithValue("$take", request.Take);
        command.Parameters.AddWithValue("$skip", request.Skip);

        var rows = new List<LocationRowDto>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            rows.Add(ReadRow(reader));
        }

        return rows;
    }

    private async Task WriteAuditAsync(
        LocationQueryRequestPrepared request,
        int totalCount,
        CancellationToken ct
    )
    {
        var payload = JsonSerializer.Serialize(new
        {
            request.CorrelationId,
            request.FromUtc,
            request.ToUtc,
            request.MinAccuracyMeters,
            request.MaxAccuracyMeters,
            request.SourceType,
            request.SubjectType,
            request.SubjectId,
            request.Take,
            request.Skip,
            ResultCount = totalCount
        });

        await _auditLogService.AddAsync(
            new AuditEvent
            {
                TimestampUtc = DateTimeOffset.UtcNow,
                Operator = Environment.UserName,
                ActionType = "LocationsSearchExecuted",
                CaseId = request.CaseId,
                Summary = $"Locations search returned {totalCount:0} row(s).",
                JsonPayload = payload
            },
            ct
        );
    }

    private static void AddCommonParameters(
        SqliteCommand command,
        LocationQueryRequestPrepared request
    )
    {
        command.Parameters.AddWithValue("$caseId", request.CaseId);
        AddOptionalStringParameter(command, "$fromUtc", request.FromUtc?.ToString("O", CultureInfo.InvariantCulture));
        AddOptionalStringParameter(command, "$toUtc", request.ToUtc?.ToString("O", CultureInfo.InvariantCulture));
        AddOptionalStringParameter(command, "$sourceType", request.SourceType);
        AddOptionalStringParameter(command, "$subjectType", request.SubjectType);
        AddOptionalGuidParameter(command, "$subjectId", request.SubjectId);
        AddOptionalDoubleParameter(command, "$minAccuracy", request.MinAccuracyMeters);
        AddOptionalDoubleParameter(command, "$maxAccuracy", request.MaxAccuracyMeters);
    }

    private static void AddOptionalStringParameter(
        SqliteCommand command,
        string parameterName,
        string? value
    )
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = parameterName;
        parameter.Value = string.IsNullOrWhiteSpace(value)
            ? DBNull.Value
            : value;
        command.Parameters.Add(parameter);
    }

    private static void AddOptionalGuidParameter(
        SqliteCommand command,
        string parameterName,
        Guid? value
    )
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = parameterName;
        parameter.Value = value.HasValue
            ? value.Value
            : DBNull.Value;
        command.Parameters.Add(parameter);
    }

    private static void AddOptionalDoubleParameter(
        SqliteCommand command,
        string parameterName,
        double? value
    )
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = parameterName;
        parameter.Value = value.HasValue
            ? value.Value
            : DBNull.Value;
        command.Parameters.Add(parameter);
    }

    private static LocationRowDto ReadRow(SqliteDataReader reader)
    {
        return new LocationRowDto(
            LocationObservationId: ReadGuid(reader.GetValue(0)),
            CaseId: ReadGuid(reader.GetValue(1)),
            ObservedUtc: TryParseDateTimeOffset(reader.GetValue(2)) ?? DateTimeOffset.UnixEpoch,
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

    private sealed record LocationQueryRequestPrepared(
        Guid CaseId,
        DateTimeOffset? FromUtc,
        DateTimeOffset? ToUtc,
        double? MinAccuracyMeters,
        double? MaxAccuracyMeters,
        string? SourceType,
        string? SubjectType,
        Guid? SubjectId,
        int Take,
        int Skip,
        string CorrelationId,
        bool CanSearch
    )
    {
        public static LocationQueryRequestPrepared From(LocationQueryRequest request)
        {
            if (request.CaseId == Guid.Empty)
            {
                return new LocationQueryRequestPrepared(
                    Guid.Empty,
                    FromUtc: null,
                    ToUtc: null,
                    MinAccuracyMeters: null,
                    MaxAccuracyMeters: null,
                    SourceType: null,
                    SubjectType: null,
                    SubjectId: null,
                    Take: 0,
                    Skip: 0,
                    CorrelationId: request.CorrelationId,
                    CanSearch: false
                );
            }

            var fromUtc = request.FromUtc?.ToUniversalTime();
            var toUtc = request.ToUtc?.ToUniversalTime();
            if (fromUtc.HasValue && toUtc.HasValue && fromUtc.Value > toUtc.Value)
            {
                (fromUtc, toUtc) = (toUtc, fromUtc);
            }

            var minAccuracy = request.MinAccuracyMeters;
            var maxAccuracy = request.MaxAccuracyMeters;
            if (minAccuracy.HasValue
                && maxAccuracy.HasValue
                && minAccuracy.Value > maxAccuracy.Value)
            {
                (minAccuracy, maxAccuracy) = (maxAccuracy, minAccuracy);
            }

            var subjectId = request.SubjectId.GetValueOrDefault() == Guid.Empty
                ? null
                : request.SubjectId;

            return new LocationQueryRequestPrepared(
                request.CaseId,
                fromUtc,
                toUtc,
                minAccuracy,
                maxAccuracy,
                NormalizeSourceType(request.SourceType),
                NormalizeSubjectType(request.SubjectType),
                subjectId,
                Take: Math.Clamp(request.Take, 1, 500),
                Skip: Math.Max(0, request.Skip),
                CorrelationId: string.IsNullOrWhiteSpace(request.CorrelationId)
                    ? Guid.NewGuid().ToString("N")
                    : request.CorrelationId.Trim(),
                CanSearch: true
            );
        }

        private static string? NormalizeSourceType(string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return null;
            }

            return value.Trim().ToUpperInvariant();
        }

        private static string? NormalizeSubjectType(string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return null;
            }

            var normalized = value.Trim().ToLowerInvariant();
            return normalized switch
            {
                "target" => "target",
                "globalperson" or "global_person" or "global" => "globalperson",
                _ => null
            };
        }
    }
}

public sealed record LocationQueryRequest(
    Guid CaseId,
    DateTimeOffset? FromUtc,
    DateTimeOffset? ToUtc,
    double? MinAccuracyMeters,
    double? MaxAccuracyMeters,
    string? SourceType,
    string? SubjectType,
    Guid? SubjectId,
    int Take,
    int Skip,
    string CorrelationId
);

public sealed record LocationQueryPage(IReadOnlyList<LocationRowDto> Rows, int TotalCount)
{
    public static LocationQueryPage Empty { get; } = new(Array.Empty<LocationRowDto>(), 0);
}
