using CaseGraph.Core.Abstractions;
using CaseGraph.Core.Diagnostics;
using CaseGraph.Core.Models;
using CaseGraph.Infrastructure.Diagnostics;
using CaseGraph.Infrastructure.IncidentWindow;
using CaseGraph.Infrastructure.Locations;
using CaseGraph.Infrastructure.Persistence;
using CaseGraph.Infrastructure.Persistence.Entities;
using CaseGraph.Infrastructure.Services;
using CaseGraph.Infrastructure.Timeline;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using System.Globalization;
using System.Text.Json;

namespace CaseGraph.Infrastructure.Incidents;

public sealed class IncidentService : IIncidentService
{
    private readonly IDbContextFactory<WorkspaceDbContext> _dbContextFactory;
    private readonly IWorkspaceDatabaseInitializer _databaseInitializer;
    private readonly IWorkspaceWriteGate _workspaceWriteGate;
    private readonly IWorkspacePathProvider _workspacePathProvider;
    private readonly IAuditLogService _auditLogService;
    private readonly IClock _clock;
    private readonly IPerformanceInstrumentation _performanceInstrumentation;

    public IncidentService(
        IDbContextFactory<WorkspaceDbContext> dbContextFactory,
        IWorkspaceDatabaseInitializer databaseInitializer,
        IWorkspaceWriteGate workspaceWriteGate,
        IWorkspacePathProvider workspacePathProvider,
        IAuditLogService auditLogService,
        IClock clock,
        IPerformanceInstrumentation? performanceInstrumentation = null
    )
    {
        _dbContextFactory = dbContextFactory;
        _databaseInitializer = databaseInitializer;
        _workspaceWriteGate = workspaceWriteGate;
        _workspacePathProvider = workspacePathProvider;
        _auditLogService = auditLogService;
        _clock = clock;
        _performanceInstrumentation = performanceInstrumentation
            ?? new PerformanceInstrumentation(new PerformanceBudgetOptions(), TimeProvider.System);
    }

    public async Task<IReadOnlyList<IncidentRecord>> GetIncidentsAsync(Guid caseId, CancellationToken ct)
    {
        if (caseId == Guid.Empty)
        {
            return Array.Empty<IncidentRecord>();
        }

        await _databaseInitializer.EnsureInitializedAsync(ct);

        await using var db = await _dbContextFactory.CreateDbContextAsync(ct);
        var incidents = await db.Incidents
            .AsNoTracking()
            .Include(item => item.Locations)
            .Include(item => item.PinnedResults)
            .Where(item => item.CaseId == caseId)
            .ToListAsync(ct);

        return OrderIncidentsForList(incidents)
            .Select(MapIncident)
            .ToList();
    }

    public async Task<IncidentRecord?> GetIncidentAsync(Guid caseId, Guid incidentId, CancellationToken ct)
    {
        if (caseId == Guid.Empty || incidentId == Guid.Empty)
        {
            return null;
        }

        await _databaseInitializer.EnsureInitializedAsync(ct);

        await using var db = await _dbContextFactory.CreateDbContextAsync(ct);
        var incident = await LoadIncidentEntityAsync(db, caseId, incidentId, asNoTracking: true, ct);
        return incident is null ? null : MapIncident(incident);
    }

    public async Task<IncidentRecord> SaveIncidentAsync(IncidentRecord incident, string correlationId, CancellationToken ct)
    {
        var prepared = PreparedIncident.ForSave(incident, _clock.UtcNow, correlationId);
        var logFields = BuildIncidentFields(prepared, prepared.CorrelationId);
        var eventName = prepared.IsCreate ? "IncidentCreate" : "IncidentUpdate";

        AppFileLogger.LogEvent(
            eventName: $"{eventName}Started",
            level: "INFO",
            message: prepared.IsCreate ? "Creating incident." : "Updating incident.",
            fields: logFields
        );

        IncidentRecord saved = null!;
        try
        {
            await _performanceInstrumentation.TrackAsync(
                new PerformanceOperationContext(
                    PerformanceOperationKinds.FeatureQuery,
                    prepared.IsCreate ? "Create" : "Update",
                    FeatureName: "OpenIncidentWorkspace",
                    CaseId: prepared.CaseId,
                    CorrelationId: prepared.CorrelationId
                ),
                async innerCt =>
                {
                    await _workspaceWriteGate.ExecuteWriteAsync(
                        operationName: prepared.IsCreate ? "Incident.Create" : "Incident.Update",
                        async writeCt =>
                        {
                            await _databaseInitializer.EnsureInitializedAsync(writeCt);
                            await using var db = await _dbContextFactory.CreateDbContextAsync(writeCt);
                            var entity = await LoadIncidentEntityAsync(
                                db,
                                prepared.CaseId,
                                prepared.IncidentId,
                                asNoTracking: false,
                                writeCt
                            );

                            var isCreate = entity is null;
                            entity ??= new IncidentRecordEntity
                            {
                                IncidentId = prepared.IncidentId,
                                CaseId = prepared.CaseId,
                                CreatedUtc = prepared.TimestampUtc
                            };

                            entity.CaseId = prepared.CaseId;
                            entity.Title = prepared.Title;
                            entity.IncidentType = prepared.IncidentType;
                            entity.Status = prepared.Status;
                            entity.SummaryNotes = prepared.SummaryNotes;
                            entity.PrimaryOccurrenceUtc = prepared.PrimaryOccurrenceUtc;
                            entity.OffenseWindowStartUtc = prepared.OffenseWindowStartUtc;
                            entity.OffenseWindowEndUtc = prepared.OffenseWindowEndUtc;
                            entity.UpdatedUtc = prepared.TimestampUtc;

                            if (isCreate)
                            {
                                db.Incidents.Add(entity);
                            }

                            entity.Locations.Clear();
                            foreach (var location in prepared.Locations)
                            {
                                entity.Locations.Add(new IncidentLocationRecord
                                {
                                    IncidentLocationId = location.IncidentLocationId,
                                    IncidentId = prepared.IncidentId,
                                    SortOrder = location.SortOrder,
                                    Label = location.Label,
                                    Latitude = location.Latitude,
                                    Longitude = location.Longitude,
                                    RadiusMeters = location.RadiusMeters,
                                    Notes = location.Notes
                                });
                            }

                            db.AuditEvents.Add(BuildAuditRecord(
                                timestampUtc: prepared.TimestampUtc,
                                caseId: prepared.CaseId,
                                actionType: isCreate ? "IncidentCreated" : "IncidentUpdated",
                                summary: isCreate
                                    ? $"Created incident '{prepared.Title}'."
                                    : $"Updated incident '{prepared.Title}'.",
                                payload: JsonSerializer.Serialize(new
                                {
                                    prepared.CorrelationId,
                                    prepared.IncidentId,
                                    prepared.Title,
                                    prepared.IncidentType,
                                    prepared.Status,
                                    prepared.PrimaryOccurrenceUtc,
                                    prepared.OffenseWindowStartUtc,
                                    prepared.OffenseWindowEndUtc,
                                    SceneCount = prepared.Locations.Count
                                })
                            ));

                            await db.SaveChangesAsync(writeCt);
                            saved = MapIncident(entity);
                        },
                        innerCt,
                        prepared.CorrelationId
                    );
                },
                ct
            );
        }
        catch (Exception ex)
        {
            AppFileLogger.LogEvent(
                eventName: $"{eventName}Failed",
                level: "ERROR",
                message: prepared.IsCreate ? "Incident create failed." : "Incident update failed.",
                ex: ex,
                fields: logFields
            );
            throw;
        }

        AppFileLogger.LogEvent(
            eventName: prepared.IsCreate ? "IncidentCreated" : "IncidentUpdated",
            level: "INFO",
            message: prepared.IsCreate ? "Incident created." : "Incident updated.",
            fields: logFields
        );

        return saved;
    }

    public async Task<IncidentCrossReferenceResult> RunCrossReferenceAsync(Guid caseId, Guid incidentId, string correlationId, CancellationToken ct)
    {
        if (caseId == Guid.Empty)
        {
            throw new ArgumentException("Case id is required.", nameof(caseId));
        }

        if (incidentId == Guid.Empty)
        {
            throw new ArgumentException("Incident id is required.", nameof(incidentId));
        }

        var normalizedCorrelationId = NormalizeCorrelationId(correlationId);
        var incident = await GetIncidentAsync(caseId, incidentId, ct)
            ?? throw new InvalidOperationException($"Incident {incidentId:D} was not found for case {caseId:D}.");
        var fields = new Dictionary<string, object?>
        {
            ["caseId"] = caseId.ToString("D"),
            ["incidentId"] = incidentId.ToString("D"),
            ["correlationId"] = normalizedCorrelationId,
            ["sceneCount"] = incident.Locations.Count
        };

        AppFileLogger.LogEvent(
            eventName: "IncidentCrossReferenceStarted",
            level: "INFO",
            message: "Incident cross-reference started.",
            fields: fields
        );

        try
        {
            return await _performanceInstrumentation.TrackAsync(
                new PerformanceOperationContext(
                    PerformanceOperationKinds.FeatureQuery,
                    "Run",
                    FeatureName: "OpenIncidentWorkspace",
                    CaseId: caseId,
                    CorrelationId: normalizedCorrelationId
                ),
                async innerCt =>
                {
                    await _databaseInitializer.EnsureInitializedAsync(innerCt);
                    await using var connection = new SqliteConnection($"Data Source={_workspacePathProvider.WorkspaceDbPath}");
                    await connection.OpenAsync(innerCt);

                    var messageResults = await QueryMessageResultsAsync(connection, incident, innerCt);
                    var locationResults = await QueryLocationResultsAsync(connection, incident, innerCt);
                    var timelineItems = BuildTimelineItems(incident, messageResults, locationResults);
                    var executedUtc = _clock.UtcNow.ToUniversalTime();
                    var result = new IncidentCrossReferenceResult(
                        Incident: incident,
                        CorrelationId: normalizedCorrelationId,
                        ExecutedUtc: executedUtc,
                        MessageResults: messageResults,
                        LocationResults: locationResults,
                        TimelineItems: timelineItems
                    );

                    await _auditLogService.AddAsync(
                        new AuditEvent
                        {
                            TimestampUtc = executedUtc,
                            Operator = Environment.UserName,
                            ActionType = "IncidentCrossReferenceExecuted",
                            CaseId = caseId,
                            Summary =
                                $"Incident cross-reference returned {messageResults.Count:0} message hit(s), "
                                + $"{locationResults.Count:0} location hit(s), and "
                                + $"{timelineItems.Count:0} timeline item(s).",
                            JsonPayload = JsonSerializer.Serialize(new
                            {
                                CorrelationId = normalizedCorrelationId,
                                IncidentId = incidentId,
                                incident.Title,
                                incident.IncidentType,
                                incident.Status,
                                incident.PrimaryOccurrenceUtc,
                                incident.OffenseWindowStartUtc,
                                incident.OffenseWindowEndUtc,
                                SceneCount = incident.Locations.Count,
                                MessageResultCount = messageResults.Count,
                                LocationResultCount = locationResults.Count,
                                TimelineItemCount = timelineItems.Count
                            })
                        },
                        innerCt
                    );

                    AppFileLogger.LogEvent(
                        eventName: "IncidentCrossReferenceCompleted",
                        level: "INFO",
                        message: "Incident cross-reference completed.",
                        fields: new Dictionary<string, object?>
                        {
                            ["caseId"] = caseId.ToString("D"),
                            ["incidentId"] = incidentId.ToString("D"),
                            ["correlationId"] = normalizedCorrelationId,
                            ["messageResultCount"] = messageResults.Count,
                            ["locationResultCount"] = locationResults.Count,
                            ["timelineItemCount"] = timelineItems.Count
                        }
                    );

                    return result;
                },
                ct
            );
        }
        catch (Exception ex)
        {
            AppFileLogger.LogEvent(
                eventName: "IncidentCrossReferenceFailed",
                level: "ERROR",
                message: "Incident cross-reference failed.",
                ex: ex,
                fields: fields
            );
            throw;
        }
    }

    public Task<IncidentRecord> PinMessageAsync(Guid caseId, Guid incidentId, TimelineRowDto message, string correlationId, CancellationToken ct)
    {
        if (message is null)
        {
            throw new ArgumentNullException(nameof(message));
        }

        var summary = string.IsNullOrWhiteSpace(message.Body)
            ? message.Preview
            : message.Body!;
        return PinResultAsync(
            caseId,
            incidentId,
            new PreparedPin(
                ResultType: "Message",
                SourceRecordId: message.MessageEventId,
                SourceEvidenceItemId: message.SourceEvidenceItemId,
                SourceLocator: message.SourceLocator,
                Citation: message.Citation,
                Title: string.IsNullOrWhiteSpace(message.ParticipantsSummary) ? "Message hit" : message.ParticipantsSummary,
                Summary: summary,
                EventUtc: message.TimestampUtc,
                Latitude: null,
                Longitude: null,
                SceneLabel: null,
                CorrelationId: NormalizeCorrelationId(correlationId)
            ),
            ct
        );
    }

    public Task<IncidentRecord> PinLocationAsync(Guid caseId, Guid incidentId, IncidentLocationHit locationHit, string correlationId, CancellationToken ct)
    {
        if (locationHit is null)
        {
            throw new ArgumentNullException(nameof(locationHit));
        }

        var location = locationHit.Location;
        var title = !string.IsNullOrWhiteSpace(location.SubjectDisplayName)
            ? location.SubjectDisplayName!
            : $"Location near {locationHit.SceneLabel}";
        var summary = $"{location.CoordinatesDisplay} | Scene {locationHit.SceneLabel} | {locationHit.DistanceDisplay}";

        return PinResultAsync(
            caseId,
            incidentId,
            new PreparedPin(
                ResultType: "Location",
                SourceRecordId: location.LocationObservationId,
                SourceEvidenceItemId: location.SourceEvidenceItemId,
                SourceLocator: location.SourceLocator,
                Citation: location.Citation,
                Title: title,
                Summary: summary,
                EventUtc: location.ObservedUtc,
                Latitude: location.Latitude,
                Longitude: location.Longitude,
                SceneLabel: locationHit.SceneLabel,
                CorrelationId: NormalizeCorrelationId(correlationId)
            ),
            ct
        );
    }

    private async Task<IncidentRecord> PinResultAsync(Guid caseId, Guid incidentId, PreparedPin pin, CancellationToken ct)
    {
        if (caseId == Guid.Empty)
        {
            throw new ArgumentException("Case id is required.", nameof(caseId));
        }

        if (incidentId == Guid.Empty)
        {
            throw new ArgumentException("Incident id is required.", nameof(incidentId));
        }

        var timestampUtc = _clock.UtcNow.ToUniversalTime();
        var fields = new Dictionary<string, object?>
        {
            ["caseId"] = caseId.ToString("D"),
            ["incidentId"] = incidentId.ToString("D"),
            ["resultType"] = pin.ResultType,
            ["sourceRecordId"] = pin.SourceRecordId.ToString("D"),
            ["correlationId"] = pin.CorrelationId
        };

        AppFileLogger.LogEvent(
            eventName: "IncidentPinStarted",
            level: "INFO",
            message: "Saving incident pin.",
            fields: fields
        );

        IncidentRecord saved = null!;
        try
        {
            await _workspaceWriteGate.ExecuteWriteAsync(
                operationName: "Incident.PinResult",
                async writeCt =>
                {
                    await _databaseInitializer.EnsureInitializedAsync(writeCt);
                    await using var db = await _dbContextFactory.CreateDbContextAsync(writeCt);
                    var incidentExists = await db.Incidents
                        .AsNoTracking()
                        .AnyAsync(item => item.CaseId == caseId && item.IncidentId == incidentId, writeCt);
                    if (!incidentExists)
                    {
                        throw new InvalidOperationException($"Incident {incidentId:D} was not found for case {caseId:D}.");
                    }

                    var existingPin = await db.IncidentPinnedResults
                        .AsNoTracking()
                        .AnyAsync(
                            item => item.IncidentId == incidentId
                                && item.SourceRecordId == pin.SourceRecordId
                                && item.ResultType == pin.ResultType,
                            writeCt
                        );

                    if (!existingPin)
                    {
                        db.IncidentPinnedResults.Add(new IncidentPinnedResultRecord
                        {
                            IncidentPinnedResultId = Guid.NewGuid(),
                            IncidentId = incidentId,
                            ResultType = pin.ResultType,
                            SourceRecordId = pin.SourceRecordId,
                            SourceEvidenceItemId = pin.SourceEvidenceItemId,
                            SourceLocator = pin.SourceLocator,
                            Citation = pin.Citation,
                            Title = pin.Title,
                            Summary = pin.Summary,
                            EventUtc = pin.EventUtc,
                            Latitude = pin.Latitude,
                            Longitude = pin.Longitude,
                            SceneLabel = pin.SceneLabel,
                            PinnedUtc = timestampUtc
                        });
                    }

                    db.AuditEvents.Add(BuildAuditRecord(
                        timestampUtc: timestampUtc,
                        caseId: caseId,
                        actionType: "IncidentPinnedResultAdded",
                        summary: $"Pinned {pin.ResultType.ToLowerInvariant()} result to incident.",
                        payload: JsonSerializer.Serialize(new
                        {
                            pin.CorrelationId,
                            IncidentId = incidentId,
                            pin.ResultType,
                            pin.SourceRecordId,
                            pin.SourceEvidenceItemId,
                            pin.SourceLocator,
                            pin.SceneLabel
                        })
                    ));

                    await db.SaveChangesAsync(writeCt);
                    var reloaded = await LoadIncidentEntityAsync(db, caseId, incidentId, asNoTracking: true, writeCt)
                        ?? throw new InvalidOperationException($"Incident {incidentId:D} was not found after pinning.");
                    saved = MapIncident(reloaded);
                },
                ct,
                pin.CorrelationId
            );
        }
        catch (Exception ex)
        {
            AppFileLogger.LogEvent(
                eventName: "IncidentPinFailed",
                level: "ERROR",
                message: "Incident pin save failed.",
                ex: ex,
                fields: fields
            );
            throw;
        }

        AppFileLogger.LogEvent(
            eventName: "IncidentPinnedResultAdded",
            level: "INFO",
            message: "Incident pin saved.",
            fields: fields
        );

        return saved;
    }

    private static async Task<IReadOnlyList<TimelineRowDto>> QueryMessageResultsAsync(
        SqliteConnection connection,
        IncidentRecord incident,
        CancellationToken ct
    )
    {
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
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
            ORDER BY me.TimestampUtc DESC, me.MessageEventId DESC;
            """;
        AddIncidentWindowParameters(command, incident);

        var rows = new List<TimelineRowDto>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            rows.Add(ReadMessageRow(reader));
        }

        return rows;
    }

    private static async Task<IReadOnlyList<IncidentLocationHit>> QueryLocationResultsAsync(
        SqliteConnection connection,
        IncidentRecord incident,
        CancellationToken ct
    )
    {
        if (incident.Locations.Count == 0)
        {
            return Array.Empty<IncidentLocationHit>();
        }

        var matches = new Dictionary<Guid, IncidentLocationHit>();
        foreach (var scene in incident.Locations.OrderBy(item => item.SortOrder))
        {
            var boundingBox = GeoMath.GetBoundingBox(scene.Latitude, scene.Longitude, scene.RadiusMeters);
            await using var command = connection.CreateCommand();
            command.CommandText = GetLocationRowsSql(GetBoundingBoxWhereSql("lo", boundingBox));
            AddIncidentWindowParameters(command, incident);
            AddBoundingBoxParameters(command, boundingBox);

            await using var reader = await command.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                var row = ReadLocationRow(reader);
                var distance = GeoMath.HaversineDistanceMeters(
                    scene.Latitude,
                    scene.Longitude,
                    row.Latitude,
                    row.Longitude
                );
                if (distance > scene.RadiusMeters)
                {
                    continue;
                }

                var label = string.IsNullOrWhiteSpace(scene.Label)
                    ? $"Scene {scene.SortOrder + 1}"
                    : scene.Label;
                var hit = new IncidentLocationHit(
                    IncidentLocationId: scene.IncidentLocationId,
                    SceneLabel: label,
                    DistanceMeters: distance,
                    Location: row with
                    {
                        DistanceFromCenterMeters = distance
                    }
                );

                if (matches.TryGetValue(row.LocationObservationId, out var existing)
                    && existing.DistanceMeters <= distance)
                {
                    continue;
                }

                matches[row.LocationObservationId] = hit;
            }
        }

        return matches.Values
            .OrderByDescending(item => item.Location.ObservedUtc)
            .ThenByDescending(item => item.Location.LocationObservationId)
            .ToList();
    }

    private static IReadOnlyList<IncidentTimelineItem> BuildTimelineItems(
        IncidentRecord incident,
        IReadOnlyList<TimelineRowDto> messages,
        IReadOnlyList<IncidentLocationHit> locationHits
    )
    {
        var anchorUtc = incident.PrimaryOccurrenceUtc?.ToUniversalTime()
            ?? incident.OffenseWindowStartUtc
                .AddTicks((incident.OffenseWindowEndUtc - incident.OffenseWindowStartUtc).Ticks / 2);
        var items = new List<IncidentTimelineItem>
        {
            new(
                MarkerType: "Incident",
                TimestampUtc: anchorUtc,
                Title: incident.Title,
                Summary: $"{incident.IncidentType} | {incident.Status}",
                Citation: null,
                IsAnchor: true,
                MessageEventId: null,
                LocationObservationId: null,
                SourceEvidenceItemId: null,
                SourceLocator: null
            )
        };

        items.AddRange(messages
            .Where(item => item.TimestampUtc.HasValue)
            .Select(item => new IncidentTimelineItem(
                MarkerType: "Message",
                TimestampUtc: item.TimestampUtc!.Value,
                Title: item.ParticipantsSummary,
                Summary: item.Preview,
                Citation: item.Citation,
                IsAnchor: false,
                MessageEventId: item.MessageEventId,
                LocationObservationId: null,
                SourceEvidenceItemId: item.SourceEvidenceItemId,
                SourceLocator: item.SourceLocator
            )));

        items.AddRange(locationHits.Select(hit => new IncidentTimelineItem(
            MarkerType: "Location",
            TimestampUtc: hit.Location.ObservedUtc,
            Title: hit.SceneLabel,
            Summary: $"{hit.Location.CoordinatesDisplay} | {hit.DistanceDisplay}",
            Citation: hit.Location.Citation,
            IsAnchor: false,
            MessageEventId: null,
            LocationObservationId: hit.Location.LocationObservationId,
            SourceEvidenceItemId: hit.Location.SourceEvidenceItemId,
            SourceLocator: hit.Location.SourceLocator
        )));

        return items
            .OrderBy(item => item.TimestampUtc)
            .ThenBy(item => item.IsAnchor ? 0 : 1)
            .ThenBy(item => item.MarkerType, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static AuditEventRecord BuildAuditRecord(
        DateTimeOffset timestampUtc,
        Guid caseId,
        string actionType,
        string summary,
        string payload
    )
    {
        return new AuditEventRecord
        {
            AuditEventId = Guid.NewGuid(),
            TimestampUtc = timestampUtc,
            Operator = Environment.UserName,
            ActionType = actionType,
            CaseId = caseId,
            Summary = summary,
            JsonPayload = payload
        };
    }

    private static IReadOnlyList<IncidentRecordEntity> OrderIncidentsForList(IEnumerable<IncidentRecordEntity> incidents)
    {
        // SQLite cannot translate DateTimeOffset ORDER BY for this query, so keep filtering in SQL and sort after materialization.
        return incidents
            .OrderByDescending(item => item.UpdatedUtc.ToUniversalTime())
            .ThenBy(item => item.Title, StringComparer.Ordinal)
            .ThenBy(item => item.IncidentId)
            .ToList();
    }

    private static Dictionary<string, object?> BuildIncidentFields(PreparedIncident incident, string correlationId)
    {
        return new Dictionary<string, object?>
        {
            ["caseId"] = incident.CaseId.ToString("D"),
            ["incidentId"] = incident.IncidentId.ToString("D"),
            ["title"] = incident.Title,
            ["incidentType"] = incident.IncidentType,
            ["status"] = incident.Status,
            ["sceneCount"] = incident.Locations.Count,
            ["correlationId"] = correlationId
        };
    }

    private static async Task<IncidentRecordEntity?> LoadIncidentEntityAsync(
        WorkspaceDbContext db,
        Guid caseId,
        Guid incidentId,
        bool asNoTracking,
        CancellationToken ct
    )
    {
        IQueryable<IncidentRecordEntity> query = db.Incidents
            .Include(item => item.Locations)
            .Include(item => item.PinnedResults);

        if (asNoTracking)
        {
            query = query.AsNoTracking();
        }

        return await query.FirstOrDefaultAsync(
            item => item.CaseId == caseId && item.IncidentId == incidentId,
            ct
        );
    }

    private static IncidentRecord MapIncident(IncidentRecordEntity entity)
    {
        return new IncidentRecord(
            IncidentId: entity.IncidentId,
            CaseId: entity.CaseId,
            Title: entity.Title,
            IncidentType: entity.IncidentType,
            Status: entity.Status,
            SummaryNotes: entity.SummaryNotes,
            PrimaryOccurrenceUtc: entity.PrimaryOccurrenceUtc?.ToUniversalTime(),
            OffenseWindowStartUtc: entity.OffenseWindowStartUtc.ToUniversalTime(),
            OffenseWindowEndUtc: entity.OffenseWindowEndUtc.ToUniversalTime(),
            CreatedUtc: entity.CreatedUtc.ToUniversalTime(),
            UpdatedUtc: entity.UpdatedUtc.ToUniversalTime(),
            Locations: entity.Locations
                .OrderBy(item => item.SortOrder)
                .Select(item => new IncidentLocation(
                    IncidentLocationId: item.IncidentLocationId,
                    SortOrder: item.SortOrder,
                    Label: item.Label,
                    Latitude: item.Latitude,
                    Longitude: item.Longitude,
                    RadiusMeters: item.RadiusMeters,
                    Notes: item.Notes
                ))
                .ToList(),
            PinnedResults: entity.PinnedResults
                .OrderByDescending(item => item.PinnedUtc)
                .ThenByDescending(item => item.IncidentPinnedResultId)
                .Select(item => new IncidentPinnedResult(
                    IncidentPinnedResultId: item.IncidentPinnedResultId,
                    ResultType: item.ResultType,
                    SourceRecordId: item.SourceRecordId,
                    SourceEvidenceItemId: item.SourceEvidenceItemId,
                    SourceLocator: item.SourceLocator,
                    Citation: item.Citation,
                    Title: item.Title,
                    Summary: item.Summary,
                    EventUtc: item.EventUtc?.ToUniversalTime(),
                    Latitude: item.Latitude,
                    Longitude: item.Longitude,
                    SceneLabel: item.SceneLabel,
                    PinnedUtc: item.PinnedUtc.ToUniversalTime()
                ))
                .ToList()
        );
    }

    private static TimelineRowDto ReadMessageRow(SqliteDataReader reader)
    {
        var timestampUtc = TryParseDateTimeOffset(reader.GetValue(3))
            ?? throw new InvalidOperationException("Message timestamp is required for incident cross-reference.");
        var senderRaw = reader.IsDBNull(5) ? null : reader.GetString(5);
        var recipientsRaw = reader.IsDBNull(6) ? null : reader.GetString(6);
        var resolvedSender = NormalizeGroupConcat(reader.IsDBNull(14) ? null : reader.GetString(14));
        var resolvedRecipients = NormalizeGroupConcat(reader.IsDBNull(15) ? null : reader.GetString(15));
        var senderDisplay = !string.IsNullOrWhiteSpace(resolvedSender) ? resolvedSender : senderRaw;
        var recipientsDisplay = !string.IsNullOrWhiteSpace(resolvedRecipients) ? resolvedRecipients : recipientsRaw;

        return new TimelineRowDto(
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
            SourceLocator: reader.IsDBNull(8) ? string.Empty : reader.GetString(8),
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

    private static LocationRowDto ReadLocationRow(SqliteDataReader reader)
    {
        return new LocationRowDto(
            LocationObservationId: ReadGuid(reader.GetValue(0)),
            CaseId: ReadGuid(reader.GetValue(1)),
            ObservedUtc: TryParseDateTimeOffset(reader.GetValue(2))
                ?? throw new InvalidOperationException("Location timestamp is required for incident cross-reference."),
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

    private static void AddIncidentWindowParameters(SqliteCommand command, IncidentRecord incident)
    {
        command.Parameters.AddWithValue("$caseId", incident.CaseId);
        command.Parameters.AddWithValue("$startUtc", incident.OffenseWindowStartUtc.ToString("O", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$endUtc", incident.OffenseWindowEndUtc.ToString("O", CultureInfo.InvariantCulture));
    }

    private static void AddBoundingBoxParameters(SqliteCommand command, GeoBoundingBox box)
    {
        command.Parameters.AddWithValue("$minLatitude", box.MinLatitude);
        command.Parameters.AddWithValue("$maxLatitude", box.MaxLatitude);
        command.Parameters.AddWithValue("$minLongitude", box.MinLongitude);
        command.Parameters.AddWithValue("$maxLongitude", box.MaxLongitude);
    }

    private static string GetLocationRowsSql(string boundingBoxWhereSql)
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
            {{boundingBoxWhereSql}}
            ORDER BY lo.ObservedUtc DESC, lo.LocationObservationId DESC;
            """;
    }

    private static string GetBoundingBoxWhereSql(string alias, GeoBoundingBox box)
    {
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
            DateTime dateTime => new DateTimeOffset(dateTime).ToUniversalTime(),
            string text when DateTimeOffset.TryParse(
                text,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal,
                out var parsed) => parsed.ToUniversalTime(),
            _ => null
        };
    }

    private static string BuildPreview(string? snippet, string? body)
    {
        var value = !string.IsNullOrWhiteSpace(snippet) ? snippet : body;
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
        var right = string.IsNullOrWhiteSpace(recipients) ? "(unknown recipients)" : recipients.Trim();
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

    private static string NormalizeCorrelationId(string correlationId)
    {
        return string.IsNullOrWhiteSpace(correlationId)
            ? Guid.NewGuid().ToString("N")
            : correlationId.Trim();
    }

    private sealed record PreparedIncident(
        Guid IncidentId,
        Guid CaseId,
        string Title,
        string IncidentType,
        string Status,
        string SummaryNotes,
        DateTimeOffset? PrimaryOccurrenceUtc,
        DateTimeOffset OffenseWindowStartUtc,
        DateTimeOffset OffenseWindowEndUtc,
        DateTimeOffset TimestampUtc,
        IReadOnlyList<IncidentLocation> Locations,
        string CorrelationId,
        bool IsCreate
    )
    {
        public static PreparedIncident ForSave(IncidentRecord incident, DateTimeOffset nowUtc, string correlationId)
        {
            if (incident.CaseId == Guid.Empty)
            {
                throw new ArgumentException("Incident case id is required.", nameof(incident));
            }

            var title = RequireValue(incident.Title, "Incident title");
            var incidentType = RequireValue(incident.IncidentType, "Incident type");
            var status = RequireValue(incident.Status, "Incident status");
            var summaryNotes = incident.SummaryNotes?.Trim() ?? string.Empty;
            var startUtc = incident.OffenseWindowStartUtc.ToUniversalTime();
            var endUtc = incident.OffenseWindowEndUtc.ToUniversalTime();
            if (startUtc > endUtc)
            {
                (startUtc, endUtc) = (endUtc, startUtc);
            }

            var locations = NormalizeLocations(incident.Locations);
            if (locations.Count == 0)
            {
                throw new ArgumentException("At least one incident scene location is required.", nameof(incident));
            }

            var incidentId = incident.IncidentId == Guid.Empty ? Guid.NewGuid() : incident.IncidentId;
            return new PreparedIncident(
                IncidentId: incidentId,
                CaseId: incident.CaseId,
                Title: title,
                IncidentType: incidentType,
                Status: status,
                SummaryNotes: summaryNotes,
                PrimaryOccurrenceUtc: incident.PrimaryOccurrenceUtc?.ToUniversalTime(),
                OffenseWindowStartUtc: startUtc,
                OffenseWindowEndUtc: endUtc,
                TimestampUtc: nowUtc.ToUniversalTime(),
                Locations: locations,
                CorrelationId: NormalizeCorrelationId(correlationId),
                IsCreate: incident.IncidentId == Guid.Empty
            );
        }

        private static IReadOnlyList<IncidentLocation> NormalizeLocations(IReadOnlyList<IncidentLocation> locations)
        {
            var normalized = new List<IncidentLocation>();
            for (var i = 0; i < locations.Count; i++)
            {
                var location = locations[i];
                var label = RequireValue(location.Label, $"Scene location {i + 1} label");
                if (location.Latitude is < -90d or > 90d)
                {
                    throw new ArgumentException($"Scene location {i + 1} latitude must be between -90 and 90.");
                }

                if (location.Longitude is < -180d or > 180d)
                {
                    throw new ArgumentException($"Scene location {i + 1} longitude must be between -180 and 180.");
                }

                if (location.RadiusMeters <= 0d)
                {
                    throw new ArgumentException($"Scene location {i + 1} radius must be greater than zero.");
                }

                normalized.Add(new IncidentLocation(
                    IncidentLocationId: Guid.NewGuid(),
                    SortOrder: i,
                    Label: label,
                    Latitude: location.Latitude,
                    Longitude: location.Longitude,
                    RadiusMeters: location.RadiusMeters,
                    Notes: location.Notes?.Trim() ?? string.Empty
                ));
            }

            return normalized;
        }

        private static string RequireValue(string? value, string fieldName)
        {
            var normalized = value?.Trim();
            return string.IsNullOrWhiteSpace(normalized)
                ? throw new ArgumentException($"{fieldName} is required.")
                : normalized;
        }
    }

    private sealed record PreparedPin(
        string ResultType,
        Guid SourceRecordId,
        Guid SourceEvidenceItemId,
        string SourceLocator,
        string Citation,
        string Title,
        string Summary,
        DateTimeOffset? EventUtc,
        double? Latitude,
        double? Longitude,
        string? SceneLabel,
        string CorrelationId
    );
}
