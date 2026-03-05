using CaseGraph.Core.Diagnostics;
using System.Xml;
using System.Xml.Linq;

namespace CaseGraph.Infrastructure.Locations;

public sealed class LocationPlistParser : ILocationObservationParser
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

        var header = new byte[6];
        var read = await stream.ReadAsync(header.AsMemory(0, header.Length), ct);
        stream.Seek(0, SeekOrigin.Begin);

        if (read == header.Length
            && string.Equals(System.Text.Encoding.ASCII.GetString(header), "bplist", StringComparison.Ordinal))
        {
            AppFileLogger.LogEvent(
                eventName: "LocationsIngestPlistBinaryNotSupported",
                level: "WARN",
                message: "Binary PLIST is not supported by the v1 locations parser.",
                fields: new Dictionary<string, object?>
                {
                    ["file"] = sourceLabel
                }
            );
            return new LocationParserResult(0, 0, 0, Array.Empty<string>());
        }

        XDocument document;
        try
        {
            using var reader = XmlReader.Create(
                stream,
                new XmlReaderSettings
                {
                    Async = true,
                    IgnoreComments = true,
                    IgnoreWhitespace = true,
                    DtdProcessing = DtdProcessing.Ignore
                }
            );
            document = await XDocument.LoadAsync(reader, LoadOptions.None, ct);
        }
        catch (XmlException ex)
        {
            AppFileLogger.LogEvent(
                eventName: "LocationsIngestPlistParseFailed",
                level: "WARN",
                message: "PLIST parser could not read location artifact; file was skipped.",
                ex: ex,
                fields: new Dictionary<string, object?>
                {
                    ["file"] = sourceLabel
                }
            );
            return new LocationParserResult(0, 0, 1, Array.Empty<string>());
        }
        var plistRoot = document.Root;
        if (plistRoot is null)
        {
            return new LocationParserResult(0, 0, 0, Array.Empty<string>());
        }

        var payloadRoot = string.Equals(plistRoot.Name.LocalName, "plist", StringComparison.OrdinalIgnoreCase)
            ? plistRoot.Elements().FirstOrDefault()
            : plistRoot;
        if (payloadRoot is null)
        {
            return new LocationParserResult(0, 0, 0, Array.Empty<string>());
        }

        var totalDictCount = payloadRoot
            .DescendantsAndSelf()
            .Count(element => string.Equals(element.Name.LocalName, "dict", StringComparison.OrdinalIgnoreCase));

        await ProcessNodeAsync(payloadRoot, "root");

        return new LocationParserResult(
            state.Processed,
            state.Accepted,
            state.Skipped,
            unknownFields.OrderBy(item => item, StringComparer.Ordinal).ToArray()
        );

        async ValueTask ProcessNodeAsync(XElement node, string keyPath)
        {
            ct.ThrowIfCancellationRequested();
            var localName = node.Name.LocalName;
            if (string.Equals(localName, "dict", StringComparison.OrdinalIgnoreCase))
            {
                state.Processed++;
                var entries = ReadDictEntries(node);
                var map = new Dictionary<string, string?>(StringComparer.Ordinal);
                foreach (var (key, valueElement) in entries)
                {
                    var normalized = LocationParseHelpers.NormalizeKey(key);
                    if (string.IsNullOrWhiteSpace(normalized))
                    {
                        continue;
                    }

                    map[normalized] = TryReadPlistScalar(valueElement);
                    if (!LocationParseHelpers.KnownKeys.Contains(normalized))
                    {
                        unknownFields.Add(normalized);
                    }
                }

                var sourceLocator = $"plist://{LocationParseHelpers.EscapePathSegment(sourceLabel)}#keyPath={LocationParseHelpers.EscapePathSegment(keyPath)}";
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
                        eventName: "LocationsIngestPlistNodeSkipped",
                        parser: "PLIST",
                        locator: sourceLocator,
                        reason: reason
                    );
                }

                if (onProgress is not null && state.Processed % 25 == 0)
                {
                    var fraction = totalDictCount <= 0
                        ? 1
                        : Math.Clamp(state.Processed / (double)totalDictCount, 0, 1);
                    onProgress(new LocationParserProgress(
                        FractionComplete: fraction,
                        ProcessedCount: state.Processed,
                        AcceptedCount: state.Accepted,
                        SkippedCount: state.Skipped,
                        Phase: "Parsing PLIST locations..."
                    ));
                }

                foreach (var (key, valueElement) in entries)
                {
                    if (valueElement.Name.LocalName is not ("dict" or "array"))
                    {
                        continue;
                    }

                    await ProcessNodeAsync(valueElement, $"{keyPath}.{key}");
                }

                return;
            }

            if (string.Equals(localName, "array", StringComparison.OrdinalIgnoreCase))
            {
                var index = 0;
                foreach (var child in node.Elements())
                {
                    await ProcessNodeAsync(child, $"{keyPath}[{index}]");
                    index++;
                }
            }
        }
    }

    private static IReadOnlyList<(string Key, XElement Value)> ReadDictEntries(XElement dict)
    {
        var entries = new List<(string Key, XElement Value)>();
        var children = dict.Elements().ToList();
        for (var index = 0; index < children.Count; index++)
        {
            var child = children[index];
            if (!string.Equals(child.Name.LocalName, "key", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (index + 1 >= children.Count)
            {
                break;
            }

            var key = child.Value.Trim();
            if (string.IsNullOrWhiteSpace(key))
            {
                continue;
            }

            var valueElement = children[index + 1];
            if (string.Equals(valueElement.Name.LocalName, "key", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            entries.Add((key, valueElement));
            index++;
        }

        return entries;
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
            SourceType: "PLIST",
            SourceLabel: sourceLabel,
            SubjectType: subjectType,
            SubjectId: subjectId,
            SourceLocator: sourceLocator
        );
        return true;
    }

    private static string? TryReadPlistScalar(XElement element)
    {
        return element.Name.LocalName switch
        {
            "string" => element.Value,
            "real" => element.Value,
            "integer" => element.Value,
            "date" => element.Value,
            "true" => "true",
            "false" => "false",
            "data" => element.Value,
            _ => null
        };
    }

    private sealed class ParseState
    {
        public int Processed { get; set; }

        public int Accepted { get; set; }

        public int Skipped { get; set; }
    }
}
