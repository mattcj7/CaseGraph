using System.Text.Json;
using CaseGraph.Core.Diagnostics;

namespace CaseGraph.Infrastructure.Locations;

public sealed class LocationJsonParser : ILocationObservationParser
{
    public async Task<LocationParserResult> ParseAsync(
        string filePath,
        string sourceLabel,
        Func<LocationParsedObservation, CancellationToken, ValueTask> onObservation,
        Action<LocationParserProgress>? onProgress,
        CancellationToken ct
    )
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceLabel);
        ArgumentNullException.ThrowIfNull(onObservation);

        var unknownFields = new HashSet<string>(StringComparer.Ordinal);
        var state = new ParseState();

        await using var stream = new FileStream(
            filePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 1024 * 64,
            FileOptions.Asynchronous | FileOptions.SequentialScan
        );

        var readAsArray = false;
        try
        {
            var index = 0;
            await foreach (var element in JsonSerializer.DeserializeAsyncEnumerable<JsonElement>(stream, cancellationToken: ct))
            {
                var pointer = $"/{index}";
                await ProcessElementAsync(element, pointer);
                index++;
            }

            readAsArray = true;
        }
        catch (JsonException)
        {
            readAsArray = false;
        }

        if (!readAsArray)
        {
            try
            {
                stream.Seek(0, SeekOrigin.Begin);
                using var document = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
                await ProcessElementAsync(document.RootElement, string.Empty);
            }
            catch (JsonException ex)
            {
                AppFileLogger.LogEvent(
                    eventName: "LocationsIngestJsonParseFailed",
                    level: "WARN",
                    message: "JSON parser could not read location artifact; file was skipped.",
                    ex: ex,
                    fields: new Dictionary<string, object?>
                    {
                        ["file"] = sourceLabel
                    }
                );
                state.Skipped++;
            }
        }

        return new LocationParserResult(
            state.Processed,
            state.Accepted,
            state.Skipped,
            unknownFields.OrderBy(item => item, StringComparer.Ordinal).ToArray()
        );

        async ValueTask ProcessElementAsync(JsonElement element, string pointer)
        {
            ct.ThrowIfCancellationRequested();

            switch (element.ValueKind)
            {
                case JsonValueKind.Array:
                {
                    var index = 0;
                    foreach (var item in element.EnumerateArray())
                    {
                        await ProcessElementAsync(item, $"{pointer}/{index}");
                        index++;
                    }

                    return;
                }
                case JsonValueKind.Object:
                {
                    state.Processed++;
                    var map = new Dictionary<string, string?>(StringComparer.Ordinal);
                    var children = new List<(string Token, JsonElement Value)>();
                    foreach (var property in element.EnumerateObject())
                    {
                        var normalizedKey = LocationParseHelpers.NormalizeKey(property.Name);
                        if (!string.IsNullOrWhiteSpace(normalizedKey))
                        {
                            map[normalizedKey] = TryReadScalar(property.Value);
                            if (!LocationParseHelpers.KnownKeys.Contains(normalizedKey))
                            {
                                unknownFields.Add(normalizedKey);
                            }
                        }

                        if (property.Value.ValueKind is JsonValueKind.Array or JsonValueKind.Object)
                        {
                            children.Add((property.Name, property.Value));
                        }
                    }

                    var effectivePointer = string.IsNullOrWhiteSpace(pointer)
                        ? "/"
                        : pointer;
                    var sourceLocator = $"json://{LocationParseHelpers.EscapePathSegment(sourceLabel)}#ptr={effectivePointer}";
                    if (TryBuildObservation(
                            map,
                            sourceLabel,
                            sourceLocator,
                            out var observation,
                            out var reason))
                    {
                        state.Accepted++;
                        await onObservation(observation!, ct);
                    }
                    else if (LocationParseHelpers.LooksLikeLocationCandidate(map))
                    {
                        state.Skipped++;
                        LocationParseHelpers.LogRowSkipped(
                            eventName: "LocationsIngestJsonObjectSkipped",
                            parser: "JSON",
                            locator: sourceLocator,
                            reason: reason
                        );
                    }

                    if (onProgress is not null && state.Processed % 50 == 0)
                    {
                        var fraction = stream.Length <= 0
                            ? 1
                            : Math.Clamp(stream.Position / (double)stream.Length, 0, 1);
                        onProgress(new LocationParserProgress(
                            FractionComplete: fraction,
                            ProcessedCount: state.Processed,
                            AcceptedCount: state.Accepted,
                            SkippedCount: state.Skipped,
                            Phase: "Parsing JSON locations..."
                        ));
                    }

                    foreach (var (token, child) in children)
                    {
                        var childPointer = AppendPointer(pointer, token);
                        await ProcessElementAsync(child, childPointer);
                    }

                    return;
                }
                default:
                    return;
            }
        }
    }

    private static bool TryBuildObservation(
        IReadOnlyDictionary<string, string?> map,
        string sourceLabel,
        string sourceLocator,
        out LocationParsedObservation? observation,
        out string reason
    )
    {
        observation = null;
        reason = string.Empty;

        var timestampRaw = LocationParseHelpers.GetFirstValue(map, LocationParseHelpers.TimestampKeys);
        if (!LocationParseHelpers.TryParseTimestampUtc(timestampRaw, out var observedUtc))
        {
            reason = "Timestamp is missing or invalid.";
            return false;
        }

        var latitudeRaw = LocationParseHelpers.GetFirstValue(map, LocationParseHelpers.LatitudeKeys);
        if (!LocationParseHelpers.TryParseLatitude(latitudeRaw, out var latitude))
        {
            reason = "Latitude is missing or invalid.";
            return false;
        }

        var longitudeRaw = LocationParseHelpers.GetFirstValue(map, LocationParseHelpers.LongitudeKeys);
        if (!LocationParseHelpers.TryParseLongitude(longitudeRaw, out var longitude))
        {
            reason = "Longitude is missing or invalid.";
            return false;
        }

        var accuracy = LocationParseHelpers.TryParseOptionalDouble(
            LocationParseHelpers.GetFirstValue(map, LocationParseHelpers.AccuracyKeys)
        );
        var altitude = LocationParseHelpers.TryParseOptionalDouble(
            LocationParseHelpers.GetFirstValue(map, LocationParseHelpers.AltitudeKeys)
        );
        var speed = LocationParseHelpers.TryParseOptionalDouble(
            LocationParseHelpers.GetFirstValue(map, LocationParseHelpers.SpeedKeys)
        );
        var heading = LocationParseHelpers.TryParseOptionalDouble(
            LocationParseHelpers.GetFirstValue(map, LocationParseHelpers.HeadingKeys)
        );

        var subjectTypeRaw = LocationParseHelpers.GetFirstValue(map, LocationParseHelpers.SubjectTypeKeys);
        var subjectIdRaw = LocationParseHelpers.GetFirstValue(map, LocationParseHelpers.SubjectIdKeys);
        var subjectType = LocationParseHelpers.NormalizeSubjectType(subjectTypeRaw);
        var subjectId = LocationParseHelpers.TryParseGuidNullable(subjectIdRaw);

        observation = new LocationParsedObservation(
            ObservedUtc: observedUtc,
            Latitude: latitude,
            Longitude: longitude,
            AccuracyMeters: accuracy,
            AltitudeMeters: altitude,
            SpeedMps: speed,
            HeadingDegrees: heading,
            SourceType: "JSON",
            SourceLabel: sourceLabel,
            SubjectType: subjectType,
            SubjectId: subjectId,
            SourceLocator: sourceLocator
        );
        return true;
    }

    private static string? TryReadScalar(JsonElement value)
    {
        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Number => value.ToString(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            JsonValueKind.Null or JsonValueKind.Undefined => null,
            _ => null
        };
    }

    private static string AppendPointer(string pointer, string token)
    {
        var escaped = token
            .Replace("~", "~0", StringComparison.Ordinal)
            .Replace("/", "~1", StringComparison.Ordinal);

        if (string.IsNullOrWhiteSpace(pointer))
        {
            return $"/{escaped}";
        }

        return $"{pointer}/{escaped}";
    }

    private sealed class ParseState
    {
        public int Processed { get; set; }

        public int Accepted { get; set; }

        public int Skipped { get; set; }
    }
}
