using CaseGraph.Core.Diagnostics;
using System.IO;

namespace CaseGraph.Infrastructure.Locations;

public sealed class LocationCsvParser : ILocationObservationParser
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
        var processed = 0;
        var accepted = 0;
        var skipped = 0;

        await using var stream = new FileStream(
            filePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 1024 * 64,
            FileOptions.Asynchronous | FileOptions.SequentialScan
        );
        using var reader = new StreamReader(stream);
        var headerLine = await reader.ReadLineAsync(ct);
        if (string.IsNullOrWhiteSpace(headerLine))
        {
            return new LocationParserResult(0, 0, 0, Array.Empty<string>());
        }

        var headerValues = ParseCsvLine(headerLine);
        var normalizedHeaders = headerValues
            .Select(LocationParseHelpers.NormalizeKey)
            .ToArray();

        foreach (var header in normalizedHeaders)
        {
            if (string.IsNullOrWhiteSpace(header))
            {
                continue;
            }

            if (LocationParseHelpers.KnownKeys.Contains(header))
            {
                continue;
            }

            unknownFields.Add(header);
        }

        var rowNumber = 1;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var line = await reader.ReadLineAsync(ct);
            if (line is null)
            {
                break;
            }

            rowNumber++;
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            processed++;
            var rowValues = ParseCsvLine(line);
            var rowMap = new Dictionary<string, string?>(StringComparer.Ordinal);
            for (var index = 0; index < normalizedHeaders.Length; index++)
            {
                var key = normalizedHeaders[index];
                if (string.IsNullOrWhiteSpace(key))
                {
                    continue;
                }

                rowMap[key] = index < rowValues.Count
                    ? rowValues[index]
                    : null;
            }

            var sourceLocator = $"csv://{LocationParseHelpers.EscapePathSegment(sourceLabel)}#row={rowNumber}";
            if (!TryBuildObservation(
                    rowMap,
                    sourceLabel,
                    sourceLocator,
                    out var observation,
                    out var reason))
            {
                skipped++;
                if (LocationParseHelpers.LooksLikeLocationCandidate(rowMap))
                {
                    LocationParseHelpers.LogRowSkipped(
                        eventName: "LocationsIngestCsvRowSkipped",
                        parser: "CSV",
                        locator: sourceLocator,
                        reason: reason
                    );
                }

                ReportProgress();
                continue;
            }

            accepted++;
            await onObservation(observation!, ct);
            ReportProgress();
        }

        foreach (var field in unknownFields)
        {
            AppFileLogger.LogEvent(
                eventName: "LocationsIngestCsvUnknownField",
                level: "INFO",
                message: "CSV column is not recognized by locations ingest and was ignored.",
                fields: new Dictionary<string, object?>
                {
                    ["field"] = field,
                    ["file"] = sourceLabel
                }
            );
        }

        return new LocationParserResult(
            processed,
            accepted,
            skipped,
            unknownFields.OrderBy(item => item, StringComparer.Ordinal).ToArray()
        );

        void ReportProgress()
        {
            if (onProgress is null)
            {
                return;
            }

            if ((processed + accepted + skipped) % 50 != 0 && processed > 0)
            {
                return;
            }

            var fraction = stream.Length <= 0
                ? 1
                : Math.Clamp(stream.Position / (double)stream.Length, 0, 1);
            onProgress(new LocationParserProgress(
                FractionComplete: fraction,
                ProcessedCount: processed,
                AcceptedCount: accepted,
                SkippedCount: skipped,
                Phase: "Parsing CSV locations..."
            ));
        }
    }

    private static bool TryBuildObservation(
        IReadOnlyDictionary<string, string?> rowMap,
        string sourceLabel,
        string sourceLocator,
        out LocationParsedObservation? observation,
        out string reason
    )
    {
        observation = null;
        reason = string.Empty;

        var timestampRaw = LocationParseHelpers.GetFirstValue(rowMap, LocationParseHelpers.TimestampKeys);
        if (!LocationParseHelpers.TryParseTimestampUtc(timestampRaw, out var observedUtc))
        {
            reason = "Timestamp is missing or invalid.";
            return false;
        }

        var latitudeRaw = LocationParseHelpers.GetFirstValue(rowMap, LocationParseHelpers.LatitudeKeys);
        if (!LocationParseHelpers.TryParseLatitude(latitudeRaw, out var latitude))
        {
            reason = "Latitude is missing or invalid.";
            return false;
        }

        var longitudeRaw = LocationParseHelpers.GetFirstValue(rowMap, LocationParseHelpers.LongitudeKeys);
        if (!LocationParseHelpers.TryParseLongitude(longitudeRaw, out var longitude))
        {
            reason = "Longitude is missing or invalid.";
            return false;
        }

        var accuracy = LocationParseHelpers.TryParseOptionalDouble(
            LocationParseHelpers.GetFirstValue(rowMap, LocationParseHelpers.AccuracyKeys)
        );
        var altitude = LocationParseHelpers.TryParseOptionalDouble(
            LocationParseHelpers.GetFirstValue(rowMap, LocationParseHelpers.AltitudeKeys)
        );
        var speed = LocationParseHelpers.TryParseOptionalDouble(
            LocationParseHelpers.GetFirstValue(rowMap, LocationParseHelpers.SpeedKeys)
        );
        var heading = LocationParseHelpers.TryParseOptionalDouble(
            LocationParseHelpers.GetFirstValue(rowMap, LocationParseHelpers.HeadingKeys)
        );

        var subjectTypeRaw = LocationParseHelpers.GetFirstValue(rowMap, LocationParseHelpers.SubjectTypeKeys);
        var subjectIdRaw = LocationParseHelpers.GetFirstValue(rowMap, LocationParseHelpers.SubjectIdKeys);
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
            SourceType: "CSV",
            SourceLabel: sourceLabel,
            SubjectType: subjectType,
            SubjectId: subjectId,
            SourceLocator: sourceLocator
        );
        return true;
    }

    private static List<string> ParseCsvLine(string line)
    {
        var values = new List<string>();
        if (line.Length == 0)
        {
            values.Add(string.Empty);
            return values;
        }

        var current = new System.Text.StringBuilder(line.Length);
        var insideQuotes = false;

        for (var i = 0; i < line.Length; i++)
        {
            var ch = line[i];
            if (insideQuotes)
            {
                if (ch == '"')
                {
                    var isEscapedQuote = i + 1 < line.Length && line[i + 1] == '"';
                    if (isEscapedQuote)
                    {
                        current.Append('"');
                        i++;
                        continue;
                    }

                    insideQuotes = false;
                    continue;
                }

                current.Append(ch);
                continue;
            }

            if (ch == ',')
            {
                values.Add(current.ToString());
                current.Clear();
                continue;
            }

            if (ch == '"')
            {
                insideQuotes = true;
                continue;
            }

            current.Append(ch);
        }

        values.Add(current.ToString());
        return values;
    }
}
