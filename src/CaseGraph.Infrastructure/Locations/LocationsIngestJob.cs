using CaseGraph.Core.Abstractions;
using CaseGraph.Core.Diagnostics;
using CaseGraph.Infrastructure.Persistence;
using CaseGraph.Infrastructure.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using System.Globalization;
using System.IO;

namespace CaseGraph.Infrastructure.Locations;

public sealed class LocationsIngestJob
{
    public const string IngestModuleVersion = "CaseGraph.LocationsIngest/v1";
    private const int SaveBatchSize = 200;

    private readonly IDbContextFactory<WorkspaceDbContext> _dbContextFactory;
    private readonly IWorkspaceDatabaseInitializer _databaseInitializer;
    private readonly IWorkspacePathProvider _workspacePathProvider;
    private readonly IWorkspaceWriteGate _workspaceWriteGate;
    private readonly IClock _clock;
    private readonly LocationCsvParser _csvParser;
    private readonly LocationJsonParser _jsonParser;
    private readonly LocationPlistParser _plistParser;

    public LocationsIngestJob(
        IDbContextFactory<WorkspaceDbContext> dbContextFactory,
        IWorkspaceDatabaseInitializer databaseInitializer,
        IWorkspacePathProvider workspacePathProvider,
        IWorkspaceWriteGate workspaceWriteGate,
        IClock clock,
        LocationCsvParser csvParser,
        LocationJsonParser jsonParser,
        LocationPlistParser plistParser
    )
    {
        _dbContextFactory = dbContextFactory;
        _databaseInitializer = databaseInitializer;
        _workspacePathProvider = workspacePathProvider;
        _workspaceWriteGate = workspaceWriteGate;
        _clock = clock;
        _csvParser = csvParser;
        _jsonParser = jsonParser;
        _plistParser = plistParser;
    }

    public async Task<LocationsIngestResult> IngestAsync(
        Guid caseId,
        Guid evidenceItemId,
        IProgress<LocationsIngestProgress>? progress,
        CancellationToken ct
    )
    {
        if (caseId == Guid.Empty)
        {
            throw new ArgumentException("Case id is required.", nameof(caseId));
        }

        if (evidenceItemId == Guid.Empty)
        {
            throw new ArgumentException("Evidence item id is required.", nameof(evidenceItemId));
        }

        await _databaseInitializer.EnsureInitializedAsync(ct);

        EvidenceItemRecord? evidence;
        await using (var db = await _dbContextFactory.CreateDbContextAsync(ct))
        {
            evidence = await db.EvidenceItems
                .AsNoTracking()
                .FirstOrDefaultAsync(
                    item => item.CaseId == caseId && item.EvidenceItemId == evidenceItemId,
                    ct
                );
        }

        if (evidence is null)
        {
            throw new FileNotFoundException($"Evidence item was not found for {evidenceItemId:D}.");
        }

        var storedAbsolutePath = Path.Combine(
            _workspacePathProvider.CasesRoot,
            caseId.ToString("D"),
            evidence.StoredRelativePath.Replace('/', Path.DirectorySeparatorChar)
        );
        var sourceLabel = evidence.OriginalFileName;
        var extension = evidence.FileExtension?.Trim().ToLowerInvariant() ?? string.Empty;

        progress?.Report(new LocationsIngestProgress(
            FractionComplete: 0.05,
            Phase: "Preparing locations ingest...",
            Processed: 0,
            Accepted: 0,
            Skipped: 0
        ));

        if (!File.Exists(storedAbsolutePath))
        {
            AppFileLogger.LogEvent(
                eventName: "LocationsIngestFileMissing",
                level: "WARN",
                message: "Stored evidence file for locations ingest does not exist.",
                fields: new Dictionary<string, object?>
                {
                    ["caseId"] = caseId.ToString("D"),
                    ["evidenceItemId"] = evidenceItemId.ToString("D"),
                    ["path"] = storedAbsolutePath
                }
            );
            return new LocationsIngestResult(
                ProcessedCount: 0,
                InsertedCount: 0,
                SkippedCount: 0,
                ReplacedCount: 0,
                FileErrorCount: 1,
                UnknownFieldNames: Array.Empty<string>()
            );
        }

        var parser = ResolveParser(extension);
        if (parser is null)
        {
            AppFileLogger.LogEvent(
                eventName: "LocationsIngestUnsupportedFileType",
                level: "WARN",
                message: "Evidence type is not supported by locations ingest.",
                fields: new Dictionary<string, object?>
                {
                    ["caseId"] = caseId.ToString("D"),
                    ["evidenceItemId"] = evidenceItemId.ToString("D"),
                    ["fileExtension"] = extension
                }
            );

            return new LocationsIngestResult(
                ProcessedCount: 0,
                InsertedCount: 0,
                SkippedCount: 0,
                ReplacedCount: 0,
                FileErrorCount: 1,
                UnknownFieldNames: Array.Empty<string>()
            );
        }

        var result = await _workspaceWriteGate.ExecuteWriteWithResultAsync(
            operationName: "LocationsIngest.Persist",
            async writeCt =>
            {
                await _databaseInitializer.EnsureInitializedAsync(writeCt);
                await using var db = await _dbContextFactory.CreateDbContextAsync(writeCt);
                await using var transaction = await db.Database.BeginTransactionAsync(writeCt);

                var replacedCount = await db.LocationObservations
                    .Where(item => item.CaseId == caseId && item.SourceEvidenceItemId == evidenceItemId)
                    .ExecuteDeleteAsync(writeCt);

                var pendingInserts = 0;
                LocationParserResult parseResult = await parser.ParseAsync(
                    storedAbsolutePath,
                    sourceLabel,
                    async (observation, callbackCt) =>
                    {
                        callbackCt.ThrowIfCancellationRequested();
                        db.LocationObservations.Add(
                            new LocationObservationRecord
                            {
                                LocationObservationId = Guid.NewGuid(),
                                CaseId = caseId,
                                ObservedUtc = observation.ObservedUtc,
                                Latitude = observation.Latitude,
                                Longitude = observation.Longitude,
                                AccuracyMeters = observation.AccuracyMeters,
                                AltitudeMeters = observation.AltitudeMeters,
                                SpeedMps = observation.SpeedMps,
                                HeadingDegrees = observation.HeadingDegrees,
                                SourceType = observation.SourceType,
                                SourceLabel = observation.SourceLabel,
                                SubjectType = observation.SubjectType,
                                SubjectId = observation.SubjectId,
                                SourceEvidenceItemId = evidenceItemId,
                                SourceLocator = observation.SourceLocator,
                                IngestModuleVersion = IngestModuleVersion,
                                CreatedUtc = _clock.UtcNow.ToUniversalTime()
                            }
                        );

                        pendingInserts++;
                        if (pendingInserts < SaveBatchSize)
                        {
                            return;
                        }

                        await db.SaveChangesAsync(callbackCt);
                        db.ChangeTracker.Clear();
                        pendingInserts = 0;
                    },
                    parserProgress =>
                    {
                        progress?.Report(new LocationsIngestProgress(
                            FractionComplete: 0.10 + (Math.Clamp(parserProgress.FractionComplete, 0, 1) * 0.85),
                            Phase: parserProgress.Phase,
                            Processed: parserProgress.ProcessedCount,
                            Accepted: parserProgress.AcceptedCount,
                            Skipped: parserProgress.SkippedCount
                        ));
                    },
                    writeCt
                );

                if (pendingInserts > 0)
                {
                    await db.SaveChangesAsync(writeCt);
                    db.ChangeTracker.Clear();
                }

                await transaction.CommitAsync(writeCt);

                return new LocationsIngestResult(
                    ProcessedCount: parseResult.ProcessedCount,
                    InsertedCount: parseResult.AcceptedCount,
                    SkippedCount: parseResult.SkippedCount,
                    ReplacedCount: replacedCount,
                    FileErrorCount: 0,
                    UnknownFieldNames: parseResult.UnknownFieldNames
                );
            },
            ct,
            correlationId: AppFileLogger.GetScopeValue("correlationId"),
            fields: new Dictionary<string, object?>
            {
                ["caseId"] = caseId.ToString("D"),
                ["evidenceItemId"] = evidenceItemId.ToString("D")
            }
        );

        foreach (var field in result.UnknownFieldNames)
        {
            AppFileLogger.LogEvent(
                eventName: "LocationsIngestUnknownField",
                level: "INFO",
                message: "Locations ingest skipped unknown schema field.",
                fields: new Dictionary<string, object?>
                {
                    ["field"] = field,
                    ["file"] = sourceLabel,
                    ["evidenceItemId"] = evidenceItemId.ToString("D")
                }
            );
        }

        progress?.Report(new LocationsIngestProgress(
            FractionComplete: 1,
            Phase: result.Summary,
            Processed: result.ProcessedCount,
            Accepted: result.InsertedCount,
            Skipped: result.SkippedCount
        ));

        return result;
    }

    private ILocationObservationParser? ResolveParser(string extension)
    {
        return extension switch
        {
            ".csv" => _csvParser,
            ".json" => _jsonParser,
            ".plist" => _plistParser,
            _ => null
        };
    }
}

public sealed record LocationsIngestResult(
    int ProcessedCount,
    int InsertedCount,
    int SkippedCount,
    int ReplacedCount,
    int FileErrorCount,
    IReadOnlyList<string> UnknownFieldNames
)
{
    public string Summary
    {
        get
        {
            var summary = $"Ingested {InsertedCount:0} location observation(s); skipped {SkippedCount:0} row(s).";
            if (ReplacedCount > 0)
            {
                summary += $" Replaced {ReplacedCount:0} prior row(s) for the same evidence item.";
            }

            if (FileErrorCount > 0)
            {
                summary += $" Encountered {FileErrorCount:0} file-level error(s).";
            }

            return summary;
        }
    }
}

public sealed record LocationsIngestProgress(
    double FractionComplete,
    string Phase,
    int? Processed,
    int? Accepted,
    int? Skipped
);

public interface ILocationObservationParser
{
    Task<LocationParserResult> ParseAsync(
        string filePath,
        string sourceLabel,
        Func<LocationParsedObservation, CancellationToken, ValueTask> onObservation,
        Action<LocationParserProgress>? onProgress,
        CancellationToken ct
    );
}

public sealed record LocationParsedObservation(
    DateTimeOffset ObservedUtc,
    double Latitude,
    double Longitude,
    double? AccuracyMeters,
    double? AltitudeMeters,
    double? SpeedMps,
    double? HeadingDegrees,
    string SourceType,
    string SourceLabel,
    string? SubjectType,
    Guid? SubjectId,
    string SourceLocator
);

public sealed record LocationParserResult(
    int ProcessedCount,
    int AcceptedCount,
    int SkippedCount,
    IReadOnlyList<string> UnknownFieldNames
);

public readonly record struct LocationParserProgress(
    double FractionComplete,
    int ProcessedCount,
    int AcceptedCount,
    int SkippedCount,
    string Phase
);

internal static class LocationParseHelpers
{
    public static readonly string[] TimestampKeys =
    [
        "timestamp",
        "time",
        "datetime",
        "date",
        "recordedat",
        "createdat",
        "observedat",
        "observedutc"
    ];

    public static readonly string[] LatitudeKeys =
    [
        "latitude",
        "lat",
        "y"
    ];

    public static readonly string[] LongitudeKeys =
    [
        "longitude",
        "lon",
        "lng",
        "long",
        "x"
    ];

    public static readonly string[] AccuracyKeys =
    [
        "accuracymeters",
        "accuracy",
        "horizontalaccuracy",
        "haccuracy"
    ];

    public static readonly string[] AltitudeKeys =
    [
        "altitudemeters",
        "altitude"
    ];

    public static readonly string[] SpeedKeys =
    [
        "speedmps",
        "speed",
        "velocity"
    ];

    public static readonly string[] HeadingKeys =
    [
        "headingdegrees",
        "heading",
        "course"
    ];

    public static readonly string[] SubjectTypeKeys =
    [
        "subjecttype",
        "subject_kind",
        "subjectkind"
    ];

    public static readonly string[] SubjectIdKeys =
    [
        "subjectid",
        "subject_guid",
        "subjectguid"
    ];

    public static readonly HashSet<string> KnownKeys = new(
        TimestampKeys
            .Concat(LatitudeKeys)
            .Concat(LongitudeKeys)
            .Concat(AccuracyKeys)
            .Concat(AltitudeKeys)
            .Concat(SpeedKeys)
            .Concat(HeadingKeys)
            .Concat(SubjectTypeKeys)
            .Concat(SubjectIdKeys),
        StringComparer.Ordinal
    );

    public static string NormalizeKey(string key)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            return string.Empty;
        }

        return new string(
            key.Trim()
                .ToLowerInvariant()
                .Where(char.IsLetterOrDigit)
                .ToArray()
        );
    }

    public static string? GetFirstValue(
        IReadOnlyDictionary<string, string?> map,
        IEnumerable<string> normalizedKeys
    )
    {
        foreach (var key in normalizedKeys)
        {
            if (!map.TryGetValue(key, out var value))
            {
                continue;
            }

            if (string.IsNullOrWhiteSpace(value))
            {
                continue;
            }

            return value.Trim();
        }

        return null;
    }

    public static bool LooksLikeLocationCandidate(IReadOnlyDictionary<string, string?> map)
    {
        return HasAny(map, TimestampKeys)
            || HasAny(map, LatitudeKeys)
            || HasAny(map, LongitudeKeys);
    }

    public static bool TryParseTimestampUtc(string? raw, out DateTimeOffset observedUtc)
    {
        observedUtc = default;
        if (string.IsNullOrWhiteSpace(raw))
        {
            return false;
        }

        var value = raw.Trim();
        if (DateTimeOffset.TryParse(
                value,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AllowWhiteSpaces | DateTimeStyles.AssumeUniversal,
                out var dto))
        {
            observedUtc = dto.ToUniversalTime();
            return true;
        }

        if (DateTimeOffset.TryParse(
                value,
                CultureInfo.CurrentCulture,
                DateTimeStyles.AllowWhiteSpaces | DateTimeStyles.AssumeUniversal,
                out dto))
        {
            observedUtc = dto.ToUniversalTime();
            return true;
        }

        if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var numeric))
        {
            try
            {
                if (Math.Abs(numeric) >= 1_000_000_000_000)
                {
                    observedUtc = DateTimeOffset
                        .FromUnixTimeMilliseconds(Convert.ToInt64(Math.Round(numeric, MidpointRounding.AwayFromZero)))
                        .ToUniversalTime();
                    return true;
                }

                if (Math.Abs(numeric) >= 946_684_800)
                {
                    observedUtc = DateTimeOffset
                        .FromUnixTimeSeconds(Convert.ToInt64(Math.Round(numeric, MidpointRounding.AwayFromZero)))
                        .ToUniversalTime();
                    return true;
                }

                if (Math.Abs(numeric) >= 1_000_000)
                {
                    observedUtc = new DateTimeOffset(
                        new DateTime(2001, 1, 1, 0, 0, 0, DateTimeKind.Utc)
                            .AddSeconds(numeric)
                    );
                    return true;
                }
            }
            catch (ArgumentOutOfRangeException)
            {
                return false;
            }
        }

        return false;
    }

    public static bool TryParseLatitude(string? raw, out double latitude)
    {
        latitude = 0;
        if (!TryParseDouble(raw, out var parsed))
        {
            return false;
        }

        if (parsed < -90 || parsed > 90)
        {
            return false;
        }

        latitude = parsed;
        return true;
    }

    public static bool TryParseLongitude(string? raw, out double longitude)
    {
        longitude = 0;
        if (!TryParseDouble(raw, out var parsed))
        {
            return false;
        }

        if (parsed < -180 || parsed > 180)
        {
            return false;
        }

        longitude = parsed;
        return true;
    }

    public static double? TryParseOptionalDouble(string? raw)
    {
        return TryParseDouble(raw, out var parsed)
            ? parsed
            : null;
    }

    public static string? NormalizeSubjectType(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        var value = raw.Trim().ToLowerInvariant();
        return value switch
        {
            "target" => "Target",
            "globalperson" or "global_person" or "global" => "GlobalPerson",
            _ => null
        };
    }

    public static Guid? TryParseGuidNullable(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        return Guid.TryParse(raw.Trim(), out var parsed)
            ? parsed
            : null;
    }

    public static void LogRowSkipped(
        string eventName,
        string parser,
        string locator,
        string reason
    )
    {
        AppFileLogger.LogEvent(
            eventName: eventName,
            level: "WARN",
            message: "Locations ingest skipped malformed row/object.",
            fields: new Dictionary<string, object?>
            {
                ["parser"] = parser,
                ["locator"] = locator,
                ["reason"] = reason
            }
        );
    }

    public static string EscapePathSegment(string value)
    {
        return Uri.EscapeDataString(value);
    }

    private static bool HasAny(
        IReadOnlyDictionary<string, string?> map,
        IEnumerable<string> normalizedKeys
    )
    {
        return normalizedKeys.Any(key => map.ContainsKey(key));
    }

    private static bool TryParseDouble(string? raw, out double value)
    {
        value = 0;
        if (string.IsNullOrWhiteSpace(raw))
        {
            return false;
        }

        var normalized = raw.Trim();
        if (double.TryParse(
                normalized,
                NumberStyles.Float | NumberStyles.AllowThousands,
                CultureInfo.InvariantCulture,
                out value))
        {
            return true;
        }

        return double.TryParse(
            normalized,
            NumberStyles.Float | NumberStyles.AllowThousands,
            CultureInfo.CurrentCulture,
            out value
        );
    }
}
