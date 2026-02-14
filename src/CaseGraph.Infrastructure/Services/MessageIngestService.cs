using CaseGraph.Core.Abstractions;
using CaseGraph.Core.Diagnostics;
using CaseGraph.Infrastructure.Persistence;
using CaseGraph.Infrastructure.Persistence.Entities;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;
using Microsoft.EntityFrameworkCore;
using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Xml;
using System.Xml.Linq;

namespace CaseGraph.Infrastructure.Services;

public sealed class MessageIngestService : IMessageIngestService
{
    private const string IngestModuleVersion = "CaseGraph.MessagesIngest/v1";
    private const string NoMessageSheetsStatus = "No message sheets found; verify export settings.";
    private const string UfdrUnsupportedStatus = "UFDR message parsing not supported in this build. Generate a Cellebrite XLSX message export and import that.";
    private const string UfdrEncryptedStatus = "UFDR content appears encrypted or unavailable in this build. Generate a Cellebrite XLSX message export and import that.";

    private static readonly string[] PreferredSheets =
    {
        "Messages",
        "SMS",
        "iMessage",
        "Chats",
        "Chat",
        "WhatsApp",
        "Signal",
        "Instagram"
    };

    private static readonly string[] UfdrKeywords =
    {
        "message",
        "sms",
        "imessage",
        "whatsapp",
        "chat",
        "conversation"
    };

    private static readonly string[] UfdrEncryptedKeywords =
    {
        "encrypt",
        "cipher",
        "protected"
    };

    private readonly IDbContextFactory<WorkspaceDbContext> _dbContextFactory;
    private readonly IWorkspaceDatabaseInitializer _databaseInitializer;
    private readonly IWorkspacePathProvider _workspacePathProvider;
    private readonly IClock _clock;

    public MessageIngestService(
        IDbContextFactory<WorkspaceDbContext> dbContextFactory,
        IWorkspaceDatabaseInitializer databaseInitializer,
        IWorkspacePathProvider workspacePathProvider,
        IClock clock
    )
    {
        _dbContextFactory = dbContextFactory;
        _databaseInitializer = databaseInitializer;
        _workspacePathProvider = workspacePathProvider;
        _clock = clock;
    }

    public async Task<int> IngestMessagesFromEvidenceAsync(
        Guid caseId,
        EvidenceItemRecord evidence,
        IProgress<double>? progress,
        CancellationToken ct
    )
    {
        IProgress<MessageIngestProgress>? adaptedProgress = null;
        if (progress is not null)
        {
            adaptedProgress = new Progress<MessageIngestProgress>(
                update => progress.Report(Math.Clamp(update.FractionComplete, 0, 1))
            );
        }

        var result = await IngestMessagesDetailedFromEvidenceAsync(
            caseId,
            evidence,
            adaptedProgress,
            logContext: null,
            ct
        );
        return result.MessagesExtracted;
    }

    public async Task<MessageIngestResult> IngestMessagesDetailedFromEvidenceAsync(
        Guid caseId,
        EvidenceItemRecord evidence,
        IProgress<MessageIngestProgress>? progress,
        string? logContext,
        CancellationToken ct
    )
    {
        await _databaseInitializer.EnsureInitializedAsync(ct);
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();

        var storedAbsolutePath = Path.Combine(
            _workspacePathProvider.CasesRoot,
            caseId.ToString("D"),
            evidence.StoredRelativePath.Replace('/', Path.DirectorySeparatorChar)
        );
        if (!File.Exists(storedAbsolutePath))
        {
            throw new FileNotFoundException("Stored evidence file is missing.", storedAbsolutePath);
        }

        AppFileLogger.Log(
            BuildLogMessage(
                logContext,
                $"[MessagesIngest] Begin case={caseId:D} evidence={evidence.EvidenceItemId:D} file={evidence.OriginalFileName} ext={evidence.FileExtension}"
            )
        );

        var parseBatch = evidence.FileExtension.ToLowerInvariant() switch
        {
            ".xlsx" => await ParseXlsxAsync(storedAbsolutePath, evidence.OriginalFileName, progress, logContext, ct),
            ".ufdr" => await ParseUfdrAsync(storedAbsolutePath, progress, logContext, ct),
            _ => ParseBatch.Empty("No message parser is available for this evidence type.")
        };

        var threadsCreated = await PersistAsync(caseId, evidence.EvidenceItemId, parseBatch.Messages, ct);
        progress?.Report(new MessageIngestProgress(
            FractionComplete: 1,
            Phase: "Persisting parsed messages...",
            Processed: parseBatch.Messages.Count,
            Total: parseBatch.Messages.Count
        ));
        var summaryOverride = parseBatch.Messages.Count == 0
            ? parseBatch.EmptyStatusMessage ?? "Extracted 0 message(s)."
            : null;
        AppFileLogger.Log(
            BuildLogMessage(
                logContext,
                $"[MessagesIngest] Complete case={caseId:D} evidence={evidence.EvidenceItemId:D} parsed={parseBatch.Messages.Count} threads={threadsCreated} elapsedMs={stopwatch.ElapsedMilliseconds} summary=\"{summaryOverride ?? $"Extracted {parseBatch.Messages.Count} message(s)."}\""
            )
        );
        return new MessageIngestResult(
            MessagesExtracted: parseBatch.Messages.Count,
            ThreadsCreated: threadsCreated,
            SummaryOverride: summaryOverride
        );
    }

    private async Task<ParseBatch> ParseXlsxAsync(
        string filePath,
        string originalFileName,
        IProgress<MessageIngestProgress>? progress,
        string? logContext,
        CancellationToken ct
    )
    {
        var result = new List<ParsedMessage>();
        await using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var doc = SpreadsheetDocument.Open(stream, false);
        var workbookPart = doc.WorkbookPart ?? throw new InvalidOperationException("XLSX workbook not found.");
        var sheets = workbookPart.Workbook.Sheets?.OfType<Sheet>().ToList() ?? new List<Sheet>();
        if (sheets.Count == 0)
        {
            return ParseBatch.Empty(NoMessageSheetsStatus);
        }

        var selected = new List<Sheet>();
        foreach (var preferred in PreferredSheets)
        {
            var found = sheets.FirstOrDefault(s => string.Equals(s.Name?.Value, preferred, StringComparison.OrdinalIgnoreCase));
            if (found is not null && !selected.Contains(found))
            {
                selected.Add(found);
            }
        }

        if (selected.Count == 0)
        {
            return ParseBatch.Empty(NoMessageSheetsStatus);
        }

        AppFileLogger.Log(
            BuildLogMessage(
                logContext,
                $"[MessagesIngest] XLSX sheets={string.Join(",", selected.Select(s => s.Name?.Value ?? "Sheet"))}"
            )
        );

        var shared = workbookPart.SharedStringTablePart?.SharedStringTable;
        var totalRows = selected.Sum(s => Math.Max(0, GetRows(workbookPart, s).Count - 1));
        var processed = 0;
        ReportProgress(
            progress,
            0.03,
            "Parsing \"Messages\"...",
            0,
            totalRows
        );

        foreach (var sheet in selected)
        {
            ct.ThrowIfCancellationRequested();
            var rows = GetRows(workbookPart, sheet);
            if (rows.Count <= 1)
            {
                continue;
            }

            var header = rows[0].Elements<Cell>()
                .Select(c => (Index: ColumnIndex(c.CellReference?.Value), Name: HeaderKey(CellText(c, shared))))
                .Where(x => x.Index >= 0 && x.Name is not null)
                .ToDictionary(x => x.Index, x => x.Name!, EqualityComparer<int>.Default);

            if (header.Count == 0)
            {
                continue;
            }

            var rowOrdinal = 1u;
            foreach (var row in rows.Skip(1))
            {
                ct.ThrowIfCancellationRequested();
                processed++;
                rowOrdinal++;

                var map = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
                foreach (var cell in row.Elements<Cell>())
                {
                    var idx = ColumnIndex(cell.CellReference?.Value);
                    if (idx < 0 || !header.TryGetValue(idx, out var key))
                    {
                        continue;
                    }

                    map[key] = CellText(cell, shared);
                }

                var body = map.GetValueOrDefault("body");
                var sender = map.GetValueOrDefault("sender");
                var recipients = map.GetValueOrDefault("recipients");
                if (string.IsNullOrWhiteSpace(body) && string.IsNullOrWhiteSpace(sender) && string.IsNullOrWhiteSpace(recipients))
                {
                    if (processed % 5 == 0 || processed == totalRows)
                    {
                        ReportProgress(
                            progress,
                            totalRows == 0 ? 0.7 : 0.03 + (processed / (double)totalRows) * 0.67,
                            "Parsing \"Messages\"...",
                            processed,
                            totalRows
                        );
                    }

                    continue;
                }

                var rowNumber = row.RowIndex?.Value ?? rowOrdinal;
                var sheetName = sheet.Name?.Value ?? "Sheet";
                var platform = NormalizePlatform(map.GetValueOrDefault("platform") ?? sheetName);
                result.Add(new ParsedMessage(
                    Platform: platform,
                    ThreadKey: NullIfWhiteSpace(map.GetValueOrDefault("threadkey"))
                        ?? BuildDeterministicThreadKey(platform, sender, recipients),
                    ThreadTitle: NullIfWhiteSpace(map.GetValueOrDefault("threadtitle")),
                    TimestampUtc: ParseTimestamp(map.GetValueOrDefault("timestamp")),
                    Direction: NormalizeDirection(map.GetValueOrDefault("direction")),
                    Sender: NullIfWhiteSpace(sender),
                    Recipients: NullIfWhiteSpace(recipients),
                    Body: NullIfWhiteSpace(body),
                    IsDeleted: ParseBoolean(map.GetValueOrDefault("deleted")),
                    SourceLocator: $"xlsx:{originalFileName}#{sheetName}:R{rowNumber}"
                ));
                if (processed % 5 == 0 || processed == totalRows)
                {
                    ReportProgress(
                        progress,
                        totalRows == 0 ? 0.7 : 0.03 + (processed / (double)totalRows) * 0.67,
                        "Parsing \"Messages\"...",
                        processed,
                        totalRows
                    );
                }
            }
        }

        AppFileLogger.Log(
            BuildLogMessage(
                logContext,
                $"[MessagesIngest] XLSX rowsProcessed={processed} candidateRows={totalRows} parsedMessages={result.Count}"
            )
        );
        return new ParseBatch(result, EmptyStatusMessage: null);
    }

    private static List<Row> GetRows(WorkbookPart workbookPart, Sheet sheet)
    {
        var wsPart = (WorksheetPart)workbookPart.GetPartById(sheet.Id!);
        return wsPart.Worksheet.GetFirstChild<SheetData>()?.Elements<Row>().ToList() ?? new List<Row>();
    }

    private async Task<ParseBatch> ParseUfdrAsync(
        string filePath,
        IProgress<MessageIngestProgress>? progress,
        string? logContext,
        CancellationToken ct
    )
    {
        var result = new List<ParsedMessage>();
        await using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: true);
        var candidateEntries = archive.Entries
            .Where(e => UfdrKeywords.Any(keyword => e.FullName.Contains(keyword, StringComparison.OrdinalIgnoreCase)))
            .ToList();
        var entries = candidateEntries
            .Where(e => e.FullName.EndsWith(".json", StringComparison.OrdinalIgnoreCase) || e.FullName.EndsWith(".xml", StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (entries.Count == 0)
        {
            return ParseBatch.Empty(UfdrUnsupportedStatus);
        }

        AppFileLogger.Log(
            BuildLogMessage(
                logContext,
                $"[MessagesIngest] UFDR candidateEntries={candidateEntries.Count} parseEntries={entries.Count}"
            )
        );

        ReportProgress(
            progress,
            0.03,
            "Parsing UFDR message artifacts...",
            0,
            entries.Count
        );

        for (var i = 0; i < entries.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var entry = entries[i];
            if (entry.Length == 0)
            {
                ReportProgress(
                    progress,
                    0.03 + ((i + 1) / (double)entries.Count) * 0.67,
                    "Parsing UFDR message artifacts...",
                    i + 1,
                    entries.Count
                );
                continue;
            }

            if (entry.FullName.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
            {
                await using var jsonStream = entry.Open();
                using var doc = await JsonDocument.ParseAsync(jsonStream, cancellationToken: ct);
                var artifact = 0;
                ParseJson(doc.RootElement, $"ufdr:{entry.FullName}", result, ref artifact);
            }
            else
            {
                using var xmlStream = entry.Open();
                using var reader = XmlReader.Create(xmlStream, new XmlReaderSettings { IgnoreWhitespace = true, IgnoreComments = true });
                var artifact = 0;
                while (reader.Read())
                {
                    ct.ThrowIfCancellationRequested();
                    if (reader.NodeType != XmlNodeType.Element || !(reader.Name.Contains("message", StringComparison.OrdinalIgnoreCase) || reader.Name.Contains("chat", StringComparison.OrdinalIgnoreCase) || reader.Name.Contains("sms", StringComparison.OrdinalIgnoreCase)))
                    {
                        continue;
                    }

                    using var subtree = reader.ReadSubtree();
                    var el = XElement.Load(subtree);
                    artifact++;
                    var body = XmlValue(el, "body", "text", "content", "message");
                    var sender = XmlValue(el, "sender", "from", "author");
                    var recipients = XmlValue(el, "recipients", "recipient", "to", "targets");
                    if (string.IsNullOrWhiteSpace(body) && string.IsNullOrWhiteSpace(sender) && string.IsNullOrWhiteSpace(recipients))
                    {
                        continue;
                    }

                    result.Add(new ParsedMessage(
                        Platform: NormalizePlatform(XmlValue(el, "platform", "app", "source")),
                        ThreadKey: XmlValue(el, "threadid", "conversationid", "chatid", "thread", "conversation") ?? $"ufdr-xml-{artifact}",
                        ThreadTitle: XmlValue(el, "threadtitle", "title", "chattitle"),
                        TimestampUtc: ParseTimestamp(XmlValue(el, "timestamp", "time", "date", "createdat", "sentat")),
                        Direction: NormalizeDirection(XmlValue(el, "direction", "type")),
                        Sender: NullIfWhiteSpace(sender),
                        Recipients: NullIfWhiteSpace(recipients),
                        Body: NullIfWhiteSpace(body),
                        IsDeleted: ParseBoolean(XmlValue(el, "deleted", "isdeleted", "removed")),
                        SourceLocator: $"ufdr:{entry.FullName}#xpath:/{el.Name.LocalName}[{artifact}]"
                    ));
                }
            }

            ReportProgress(
                progress,
                0.03 + ((i + 1) / (double)entries.Count) * 0.67,
                "Parsing UFDR message artifacts...",
                i + 1,
                entries.Count
            );
        }

        if (result.Count == 0)
        {
            var encrypted = candidateEntries.Any(
                e => UfdrEncryptedKeywords.Any(keyword => e.FullName.Contains(keyword, StringComparison.OrdinalIgnoreCase))
            );
            return ParseBatch.Empty(encrypted ? UfdrEncryptedStatus : UfdrUnsupportedStatus);
        }

        return new ParseBatch(result, EmptyStatusMessage: null);
    }

    private async Task<int> PersistAsync(
        Guid caseId,
        Guid evidenceItemId,
        IReadOnlyList<ParsedMessage> parsed,
        CancellationToken ct
    )
    {
        await using var db = await _dbContextFactory.CreateDbContextAsync(ct);
        await using var tx = await db.Database.BeginTransactionAsync(ct);

        var threadIds = await db.MessageThreads
            .Where(t => t.EvidenceItemId == evidenceItemId)
            .Select(t => t.ThreadId)
            .ToListAsync(ct);

        if (threadIds.Count > 0)
        {
            var participants = await db.MessageParticipants.Where(p => threadIds.Contains(p.ThreadId)).ToListAsync(ct);
            var events = await db.MessageEvents.Where(e => threadIds.Contains(e.ThreadId)).ToListAsync(ct);
            var threads = await db.MessageThreads.Where(t => threadIds.Contains(t.ThreadId)).ToListAsync(ct);
            db.MessageParticipants.RemoveRange(participants);
            db.MessageEvents.RemoveRange(events);
            db.MessageThreads.RemoveRange(threads);
            await db.SaveChangesAsync(ct);
        }

        if (parsed.Count == 0)
        {
            await tx.CommitAsync(ct);
            return 0;
        }

        var threadMap = new Dictionary<string, MessageThreadRecord>(StringComparer.OrdinalIgnoreCase);
        var eventRecords = new List<MessageEventRecord>(parsed.Count);

        foreach (var item in parsed)
        {
            ct.ThrowIfCancellationRequested();
            var platform = NormalizePlatform(item.Platform);
            var threadKey = string.IsNullOrWhiteSpace(item.ThreadKey) ? $"{platform}:{item.SourceLocator}" : item.ThreadKey;
            var compositeKey = $"{platform}|{threadKey}";
            if (!threadMap.TryGetValue(compositeKey, out var thread))
            {
                thread = new MessageThreadRecord
                {
                    ThreadId = Guid.NewGuid(),
                    CaseId = caseId,
                    EvidenceItemId = evidenceItemId,
                    Platform = platform,
                    ThreadKey = threadKey,
                    Title = item.ThreadTitle,
                    CreatedAtUtc = item.TimestampUtc ?? _clock.UtcNow.ToUniversalTime(),
                    SourceLocator = item.SourceLocator,
                    IngestModuleVersion = IngestModuleVersion
                };
                threadMap[compositeKey] = thread;
            }

            eventRecords.Add(new MessageEventRecord
            {
                MessageEventId = Guid.NewGuid(),
                ThreadId = thread.ThreadId,
                CaseId = caseId,
                EvidenceItemId = evidenceItemId,
                Platform = platform,
                TimestampUtc = item.TimestampUtc,
                Direction = NormalizeDirection(item.Direction),
                Sender = item.Sender,
                Recipients = item.Recipients,
                Body = item.Body,
                IsDeleted = item.IsDeleted,
                SourceLocator = item.SourceLocator,
                IngestModuleVersion = IngestModuleVersion
            });
        }

        db.MessageThreads.AddRange(threadMap.Values);
        db.MessageEvents.AddRange(eventRecords);
        db.MessageParticipants.AddRange(BuildParticipants(threadMap.Values, eventRecords));
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return threadMap.Count;
    }

    private static List<MessageParticipantRecord> BuildParticipants(
        IEnumerable<MessageThreadRecord> threads,
        IEnumerable<MessageEventRecord> events
    )
    {
        var byThread = events.GroupBy(e => e.ThreadId).ToDictionary(g => g.Key, g => g.ToList());
        var result = new List<MessageParticipantRecord>();
        foreach (var thread in threads)
        {
            if (!byThread.TryGetValue(thread.ThreadId, out var threadEvents))
            {
                continue;
            }

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var e in threadEvents)
            {
                foreach (var value in SplitIdentifiers(e.Sender).Concat(SplitIdentifiers(e.Recipients)))
                {
                    if (!seen.Add(value))
                    {
                        continue;
                    }

                    result.Add(new MessageParticipantRecord
                    {
                        ParticipantId = Guid.NewGuid(),
                        ThreadId = thread.ThreadId,
                        Value = value,
                        Kind = IdentifierKind(value),
                        SourceLocator = e.SourceLocator,
                        IngestModuleVersion = IngestModuleVersion
                    });
                }
            }
        }

        return result;
    }

    private static IEnumerable<string> SplitIdentifiers(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return Array.Empty<string>();
        }

        return raw.Split([',', ';', '|'], StringSplitOptions.RemoveEmptyEntries)
            .Select(v => v.Trim())
            .Where(v => v.Length > 0);
    }

    private static string IdentifierKind(string value)
    {
        if (value.Contains('@'))
        {
            return "email";
        }

        return value.Count(char.IsDigit) >= 7 ? "phone" : "handle";
    }

    private static void ParseJson(JsonElement element, string sourcePrefix, List<ParsedMessage> output, ref int artifact)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                {
                    ParseJson(item, sourcePrefix, output, ref artifact);
                }
                break;
            case JsonValueKind.Object:
                artifact++;
                var body = JsonValue(element, "body", "text", "message", "content");
                var sender = JsonValue(element, "sender", "from", "author");
                var recipients = JsonValue(element, "recipients", "recipient", "to", "targets");
                if (!string.IsNullOrWhiteSpace(body) || !string.IsNullOrWhiteSpace(sender) || !string.IsNullOrWhiteSpace(recipients))
                {
                    output.Add(new ParsedMessage(
                        Platform: NormalizePlatform(JsonValue(element, "platform", "app", "source")),
                        ThreadKey: JsonValue(element, "threadid", "conversationid", "chatid", "thread", "conversation") ?? $"ufdr-{artifact}",
                        ThreadTitle: JsonValue(element, "threadtitle", "title", "chattitle"),
                        TimestampUtc: ParseTimestamp(JsonValue(element, "timestamp", "time", "date", "createdat", "sentat")),
                        Direction: NormalizeDirection(JsonValue(element, "direction", "type")),
                        Sender: NullIfWhiteSpace(sender),
                        Recipients: NullIfWhiteSpace(recipients),
                        Body: NullIfWhiteSpace(body),
                        IsDeleted: ParseBoolean(JsonValue(element, "deleted", "isdeleted", "removed")),
                        SourceLocator: $"{sourcePrefix}#artifact:{artifact}"
                    ));
                }

                foreach (var property in element.EnumerateObject())
                {
                    ParseJson(property.Value, sourcePrefix, output, ref artifact);
                }
                break;
        }
    }

    private static string? JsonValue(JsonElement element, params string[] names)
    {
        foreach (var name in names)
        {
            foreach (var property in element.EnumerateObject())
            {
                if (!string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                return property.Value.ValueKind == JsonValueKind.String
                    ? property.Value.GetString()
                    : property.Value.ToString();
            }
        }

        return null;
    }

    private static string? XmlValue(XElement element, params string[] names)
    {
        foreach (var name in names)
        {
            var attr = element.Attributes().FirstOrDefault(a => string.Equals(a.Name.LocalName, name, StringComparison.OrdinalIgnoreCase));
            if (attr is not null && !string.IsNullOrWhiteSpace(attr.Value))
            {
                return attr.Value.Trim();
            }

            var child = element.Descendants().FirstOrDefault(d => string.Equals(d.Name.LocalName, name, StringComparison.OrdinalIgnoreCase));
            if (child is not null && !string.IsNullOrWhiteSpace(child.Value))
            {
                return child.Value.Trim();
            }
        }

        return null;
    }

    private static string CellText(Cell cell, SharedStringTable? shared)
    {
        if (cell.DataType?.Value == CellValues.SharedString)
        {
            var raw = cell.CellValue?.InnerText ?? cell.InnerText;
            if (int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var idx) && shared is not null)
            {
                return shared.ElementAtOrDefault(idx)?.InnerText ?? string.Empty;
            }
        }

        if (cell.DataType?.Value == CellValues.InlineString)
        {
            return cell.InlineString?.Text?.Text ?? string.Empty;
        }

        if (cell.DataType?.Value == CellValues.Boolean)
        {
            return (cell.CellValue?.InnerText ?? "0") == "1" ? "true" : "false";
        }

        return cell.CellValue?.InnerText ?? cell.InnerText ?? string.Empty;
    }

    private static int ColumnIndex(string? cellReference)
    {
        if (string.IsNullOrWhiteSpace(cellReference))
        {
            return -1;
        }

        var letters = new string(cellReference.TakeWhile(char.IsLetter).ToArray());
        if (letters.Length == 0)
        {
            return -1;
        }

        var value = 0;
        foreach (var letter in letters)
        {
            value = (value * 26) + (letter - 'A' + 1);
        }

        return value - 1;
    }

    private static string? HeaderKey(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var normalized = new string(value.Trim().ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray());
        return normalized switch
        {
            "timestamp" or "time" or "date" or "datetime" or "sentat" or "createdat" => "timestamp",
            "direction" or "type" or "incomingoutgoing" => "direction",
            "sender" or "from" or "author" or "source" => "sender",
            "recipient" or "recipients" or "to" or "targets" or "destination" => "recipients",
            "body" or "message" or "text" or "content" or "messagebody" => "body",
            "deleted" or "isdeleted" or "removed" => "deleted",
            "conversationid" or "threadid" or "chatid" or "thread" or "conversation" => "threadkey",
            "platform" or "app" or "sourceapp" => "platform",
            "threadtitle" or "conversationtitle" or "chattitle" or "title" => "threadtitle",
            _ => null
        };
    }

    private static DateTimeOffset? ParseTimestamp(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        if (DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var dto))
        {
            return dto.ToUniversalTime();
        }

        if (DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var dt))
        {
            return new DateTimeOffset(dt.ToUniversalTime());
        }

        if (double.TryParse(value, NumberStyles.Any, CultureInfo.InvariantCulture, out var serial))
        {
            try
            {
                return new DateTimeOffset(DateTime.SpecifyKind(DateTime.FromOADate(serial), DateTimeKind.Utc));
            }
            catch (ArgumentException)
            {
            }
        }

        return null;
    }

    private static string NormalizeDirection(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "Unknown";
        }

        var normalized = value.Trim().ToLowerInvariant();
        if (normalized.Contains("in"))
        {
            return "Incoming";
        }

        if (normalized.Contains("out"))
        {
            return "Outgoing";
        }

        return "Unknown";
    }

    private static string NormalizePlatform(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "OTHER";
        }

        var normalized = value.Trim().ToLowerInvariant();
        if (normalized.Contains("sms"))
        {
            return "SMS";
        }

        if (normalized.Contains("imessage"))
        {
            return "iMessage";
        }

        if (normalized.Contains("whatsapp"))
        {
            return "WhatsApp";
        }

        if (normalized.Contains("signal"))
        {
            return "Signal";
        }

        if (normalized.Contains("instagram"))
        {
            return "Instagram";
        }

        return "OTHER";
    }

    private static string BuildDeterministicThreadKey(
        string platform,
        string? sender,
        string? recipients
    )
    {
        var canonical = string.Join(
            "|",
            NormalizePlatform(platform),
            CanonicalIdentifierSet(sender),
            CanonicalIdentifierSet(recipients)
        );
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(canonical));
        return $"v1:{Convert.ToHexString(hash.AsSpan(0, 12)).ToLowerInvariant()}";
    }

    private static string CanonicalIdentifierSet(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return string.Empty;
        }

        return string.Join(
            ",",
            SplitIdentifiers(raw)
                .Select(value => value.ToLowerInvariant())
                .Distinct(StringComparer.Ordinal)
                .OrderBy(value => value, StringComparer.Ordinal)
        );
    }

    private static bool ParseBoolean(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        return value.Trim().ToLowerInvariant() is "1" or "true" or "yes" or "y" or "deleted";
    }

    private static void ReportProgress(
        IProgress<MessageIngestProgress>? progress,
        double fraction,
        string phase,
        int? processed,
        int? total
    )
    {
        progress?.Report(
            new MessageIngestProgress(
                FractionComplete: Math.Clamp(fraction, 0, 1),
                Phase: phase,
                Processed: processed,
                Total: total
            )
        );
    }

    private static string BuildLogMessage(string? context, string message)
    {
        return string.IsNullOrWhiteSpace(context)
            ? message
            : $"{context} {message}";
    }

    private static string? NullIfWhiteSpace(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

    private sealed record ParseBatch(IReadOnlyList<ParsedMessage> Messages, string? EmptyStatusMessage)
    {
        public static ParseBatch Empty(string statusMessage) => new(Array.Empty<ParsedMessage>(), statusMessage);
    }

    private sealed record ParsedMessage(
        string Platform,
        string ThreadKey,
        string? ThreadTitle,
        DateTimeOffset? TimestampUtc,
        string Direction,
        string? Sender,
        string? Recipients,
        string? Body,
        bool IsDeleted,
        string SourceLocator
    );
}
