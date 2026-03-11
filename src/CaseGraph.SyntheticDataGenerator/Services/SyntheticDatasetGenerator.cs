using CaseGraph.SyntheticDataGenerator.Models;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;

namespace CaseGraph.SyntheticDataGenerator.Services;

public sealed class SyntheticDatasetGenerator
{
    private static readonly string[] FirstNames =
    [
        "Avery",
        "Jordan",
        "Riley",
        "Casey",
        "Parker",
        "Morgan",
        "Taylor",
        "Quinn",
        "Devon",
        "Cameron",
        "Reese",
        "Skyler"
    ];

    private static readonly string[] LastNames =
    [
        "Harbor",
        "Marlowe",
        "Vale",
        "Rowan",
        "Sloane",
        "Bennett",
        "Cross",
        "Hollis",
        "Mercer",
        "Wilder",
        "Ellison",
        "Monroe"
    ];

    private static readonly string[] SceneAliases =
    [
        "North Lot",
        "Pier Annex",
        "Maple Storage",
        "Service Alley",
        "Rail Yard Gate"
    ];

    private static readonly string[] FallbackAliases =
    [
        "Diner Lot",
        "River Trail",
        "Bus Depot",
        "West Ramp",
        "Park Shelter"
    ];

    private static readonly string[] ReconKeywords =
    [
        "blue bag",
        "south camera",
        "north light",
        "service road",
        "side entrance"
    ];

    public async Task<IReadOnlyList<GeneratorManifest>> GenerateAsync(
        GeneratorOptions options,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();

        Directory.CreateDirectory(options.OutputFolder);

        var manifests = new List<GeneratorManifest>(options.DatasetCount);
        for (var datasetIndex = 0; datasetIndex < options.DatasetCount; datasetIndex++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var dataset = BuildDataset(options, datasetIndex);
            var datasetFolderPath = Path.Combine(options.OutputFolder, dataset.Manifest.DatasetFolderName);
            progress?.Report($"Generating {dataset.Manifest.DatasetFolderName}...");

            if (Directory.Exists(datasetFolderPath))
            {
                Directory.Delete(datasetFolderPath, recursive: true);
            }

            Directory.CreateDirectory(datasetFolderPath);
            await WriteDatasetAsync(datasetFolderPath, dataset, cancellationToken).ConfigureAwait(false);

            manifests.Add(dataset.Manifest);
            progress?.Report(
                $"Generated {dataset.Manifest.DatasetFolderName} with {dataset.Messages.Count} messages and {dataset.Locations.Count} locations."
            );
            await Task.Yield();
        }

        return manifests;
    }

    private static SyntheticDataset BuildDataset(GeneratorOptions options, int datasetIndex)
    {
        var datasetSeed = CombineSeed(options, datasetIndex);
        var random = new Random(datasetSeed);
        var generatedAtUtc = BuildGeneratedAtUtc(options, datasetIndex, datasetSeed);
        var offenseWindowStartUtc = new DateTimeOffset(
            generatedAtUtc.Year,
            generatedAtUtc.Month,
            generatedAtUtc.Day,
            21,
            10,
            0,
            TimeSpan.Zero
        ).AddMinutes(datasetIndex * 3);
        var offenseWindowEndUtc = offenseWindowStartUtc.AddMinutes(32);
        var sceneAlias = SceneAliases[Math.Abs(datasetSeed) % SceneAliases.Length];
        var fallbackAlias = FallbackAliases[Math.Abs(datasetSeed / 7) % FallbackAliases.Length];
        var reconKeyword = ReconKeywords[Math.Abs(datasetSeed / 13) % ReconKeywords.Length];

        var persons = BuildPersons(options.PersonCount, datasetIndex, random);
        var threads = BuildThreads(persons, sceneAlias, fallbackAlias);
        var messages = BuildMessages(
            persons,
            threads,
            options.ApproximateMessageCount,
            offenseWindowStartUtc,
            offenseWindowEndUtc,
            sceneAlias,
            fallbackAlias,
            reconKeyword,
            random
        );
        var locations = BuildLocations(
            persons,
            options.ApproximateLocationCount,
            offenseWindowStartUtc,
            offenseWindowEndUtc,
            sceneAlias,
            fallbackAlias,
            random
        );

        var folderName = $"dataset-{datasetIndex + 1:000}-{options.Profile}-seed-{options.Seed}";
        var manifest = new GeneratorManifest
        {
            Seed = options.Seed,
            DatasetIndex = datasetIndex + 1,
            DatasetFolderName = folderName,
            Profile = options.Profile,
            PersonCount = persons.Count,
            RequestedMessageCount = options.ApproximateMessageCount,
            GeneratedMessageCount = messages.Count,
            RequestedLocationCount = options.ApproximateLocationCount,
            GeneratedLocationCount = locations.Count,
            GeneratedAtUtc = generatedAtUtc,
            OffenseWindowStartUtc = offenseWindowStartUtc,
            OffenseWindowEndUtc = offenseWindowEndUtc,
            CentralSubjects = persons.Where(person => person.IsCentral).Select(person => person.DisplayName).Take(3).ToArray()
        };

        return new SyntheticDataset(
            manifest,
            persons,
            threads,
            messages,
            locations,
            sceneAlias,
            fallbackAlias,
            reconKeyword
        );
    }

    private static List<PersonRecord> BuildPersons(int personCount, int datasetIndex, Random random)
    {
        var persons = new List<PersonRecord>(personCount);
        for (var index = 0; index < personCount; index++)
        {
            var firstName = FirstNames[(index + datasetIndex + random.Next(FirstNames.Length)) % FirstNames.Length];
            var lastName = LastNames[(index * 2 + datasetIndex + random.Next(LastNames.Length)) % LastNames.Length];
            var displayName = $"Synthetic {firstName} {lastName} {index + 1:00}";
            var shortCode = $"P{index + 1:00}";
            var phoneDigits = 1000000 + (((datasetIndex + 1) * 9713 + (index + 1) * 177) % 9000000);
            var phoneNumber = $"+1-555-{phoneDigits:0000000}";
            var email = $"{firstName.ToLowerInvariant()}.{lastName.ToLowerInvariant()}.{index + 1:00}@synthetic.casegraph.test";
            var isCentral = index < Math.Min(3, personCount);
            var role = isCentral
                ? index == 0 ? "Coordinator" : index == 1 ? "Primary partner" : "Scene scout"
                : "Peripheral contact";

            persons.Add(new PersonRecord(shortCode, displayName, phoneNumber, email, role, isCentral));
        }

        return persons;
    }

    private static List<ThreadRecord> BuildThreads(
        IReadOnlyList<PersonRecord> persons,
        string sceneAlias,
        string fallbackAlias
    )
    {
        var central = persons.Take(Math.Min(3, persons.Count)).ToList();
        var remaining = persons.Skip(Math.Min(3, persons.Count)).ToList();
        var threads = new List<ThreadRecord>
        {
            new(
                "thread-001",
                "Pre-event coordination",
                central.Select(person => person.PersonId).Distinct(StringComparer.Ordinal).ToArray(),
                $"Pre-event planning around {sceneAlias}."
            ),
            new(
                "thread-002",
                "Window coordination",
                persons.Take(Math.Min(4, persons.Count)).Select(person => person.PersonId).ToArray(),
                $"Offense-window coordination near {sceneAlias}."
            ),
            new(
                "thread-003",
                "Aftermath regroup",
                persons.Take(Math.Min(4, persons.Count)).Select(person => person.PersonId).ToArray(),
                $"Aftermath chatter and regroup near {fallbackAlias}."
            )
        };

        if (remaining.Count > 0)
        {
            threads.Add(
                new ThreadRecord(
                    "thread-004",
                    "Peripheral cover traffic",
                    new[] { central[0].PersonId, remaining[0].PersonId },
                    "Cover traffic that keeps peripheral contacts connected to the core subjects."
                )
            );
        }

        return threads;
    }

    private static List<MessageRecord> BuildMessages(
        IReadOnlyList<PersonRecord> persons,
        IReadOnlyList<ThreadRecord> threads,
        int messageCount,
        DateTimeOffset offenseWindowStartUtc,
        DateTimeOffset offenseWindowEndUtc,
        string sceneAlias,
        string fallbackAlias,
        string reconKeyword,
        Random random
    )
    {
        var results = new List<MessageRecord>(messageCount);
        for (var index = 0; index < messageCount; index++)
        {
            var phase = ResolvePhase(index, messageCount);
            var thread = ResolveThread(threads, phase);
            var participants = persons.Where(person => thread.ParticipantIds.Contains(person.PersonId, StringComparer.Ordinal)).ToList();
            var sender = participants[(index + random.Next(participants.Count)) % participants.Count];
            var recipients = participants
                .Where(person => !string.Equals(person.PersonId, sender.PersonId, StringComparison.Ordinal))
                .Select(person => person.DisplayName)
                .ToArray();
            var sentUtc = BuildMessageTimestamp(
                phase,
                offenseWindowStartUtc,
                offenseWindowEndUtc,
                index,
                messageCount,
                random
            );
            var body = BuildMessageBody(phase, sender.DisplayName, sceneAlias, fallbackAlias, reconKeyword, index);
            var direction = sender.IsCentral ? "Outgoing" : "Incoming";

            results.Add(
                new MessageRecord(
                    $"msg-{index + 1:0000}",
                    thread.ThreadId,
                    thread.Name,
                    sender.DisplayName,
                    recipients,
                    sentUtc,
                    direction,
                    body
                )
            );
        }

        return results.OrderBy(message => message.SentUtc).ThenBy(message => message.MessageId, StringComparer.Ordinal).ToList();
    }

    private static List<LocationRecord> BuildLocations(
        IReadOnlyList<PersonRecord> persons,
        int locationCount,
        DateTimeOffset offenseWindowStartUtc,
        DateTimeOffset offenseWindowEndUtc,
        string sceneAlias,
        string fallbackAlias,
        Random random
    )
    {
        var sceneLatitude = 39.7420 + (random.NextDouble() * 0.08);
        var sceneLongitude = -104.9980 - (random.NextDouble() * 0.08);
        var fallbackLatitude = sceneLatitude + 0.018;
        var fallbackLongitude = sceneLongitude - 0.021;
        var locations = new List<LocationRecord>(locationCount);

        for (var index = 0; index < locationCount; index++)
        {
            var phase = ResolvePhase(index, locationCount);
            var person = persons[index % persons.Count];
            var observedUtc = BuildLocationTimestamp(phase, offenseWindowStartUtc, offenseWindowEndUtc, index, locationCount, random);
            var (latitude, longitude, note) = phase switch
            {
                ScenarioPhase.OffenseWindow => (
                    sceneLatitude + NextSignedDouble(random, 0.0008),
                    sceneLongitude + NextSignedDouble(random, 0.0008),
                    $"Synthetic convergence near {sceneAlias}."
                ),
                ScenarioPhase.Aftermath => (
                    fallbackLatitude + NextSignedDouble(random, 0.0022),
                    fallbackLongitude + NextSignedDouble(random, 0.0022),
                    $"Synthetic regroup near {fallbackAlias}."
                ),
                _ => (
                    sceneLatitude + 0.022 + NextSignedDouble(random, 0.0065),
                    sceneLongitude - 0.019 + NextSignedDouble(random, 0.0065),
                    "Synthetic pre-event travel."
                )
            };

            locations.Add(
                new LocationRecord(
                    $"loc-{index + 1:0000}",
                    person.DisplayName,
                    observedUtc,
                    latitude,
                    longitude,
                    6 + (index % 8),
                    index % 2 == 0 ? "locations.csv" : "device_locations.plist",
                    note
                )
            );
        }

        return locations.OrderBy(location => location.ObservedUtc).ThenBy(location => location.LocationId, StringComparer.Ordinal).ToList();
    }

    private static ScenarioPhase ResolvePhase(int index, int totalCount)
    {
        if (index < totalCount * 0.40)
        {
            return ScenarioPhase.PreEvent;
        }

        if (index < totalCount * 0.75)
        {
            return ScenarioPhase.OffenseWindow;
        }

        return ScenarioPhase.Aftermath;
    }

    private static ThreadRecord ResolveThread(IReadOnlyList<ThreadRecord> threads, ScenarioPhase phase)
    {
        return phase switch
        {
            ScenarioPhase.PreEvent => threads[0],
            ScenarioPhase.OffenseWindow => threads[Math.Min(1, threads.Count - 1)],
            ScenarioPhase.Aftermath => threads[Math.Min(2, threads.Count - 1)],
            _ => threads[0]
        };
    }

    private static DateTimeOffset BuildGeneratedAtUtc(GeneratorOptions options, int datasetIndex, int datasetSeed)
    {
        var baseline = new DateTimeOffset(2025, 02, 01, 14, 00, 00, TimeSpan.Zero);
        return baseline
            .AddDays(Math.Abs(datasetSeed % 27))
            .AddHours(datasetIndex * 2)
            .AddMinutes((options.PersonCount * 3) + options.ApproximateMessageCount % 50);
    }

    private static DateTimeOffset BuildMessageTimestamp(
        ScenarioPhase phase,
        DateTimeOffset offenseWindowStartUtc,
        DateTimeOffset offenseWindowEndUtc,
        int index,
        int totalCount,
        Random random
    )
    {
        return phase switch
        {
            ScenarioPhase.PreEvent => offenseWindowStartUtc
                .AddHours(-3.5)
                .AddMinutes((index * 11) % 150)
                .AddSeconds(random.Next(55)),
            ScenarioPhase.OffenseWindow => offenseWindowStartUtc
                .AddMinutes((index * 3) % Math.Max((int)(offenseWindowEndUtc - offenseWindowStartUtc).TotalMinutes, 1))
                .AddSeconds(random.Next(55)),
            _ => offenseWindowEndUtc
                .AddMinutes(18 + ((index * 7) % Math.Max(totalCount / 2, 12)))
                .AddSeconds(random.Next(55))
        };
    }

    private static DateTimeOffset BuildLocationTimestamp(
        ScenarioPhase phase,
        DateTimeOffset offenseWindowStartUtc,
        DateTimeOffset offenseWindowEndUtc,
        int index,
        int totalCount,
        Random random
    )
    {
        return phase switch
        {
            ScenarioPhase.PreEvent => offenseWindowStartUtc
                .AddHours(-2.75)
                .AddMinutes((index * 9) % 120)
                .AddSeconds(random.Next(45)),
            ScenarioPhase.OffenseWindow => offenseWindowStartUtc
                .AddMinutes((index * 4) % Math.Max((int)(offenseWindowEndUtc - offenseWindowStartUtc).TotalMinutes, 1))
                .AddSeconds(random.Next(45)),
            _ => offenseWindowEndUtc
                .AddMinutes(10 + ((index * 8) % Math.Max(totalCount / 2, 10)))
                .AddSeconds(random.Next(45))
        };
    }

    private static string BuildMessageBody(
        ScenarioPhase phase,
        string senderDisplayName,
        string sceneAlias,
        string fallbackAlias,
        string reconKeyword,
        int index
    )
    {
        return phase switch
        {
            ScenarioPhase.PreEvent => $"{senderDisplayName}: check {reconKeyword} near {sceneAlias} before 21:00. Keep this synthetic scenario quiet.",
            ScenarioPhase.OffenseWindow => $"{senderDisplayName}: window is open at {sceneAlias}. Move now and keep eyes on the {reconKeyword}.",
            _ => $"{senderDisplayName}: leave {sceneAlias} and regroup at {fallbackAlias}. This is fictional synthetic follow-up chatter #{index + 1}.",
        };
    }

    private static async Task WriteDatasetAsync(
        string datasetFolderPath,
        SyntheticDataset dataset,
        CancellationToken cancellationToken
    )
    {
        var contactsPath = Path.Combine(datasetFolderPath, "contacts.csv");
        var threadsPath = Path.Combine(datasetFolderPath, "message_threads.csv");
        var messagesPath = Path.Combine(datasetFolderPath, "messages.csv");
        var locationsCsvPath = Path.Combine(datasetFolderPath, "locations.csv");
        var locationsJsonPath = Path.Combine(datasetFolderPath, "locations.json");
        var plistPath = Path.Combine(datasetFolderPath, "device_locations.plist");
        var findingsPath = Path.Combine(datasetFolderPath, "expected_findings.md");
        var manifestPath = Path.Combine(datasetFolderPath, "manifest.json");
        var readmePath = Path.Combine(datasetFolderPath, "README.md");

        await File.WriteAllTextAsync(contactsPath, BuildContactsCsv(dataset.Persons), Encoding.UTF8, cancellationToken).ConfigureAwait(false);
        await File.WriteAllTextAsync(threadsPath, BuildThreadsCsv(dataset.Threads, dataset.Persons), Encoding.UTF8, cancellationToken).ConfigureAwait(false);
        await File.WriteAllTextAsync(messagesPath, BuildMessagesCsv(dataset.Messages), Encoding.UTF8, cancellationToken).ConfigureAwait(false);
        await File.WriteAllTextAsync(locationsCsvPath, BuildLocationsCsv(dataset.Locations), Encoding.UTF8, cancellationToken).ConfigureAwait(false);
        await File.WriteAllTextAsync(locationsJsonPath, BuildLocationsJson(dataset), Encoding.UTF8, cancellationToken).ConfigureAwait(false);
        await File.WriteAllTextAsync(plistPath, BuildLocationsPlist(dataset.Locations), Encoding.UTF8, cancellationToken).ConfigureAwait(false);
        await File.WriteAllTextAsync(findingsPath, BuildExpectedFindings(dataset), Encoding.UTF8, cancellationToken).ConfigureAwait(false);
        await File.WriteAllTextAsync(readmePath, BuildReadme(dataset), Encoding.UTF8, cancellationToken).ConfigureAwait(false);
        await File.WriteAllTextAsync(
            manifestPath,
            JsonSerializer.Serialize(dataset.Manifest, new JsonSerializerOptions { WriteIndented = true }),
            Encoding.UTF8,
            cancellationToken
        ).ConfigureAwait(false);
    }

    private static string BuildContactsCsv(IReadOnlyList<PersonRecord> persons)
    {
        var builder = new StringBuilder();
        builder.AppendLine("ContactId,DisplayName,PhoneNumber,Email,Role,Notes");
        foreach (var person in persons)
        {
            builder.AppendLine(
                string.Join(
                    ',',
                    CsvEscape(person.PersonId),
                    CsvEscape(person.DisplayName),
                    CsvEscape(person.PhoneNumber),
                    CsvEscape(person.Email),
                    CsvEscape(person.Role),
                    CsvEscape("Synthetic fictional contact for CaseGraph QA.")
                )
            );
        }

        return builder.ToString();
    }

    private static string BuildThreadsCsv(IReadOnlyList<ThreadRecord> threads, IReadOnlyList<PersonRecord> persons)
    {
        var personLookup = persons.ToDictionary(person => person.PersonId, StringComparer.Ordinal);
        var builder = new StringBuilder();
        builder.AppendLine("ThreadId,ThreadName,ParticipantNames,ParticipantCount,Notes");
        foreach (var thread in threads)
        {
            var participantNames = thread.ParticipantIds.Select(id => personLookup[id].DisplayName);
            builder.AppendLine(
                string.Join(
                    ',',
                    CsvEscape(thread.ThreadId),
                    CsvEscape(thread.Name),
                    CsvEscape(string.Join("; ", participantNames)),
                    thread.ParticipantIds.Count.ToString(CultureInfo.InvariantCulture),
                    CsvEscape(thread.Notes)
                )
            );
        }

        return builder.ToString();
    }

    private static string BuildMessagesCsv(IReadOnlyList<MessageRecord> messages)
    {
        var builder = new StringBuilder();
        builder.AppendLine("MessageId,ThreadId,ThreadName,Sender,Recipients,SentUtc,Direction,Body,Notes");
        foreach (var message in messages)
        {
            builder.AppendLine(
                string.Join(
                    ',',
                    CsvEscape(message.MessageId),
                    CsvEscape(message.ThreadId),
                    CsvEscape(message.ThreadName),
                    CsvEscape(message.Sender),
                    CsvEscape(string.Join("; ", message.Recipients)),
                    CsvEscape(message.SentUtc.ToString("O", CultureInfo.InvariantCulture)),
                    CsvEscape(message.Direction),
                    CsvEscape(message.Body),
                    CsvEscape("Synthetic fictional message content.")
                )
            );
        }

        return builder.ToString();
    }

    private static string BuildLocationsCsv(IReadOnlyList<LocationRecord> locations)
    {
        var builder = new StringBuilder();
        builder.AppendLine("ObservationId,DisplayName,ObservedUtc,Latitude,Longitude,AccuracyMeters,SourceLabel,Note");
        foreach (var location in locations)
        {
            builder.AppendLine(
                string.Join(
                    ',',
                    CsvEscape(location.LocationId),
                    CsvEscape(location.DisplayName),
                    CsvEscape(location.ObservedUtc.ToString("O", CultureInfo.InvariantCulture)),
                    location.Latitude.ToString("F6", CultureInfo.InvariantCulture),
                    location.Longitude.ToString("F6", CultureInfo.InvariantCulture),
                    location.AccuracyMeters.ToString(CultureInfo.InvariantCulture),
                    CsvEscape(location.SourceLabel),
                    CsvEscape(location.Note)
                )
            );
        }

        return builder.ToString();
    }

    private static string BuildLocationsJson(SyntheticDataset dataset)
    {
        var payload = new
        {
            synthetic = true,
            fictionalNotice = dataset.Manifest.FictionalNotice,
            profile = dataset.Manifest.Profile,
            generatedAtUtc = dataset.Manifest.GeneratedAtUtc,
            observations = dataset.Locations.Select(location => new
            {
                location.LocationId,
                location.DisplayName,
                location.ObservedUtc,
                location.Latitude,
                location.Longitude,
                location.AccuracyMeters,
                location.SourceLabel,
                location.Note
            })
        };

        return JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true });
    }

    private static string BuildLocationsPlist(IReadOnlyList<LocationRecord> locations)
    {
        var builder = new StringBuilder();
        builder.AppendLine("<?xml version=\"1.0\" encoding=\"UTF-8\"?>");
        builder.AppendLine("<!DOCTYPE plist PUBLIC \"-//Apple//DTD PLIST 1.0//EN\" \"http://www.apple.com/DTDs/PropertyList-1.0.dtd\">");
        builder.AppendLine("<plist version=\"1.0\">");
        builder.AppendLine("<array>");
        foreach (var location in locations)
        {
            builder.AppendLine("  <dict>");
            builder.AppendLine($"    <key>ObservationId</key><string>{XmlEscape(location.LocationId)}</string>");
            builder.AppendLine($"    <key>DisplayName</key><string>{XmlEscape(location.DisplayName)}</string>");
            builder.AppendLine($"    <key>ObservedUtc</key><string>{XmlEscape(location.ObservedUtc.ToString("O", CultureInfo.InvariantCulture))}</string>");
            builder.AppendLine($"    <key>Latitude</key><real>{location.Latitude.ToString("F6", CultureInfo.InvariantCulture)}</real>");
            builder.AppendLine($"    <key>Longitude</key><real>{location.Longitude.ToString("F6", CultureInfo.InvariantCulture)}</real>");
            builder.AppendLine($"    <key>AccuracyMeters</key><integer>{location.AccuracyMeters.ToString(CultureInfo.InvariantCulture)}</integer>");
            builder.AppendLine($"    <key>SourceLabel</key><string>{XmlEscape(location.SourceLabel)}</string>");
            builder.AppendLine($"    <key>Note</key><string>{XmlEscape(location.Note)}</string>");
            builder.AppendLine("  </dict>");
        }

        builder.AppendLine("</array>");
        builder.AppendLine("</plist>");
        return builder.ToString();
    }

    private static string BuildExpectedFindings(SyntheticDataset dataset)
    {
        var centralSubjects = string.Join(", ", dataset.Manifest.CentralSubjects);
        var strongLinkSubjects = dataset.Persons.Take(Math.Min(3, dataset.Persons.Count)).Select(person => person.DisplayName).ToArray();

        return $"""
# Expected Findings

This dataset is fully fictional synthetic evidence generated for CaseGraph QA.

## Suggested offense window
- Start: {dataset.Manifest.OffenseWindowStartUtc:O}
- End: {dataset.Manifest.OffenseWindowEndUtc:O}

## Central subjects
- {centralSubjects}

## Likely strong graph links
- {strongLinkSubjects[0]} <-> {strongLinkSubjects[Math.Min(1, strongLinkSubjects.Length - 1)]}
- {strongLinkSubjects[0]} <-> {strongLinkSubjects[Math.Min(2, strongLinkSubjects.Length - 1)]}

## Expected location convergence
- Multiple synthetic location observations should converge near {dataset.SceneAlias} during the offense window.
- After the offense window, movement should shift toward {dataset.FallbackAlias}.

## Suggested QA searches
- Search for `{dataset.ReconKeyword}`
- Search for `{dataset.SceneAlias}`
- Search for `{dataset.FallbackAlias}`
- Filter Timeline around {dataset.Manifest.OffenseWindowStartUtc:yyyy-MM-dd HH:mm} UTC
""";
    }

    private static string BuildReadme(SyntheticDataset dataset)
    {
        return $"""
# {dataset.Manifest.DatasetFolderName}

This folder contains fictional synthetic evidence for CaseGraph testing.

- Synthetic/Fictional: yes
- Profile: {dataset.Manifest.Profile}
- Seed: {dataset.Manifest.Seed}
- GeneratedAtUtc: {dataset.Manifest.GeneratedAtUtc:O}
- Persons: {dataset.Manifest.PersonCount}
- Messages: {dataset.Manifest.GeneratedMessageCount}
- Locations: {dataset.Manifest.GeneratedLocationCount}

## Included files
- contacts.csv
- message_threads.csv
- messages.csv
- locations.csv
- locations.json
- device_locations.plist
- expected_findings.md
- manifest.json

## QA notes
- This data is intentionally synthetic and not derived from any real device or person.
- Use the provided expected findings to validate Search, Timeline, Association Graph, Incident Window, and Locations flows.
""";
    }

    private static string CsvEscape(string value)
    {
        var normalized = value.Replace("\"", "\"\"", StringComparison.Ordinal);
        return $"\"{normalized}\"";
    }

    private static string XmlEscape(string value)
    {
        return value
            .Replace("&", "&amp;", StringComparison.Ordinal)
            .Replace("<", "&lt;", StringComparison.Ordinal)
            .Replace(">", "&gt;", StringComparison.Ordinal)
            .Replace("\"", "&quot;", StringComparison.Ordinal)
            .Replace("'", "&apos;", StringComparison.Ordinal);
    }

    private static int CombineSeed(GeneratorOptions options, int datasetIndex)
    {
        unchecked
        {
            var seed = options.Seed;
            seed = (seed * 397) ^ datasetIndex;
            seed = (seed * 397) ^ options.DatasetCount;
            seed = (seed * 397) ^ options.PersonCount;
            seed = (seed * 397) ^ options.ApproximateMessageCount;
            seed = (seed * 397) ^ options.ApproximateLocationCount;
            foreach (var character in options.Profile)
            {
                seed = (seed * 31) + character;
            }

            return seed;
        }
    }

    private static double NextSignedDouble(Random random, double amplitude)
    {
        return (random.NextDouble() * amplitude * 2d) - amplitude;
    }

    private sealed record SyntheticDataset(
        GeneratorManifest Manifest,
        IReadOnlyList<PersonRecord> Persons,
        IReadOnlyList<ThreadRecord> Threads,
        IReadOnlyList<MessageRecord> Messages,
        IReadOnlyList<LocationRecord> Locations,
        string SceneAlias,
        string FallbackAlias,
        string ReconKeyword
    );

    private sealed record PersonRecord(
        string PersonId,
        string DisplayName,
        string PhoneNumber,
        string Email,
        string Role,
        bool IsCentral
    );

    private sealed record ThreadRecord(
        string ThreadId,
        string Name,
        IReadOnlyList<string> ParticipantIds,
        string Notes
    );

    private sealed record MessageRecord(
        string MessageId,
        string ThreadId,
        string ThreadName,
        string Sender,
        IReadOnlyList<string> Recipients,
        DateTimeOffset SentUtc,
        string Direction,
        string Body
    );

    private sealed record LocationRecord(
        string LocationId,
        string DisplayName,
        DateTimeOffset ObservedUtc,
        double Latitude,
        double Longitude,
        int AccuracyMeters,
        string SourceLabel,
        string Note
    );

    private enum ScenarioPhase
    {
        PreEvent,
        OffenseWindow,
        Aftermath
    }
}
