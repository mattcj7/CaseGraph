using CaseGraph.Core.Abstractions;
using CaseGraph.Core.Diagnostics;
using CaseGraph.Core.Models;
using CaseGraph.Infrastructure.Diagnostics;
using CaseGraph.Infrastructure.Import;
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
    private const string IngestModuleVersion = "CaseGraph.MessagesIngest/v2";
    private const string NoMessageSheetsStatus = "No message sheets found; verify export settings.";
    private const string NoRecognizedMessageColumnsStatus = "No recognizable message columns found; verify export settings.";
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

    private static readonly string[] MessageSheetKeywords =
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
    private readonly IWorkspaceWriteGate _workspaceWriteGate;
    private readonly IEvidenceVaultService _evidenceVaultService;
    private readonly IClock _clock;
    private readonly IPerformanceInstrumentation _performanceInstrumentation;

    public MessageIngestService(
        IDbContextFactory<WorkspaceDbContext> dbContextFactory,
        IWorkspaceDatabaseInitializer databaseInitializer,
        IWorkspacePathProvider workspacePathProvider,
        IWorkspaceWriteGate workspaceWriteGate,
        IEvidenceVaultService evidenceVaultService,
        IClock clock,
        IPerformanceInstrumentation? performanceInstrumentation = null
    )
    {
        _dbContextFactory = dbContextFactory;
        _databaseInitializer = databaseInitializer;
        _workspacePathProvider = workspacePathProvider;
        _workspaceWriteGate = workspaceWriteGate;
        _evidenceVaultService = evidenceVaultService;
        _clock = clock;
        _performanceInstrumentation = performanceInstrumentation
            ?? new PerformanceInstrumentation(new PerformanceBudgetOptions(), TimeProvider.System);
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
        return await _performanceInstrumentation.TrackAsync(
            new PerformanceOperationContext(
                PerformanceOperationKinds.ImportMaintenance,
                "IngestEvidence",
                FeatureName: "MessagesIngest",
                CaseId: caseId,
                EvidenceItemId: evidence.EvidenceItemId
            ),
            async innerCt =>
            {
                await _databaseInitializer.EnsureInitializedAsync(innerCt);
                var stopwatch = System.Diagnostics.Stopwatch.StartNew();

                var storedAbsolutePath = Path.Combine(
                    _workspacePathProvider.CasesRoot,
                    caseId.ToString("D"),
                    evidence.StoredRelativePath.Replace('/', Path.DirectorySeparatorChar)
                );
                if (!File.Exists(storedAbsolutePath))
                {
                    throw new FileNotFoundException(
                        "Stored evidence file is missing.",
                        storedAbsolutePath
                    );
                }

                AppFileLogger.Log(
                    BuildLogMessage(
                        logContext,
                        $"[MessagesIngest] Begin case={caseId:D} evidence={evidence.EvidenceItemId:D} file={evidence.OriginalFileName} ext={evidence.FileExtension}"
                    )
                );

                var parseBatch = evidence.FileExtension.ToLowerInvariant() switch
                {
                    ".csv" => await ParseCsvAsync(
                        storedAbsolutePath,
                        evidence.EvidenceItemId,
                        evidence.OriginalFileName,
                        progress,
                        logContext,
                        innerCt
                    ),
                    ".xlsx" => await ParseXlsxAsync(
                        storedAbsolutePath,
                        evidence.EvidenceItemId,
                        evidence.OriginalFileName,
                        progress,
                        logContext,
                        innerCt
                    ),
                    ".ufdr" => await ParseUfdrAsync(
                        storedAbsolutePath,
                        evidence.EvidenceItemId,
                        evidence.OriginalFileName,
                        progress,
                        logContext,
                        innerCt
                    ),
                    ".zip" => await ParseArchiveAsync(
                        caseId,
                        evidence,
                        progress,
                        logContext,
                        innerCt
                    ),
                    _ => ParseBatch.Empty("No message parser is available for this evidence type.")
                };

                if (parseBatch.Messages.Count == 0)
                {
                    var emptySummary = parseBatch.EmptyStatusMessage ?? "Extracted 0 message(s).";
                    AppFileLogger.Log(
                        BuildLogMessage(
                            logContext,
                            $"[MessagesIngest] Complete case={caseId:D} evidence={evidence.EvidenceItemId:D} parsed=0 threads=0 elapsedMs={stopwatch.ElapsedMilliseconds} summary=\"{emptySummary}\""
                        )
                    );
                    return new MessageIngestResult(
                        MessagesExtracted: 0,
                        ThreadsCreated: 0,
                        SummaryOverride: emptySummary
                    );
                }

                progress?.Report(
                    new MessageIngestProgress(
                        FractionComplete: 1,
                        Phase: "Persisting parsed messages...",
                        Processed: parseBatch.Messages.Count,
                        Total: parseBatch.Messages.Count
                    )
                );

                var threadsCreated = await PersistAsync(
                    caseId,
                    evidence.EvidenceItemId,
                    parseBatch.Messages,
                    innerCt
                );
                var summaryOverride = parseBatch.SummaryOverride;
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
            },
            ct
        );
    }

    private async Task<ParseBatch> ParseArchiveAsync(
        Guid caseId,
        EvidenceItemRecord evidence,
        IProgress<MessageIngestProgress>? progress,
        string? logContext,
        CancellationToken ct
    )
    {
        var extraction = await _evidenceVaultService.EnsureArchiveExtractedAsync(
            caseId,
            ToEvidenceItem(evidence),
            ct
        );
        if (extraction is null || extraction.ExtractedRelativePaths.Count == 0)
        {
            return ParseBatch.Empty("Archive extraction completed, but no extracted files were available for message routing.");
        }

        var supportedEntries = extraction.ExtractedRelativePaths
            .Where(path => ResolveArchiveMessageParser(path) is not null)
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToArray();
        var unsupportedEntryCount = extraction.ExtractedRelativePaths.Count - supportedEntries.Length;
        if (supportedEntries.Length == 0)
        {
            return ParseBatch.Empty("No supported message artifacts were found in the archive.");
        }

        AppFileLogger.LogEvent(
            eventName: "MessagesIngestArchiveRoutingStarted",
            level: "INFO",
            message: BuildLogMessage(logContext, "Message ingest archive routing started."),
            fields: new Dictionary<string, object?>
            {
                ["sourceLabel"] = evidence.OriginalFileName,
                ["supportedEntryCount"] = supportedEntries.Length,
                ["unsupportedEntryCount"] = unsupportedEntryCount
            }
        );

        var messages = new List<CanonicalMessageRecord>();
        var filesWithMessages = 0;
        var failedEntries = 0;

        for (var index = 0; index < supportedEntries.Length; index++)
        {
            ct.ThrowIfCancellationRequested();

            var relativePath = supportedEntries[index];
            var parserFamily = ResolveArchiveMessageParser(relativePath)!;
            var absolutePath = Path.Combine(
                extraction.ExtractedRootPath,
                relativePath.Replace('/', Path.DirectorySeparatorChar)
            );

            LogArchiveRouteSelected(logContext, evidence.OriginalFileName, relativePath, parserFamily);

            ParseBatch innerBatch;
            try
            {
                var baseFraction = 0.03 + (index / (double)supportedEntries.Length) * 0.67;
                var fractionWidth = 0.67 / supportedEntries.Length;
                var routedProgress = new Progress<MessageIngestProgress>(update =>
                {
                    progress?.Report(update with
                    {
                        FractionComplete = baseFraction + (Math.Clamp(update.FractionComplete, 0, 1) * fractionWidth),
                        Phase = $"Routing archive entry {index + 1}/{supportedEntries.Length}: {relativePath}",
                        Processed = index + 1,
                        Total = supportedEntries.Length
                    });
                });

                innerBatch = parserFamily switch
                {
                    "CSV" => await ParseCsvAsync(
                        absolutePath,
                        evidence.EvidenceItemId,
                        relativePath,
                        routedProgress,
                        logContext,
                        ct
                    ),
                    "XLSX" => await ParseXlsxAsync(
                        absolutePath,
                        evidence.EvidenceItemId,
                        relativePath,
                        routedProgress,
                        logContext,
                        ct
                    ),
                    "UFDR" => await ParseUfdrAsync(
                        absolutePath,
                        evidence.EvidenceItemId,
                        relativePath,
                        routedProgress,
                        logContext,
                        ct
                    ),
                    "HTML" => await ParseHtmlAsync(
                        absolutePath,
                        evidence.EvidenceItemId,
                        relativePath,
                        routedProgress,
                        logContext,
                        ct
                    ),
                    _ => ParseBatch.Empty($"No message parser is available for archive entry \"{relativePath}\".")
                };
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                failedEntries++;
                AppFileLogger.LogEvent(
                    eventName: "MessagesIngestArchiveEntryFailed",
                    level: "WARN",
                    message: BuildLogMessage(logContext, "Archive entry failed during message parsing and was skipped."),
                    ex: ex,
                    fields: new Dictionary<string, object?>
                    {
                        ["sourceLabel"] = evidence.OriginalFileName,
                        ["entryPath"] = relativePath,
                        ["parserFamily"] = parserFamily
                    }
                );
                continue;
            }

            if (innerBatch.Messages.Count > 0)
            {
                filesWithMessages++;
                messages.AddRange(innerBatch.Messages.Select(record =>
                    record with
                    {
                        SourceLocator = CanonicalizeArchiveMessageLocator(relativePath, record.SourceLocator)
                    }));
            }
        }

        var summary = BuildArchiveMessageSummary(
            messages.Count,
            filesWithMessages,
            supportedEntries.Length,
            unsupportedEntryCount,
            failedEntries,
            extraction.Warnings.Count
        );

        AppFileLogger.LogEvent(
            eventName: "MessagesIngestArchiveRoutingCompleted",
            level: "INFO",
            message: BuildLogMessage(logContext, "Message ingest archive routing completed."),
            fields: new Dictionary<string, object?>
            {
                ["sourceLabel"] = evidence.OriginalFileName,
                ["messagesExtracted"] = messages.Count,
                ["filesWithMessages"] = filesWithMessages,
                ["supportedEntryCount"] = supportedEntries.Length,
                ["unsupportedEntryCount"] = unsupportedEntryCount,
                ["failedEntryCount"] = failedEntries,
                ["archiveWarningCount"] = extraction.Warnings.Count
            }
        );

        return messages.Count == 0
            ? ParseBatch.Empty(summary)
            : new ParseBatch(messages, EmptyStatusMessage: null, SummaryOverride: summary);
    }

    private async Task<ParseBatch> ParseCsvAsync(
        string filePath,
        Guid evidenceItemId,
        string originalFileName,
        IProgress<MessageIngestProgress>? progress,
        string? logContext,
        CancellationToken ct
    )
    {
        var result = new List<CanonicalMessageRecord>();
        var processedRows = 0;
        var skippedRows = 0;
        var warningsCount = 0;
        var rowsVisited = 0;

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
            return ParseBatch.Empty(NoRecognizedMessageColumnsStatus);
        }

        var headerValues = ParseCsvLine(headerLine);
        var fieldMap = MessageCanonicalFieldMap.Create(headerValues);
        if (!fieldMap.LooksLikeMessageExport)
        {
            LogParserSelected(logContext, "CSV", originalFileName, null, "header-alias-miss");
            LogSchemaMatched(logContext, "CSV", originalFileName, null, fieldMap);
            LogParserResult(logContext, "CSV", originalFileName, null, fieldMap, 0, 0, 0, 0);
            return ParseBatch.Empty(NoRecognizedMessageColumnsStatus);
        }

        LogParserSelected(logContext, "CSV", originalFileName, null, "header-alias-match");
        LogSchemaMatched(logContext, "CSV", originalFileName, null, fieldMap);

        ReportProgress(progress, 0.03, "Parsing message export...", 0, null);

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
            rowsVisited++;
            var rowValues = ParseCsvLine(line);
            if (!fieldMap.LooksPopulated(rowValues))
            {
                ReportCsvProgress(progress, stream, rowsVisited, result.Count + skippedRows);
                continue;
            }

            processedRows++;
            var sourceLocator = $"csv:{originalFileName}#row={rowNumber}";
            var parseResult = CanonicalMessageRecord.TryCreate(
                fieldMap.ReadRow(rowValues),
                new CanonicalMessageContext(
                    evidenceItemId,
                    sourceLocator,
                    "CSV",
                    IngestModuleVersion,
                    PlatformHint: null
                )
            );
            if (!parseResult.Success)
            {
                skippedRows++;
                ReportCsvProgress(progress, stream, rowsVisited, result.Count + skippedRows);
                continue;
            }

            var record = parseResult.Record!;
            warningsCount += record.ParseWarnings.Count;
            result.Add(record);
            ReportCsvProgress(progress, stream, rowsVisited, result.Count + skippedRows);
        }

        LogParserResult(
            logContext,
            "CSV",
            originalFileName,
            containerLabel: null,
            fieldMap,
            processedRows,
            result.Count,
            skippedRows,
            warningsCount
        );

        return result.Count == 0
            ? new ParseBatch(Array.Empty<CanonicalMessageRecord>(), EmptyStatusMessage: null)
            : new ParseBatch(result, EmptyStatusMessage: null);
    }

    private async Task<ParseBatch> ParseXlsxAsync(
        string filePath,
        Guid evidenceItemId,
        string originalFileName,
        IProgress<MessageIngestProgress>? progress,
        string? logContext,
        CancellationToken ct
    )
    {
        var result = new List<CanonicalMessageRecord>();
        await using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var doc = SpreadsheetDocument.Open(stream, false);
        var workbookPart = doc.WorkbookPart ?? throw new InvalidOperationException("XLSX workbook not found.");
        var sheets = workbookPart.Workbook.Sheets?.OfType<Sheet>().ToList() ?? new List<Sheet>();
        if (sheets.Count == 0)
        {
            return ParseBatch.Empty(NoMessageSheetsStatus);
        }

        var candidates = new List<WorksheetCandidate>();
        foreach (var sheet in sheets)
        {
            ct.ThrowIfCancellationRequested();
            var rows = GetRows(workbookPart, sheet);
            if (rows.Count == 0)
            {
                continue;
            }

            var headerValues = ReadWorksheetValues(rows[0], workbookPart.SharedStringTablePart?.SharedStringTable);
            var fieldMap = MessageCanonicalFieldMap.Create(headerValues);
            var sheetName = sheet.Name?.Value;
            var isCandidate = fieldMap.LooksLikeMessageExport
                || (SheetNameLooksMessageLike(sheetName) && fieldMap.Matches.Count >= 2);
            if (!isCandidate)
            {
                continue;
            }

            candidates.Add(new WorksheetCandidate(sheet, rows, fieldMap));
        }

        if (candidates.Count == 0)
        {
            return ParseBatch.Empty(NoMessageSheetsStatus);
        }

        candidates = candidates
            .OrderBy(candidate => PreferredSheetRank(candidate.Sheet.Name?.Value))
            .ThenBy(candidate => candidate.Sheet.Name?.Value, StringComparer.OrdinalIgnoreCase)
            .ToList();

        LogParserSelected(
            logContext,
            "XLSX",
            originalFileName,
            null,
            $"sheet-candidates={candidates.Count}"
        );

        foreach (var candidate in candidates)
        {
            LogSchemaMatched(
                logContext,
                "XLSX",
                originalFileName,
                candidate.Sheet.Name?.Value,
                candidate.FieldMap
            );
        }

        var totalRows = candidates.Sum(candidate => Math.Max(0, candidate.Rows.Count - 1));
        var rowsVisited = 0;
        var processedRows = 0;
        var skippedRows = 0;
        var warningsCount = 0;

        ReportProgress(progress, 0.03, "Parsing message export...", 0, totalRows);
        foreach (var candidate in candidates)
        {
            ct.ThrowIfCancellationRequested();
            var shared = workbookPart.SharedStringTablePart?.SharedStringTable;
            var sheetName = candidate.Sheet.Name?.Value ?? "Sheet";

            for (var rowIndex = 1; rowIndex < candidate.Rows.Count; rowIndex++)
            {
                ct.ThrowIfCancellationRequested();
                rowsVisited++;
                var row = candidate.Rows[rowIndex];
                var rowValues = ReadWorksheetValues(row, shared);
                if (!candidate.FieldMap.LooksPopulated(rowValues))
                {
                    ReportXlsxProgress(progress, rowsVisited, totalRows);
                    continue;
                }

                processedRows++;
                var rowNumber = row.RowIndex?.Value ?? (uint)(rowIndex + 1);
                var sourceLocator = $"xlsx:{originalFileName}#{sheetName}:R{rowNumber}";
                var parseResult = CanonicalMessageRecord.TryCreate(
                    candidate.FieldMap.ReadRow(rowValues),
                    new CanonicalMessageContext(
                        evidenceItemId,
                        sourceLocator,
                        "XLSX",
                        IngestModuleVersion,
                        PlatformHint: sheetName
                    )
                );
                if (!parseResult.Success)
                {
                    skippedRows++;
                    ReportXlsxProgress(progress, rowsVisited, totalRows);
                    continue;
                }

                var record = parseResult.Record!;
                warningsCount += record.ParseWarnings.Count;
                result.Add(record);
                ReportXlsxProgress(progress, rowsVisited, totalRows);
            }
        }

        LogParserResult(
            logContext,
            "XLSX",
            originalFileName,
            null,
            BuildAggregateFieldMap(candidates.Select(candidate => candidate.FieldMap)),
            processedRows,
            result.Count,
            skippedRows,
            warningsCount
        );

        return result.Count == 0
            ? new ParseBatch(Array.Empty<CanonicalMessageRecord>(), EmptyStatusMessage: null)
            : new ParseBatch(result, EmptyStatusMessage: null);
    }

    private static List<Row> GetRows(WorkbookPart workbookPart, Sheet sheet)
    {
        var worksheetPart = (WorksheetPart)workbookPart.GetPartById(sheet.Id!);
        return worksheetPart.Worksheet.GetFirstChild<SheetData>()?.Elements<Row>().ToList() ?? new List<Row>();
    }

    private async Task<ParseBatch> ParseUfdrAsync(
        string filePath,
        Guid evidenceItemId,
        string originalFileName,
        IProgress<MessageIngestProgress>? progress,
        string? logContext,
        CancellationToken ct
    )
    {
        var result = new List<CanonicalMessageRecord>();
        var warningsCount = 0;
        await using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: true);
        var candidateEntries = archive.Entries
            .Where(entry => MessageSheetKeywords.Any(keyword =>
                entry.FullName.Contains(keyword, StringComparison.OrdinalIgnoreCase)))
            .ToList();
        var entries = candidateEntries
            .Where(entry =>
                entry.FullName.EndsWith(".json", StringComparison.OrdinalIgnoreCase)
                || entry.FullName.EndsWith(".xml", StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (entries.Count == 0)
        {
            return ParseBatch.Empty(UfdrUnsupportedStatus);
        }

        LogParserSelected(
            logContext,
            "UFDR",
            originalFileName,
            null,
            $"candidateEntries={candidateEntries.Count};parseEntries={entries.Count}"
        );

        ReportProgress(
            progress,
            0.03,
            "Parsing UFDR message artifacts...",
            0,
            entries.Count
        );

        var candidateArtifacts = 0;
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
                ParseJson(
                    doc.RootElement,
                    $"ufdr:{entry.FullName}",
                    evidenceItemId,
                    result,
                    ref artifact,
                    ref candidateArtifacts,
                    ref warningsCount
                );
            }
            else
            {
                using var xmlStream = entry.Open();
                using var reader = XmlReader.Create(xmlStream, new XmlReaderSettings { IgnoreWhitespace = true, IgnoreComments = true });
                var artifact = 0;
                while (reader.Read())
                {
                    ct.ThrowIfCancellationRequested();
                    if (reader.NodeType != XmlNodeType.Element
                        || !(reader.Name.Contains("message", StringComparison.OrdinalIgnoreCase)
                            || reader.Name.Contains("chat", StringComparison.OrdinalIgnoreCase)
                            || reader.Name.Contains("sms", StringComparison.OrdinalIgnoreCase)))
                    {
                        continue;
                    }

                    using var subtree = reader.ReadSubtree();
                    var element = XElement.Load(subtree);
                    artifact++;
                    candidateArtifacts++;
                    var parseResult = CanonicalMessageRecord.TryCreate(
                        new Dictionary<CanonicalMessageField, string?>
                        {
                            [CanonicalMessageField.Platform] = XmlValue(element, "platform", "app", "source"),
                            [CanonicalMessageField.ThreadExternalId] = XmlValue(element, "threadid", "conversationid", "chatid", "thread", "conversation"),
                            [CanonicalMessageField.ThreadTitle] = XmlValue(element, "threadtitle", "title", "chattitle"),
                            [CanonicalMessageField.SentUtc] = XmlValue(element, "timestamp", "time", "date", "createdat", "sentat"),
                            [CanonicalMessageField.Direction] = XmlValue(element, "direction", "type"),
                            [CanonicalMessageField.SenderDisplay] = XmlValue(element, "sender", "from", "author"),
                            [CanonicalMessageField.RecipientDisplays] = XmlValue(element, "recipients", "recipient", "to", "targets"),
                            [CanonicalMessageField.Body] = XmlValue(element, "body", "text", "content", "message"),
                            [CanonicalMessageField.DeletedFlag] = XmlValue(element, "deleted", "isdeleted", "removed")
                        },
                        new CanonicalMessageContext(
                            evidenceItemId,
                            $"ufdr:{entry.FullName}#xpath:/{element.Name.LocalName}[{artifact}]",
                            "UFDR",
                            IngestModuleVersion,
                            PlatformHint: null
                        )
                    );
                    if (!parseResult.Success)
                    {
                        continue;
                    }

                    var record = parseResult.Record!;
                    warningsCount += record.ParseWarnings.Count;
                    result.Add(record);
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

        LogParserResult(
            logContext,
            "UFDR",
            originalFileName,
            null,
            fieldMap: null,
            processedRows: candidateArtifacts,
            parsedRows: result.Count,
            skippedRows: Math.Max(candidateArtifacts - result.Count, 0),
            warningsCount: warningsCount
        );

        if (result.Count == 0)
        {
            var encrypted = candidateEntries.Any(entry =>
                UfdrEncryptedKeywords.Any(keyword =>
                    entry.FullName.Contains(keyword, StringComparison.OrdinalIgnoreCase)));
            return ParseBatch.Empty(encrypted ? UfdrEncryptedStatus : UfdrUnsupportedStatus);
        }

        return new ParseBatch(result, EmptyStatusMessage: null);
    }

    private async Task<ParseBatch> ParseHtmlAsync(
        string filePath,
        Guid evidenceItemId,
        string originalFileName,
        IProgress<MessageIngestProgress>? progress,
        string? logContext,
        CancellationToken ct
    )
    {
        await using var stream = new FileStream(
            filePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 1024 * 64,
            FileOptions.Asynchronous | FileOptions.SequentialScan
        );
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
        var document = await XDocument.LoadAsync(reader, LoadOptions.None, ct);
        var messageNodes = document
            .Descendants()
            .Where(node => LooksLikeHtmlMessageNode(node))
            .ToList();

        if (messageNodes.Count == 0)
        {
            LogParserSelected(logContext, "HTML", originalFileName, null, "dom-no-message-nodes");
            LogParserResult(logContext, "HTML", originalFileName, null, fieldMap: null, 0, 0, 0, 0);
            return ParseBatch.Empty("No supported HTML message nodes were found.");
        }

        LogParserSelected(logContext, "HTML", originalFileName, null, $"dom-message-nodes={messageNodes.Count}");

        var result = new List<CanonicalMessageRecord>();
        var warningsCount = 0;

        for (var index = 0; index < messageNodes.Count; index++)
        {
            ct.ThrowIfCancellationRequested();
            var node = messageNodes[index];
            var stableId = ResolveHtmlStableId(node, index + 1);
            var parseResult = CanonicalMessageRecord.TryCreate(
                new Dictionary<CanonicalMessageField, string?>
                {
                    [CanonicalMessageField.MessageExternalId] = PickFirstNonEmpty(
                        ReadAttribute(node, "data-message-id"),
                        ReadAttribute(node, "id")
                    ),
                    [CanonicalMessageField.ThreadExternalId] = PickFirstNonEmpty(
                        ReadAttribute(node, "data-thread-id"),
                        ReadDescendantValue(node, "thread-id"),
                        ReadDescendantValue(node, "conversation-id")
                    ),
                    [CanonicalMessageField.ThreadTitle] = PickFirstNonEmpty(
                        ReadAttribute(node, "data-thread-title"),
                        ReadDescendantValue(node, "thread-title"),
                        ReadDescendantValue(node, "conversation-title")
                    ),
                    [CanonicalMessageField.Platform] = PickFirstNonEmpty(
                        ReadAttribute(node, "data-platform"),
                        ReadDescendantValue(node, "platform")
                    ),
                    [CanonicalMessageField.SentUtc] = PickFirstNonEmpty(
                        ReadAttribute(node, "data-sent-utc"),
                        ReadAttribute(node, "datetime"),
                        ReadDescendantTimeValue(node),
                        ReadDescendantValue(node, "timestamp")
                    ),
                    [CanonicalMessageField.Direction] = PickFirstNonEmpty(
                        ReadAttribute(node, "data-direction"),
                        ReadDescendantValue(node, "direction")
                    ),
                    [CanonicalMessageField.SenderDisplay] = PickFirstNonEmpty(
                        ReadAttribute(node, "data-sender"),
                        ReadDescendantValue(node, "sender"),
                        ReadDescendantValue(node, "author")
                    ),
                    [CanonicalMessageField.RecipientDisplays] = PickFirstNonEmpty(
                        ReadAttribute(node, "data-recipients"),
                        ReadDescendantValue(node, "recipients"),
                        ReadDescendantValue(node, "to")
                    ),
                    [CanonicalMessageField.Body] = PickFirstNonEmpty(
                        ReadDescendantValue(node, "body"),
                        ReadDescendantValue(node, "text"),
                        NormalizeElementText(node)
                    )
                },
                new CanonicalMessageContext(
                    evidenceItemId,
                    $"html:{originalFileName}#{stableId}",
                    "HTML",
                    IngestModuleVersion,
                    PlatformHint: null
                )
            );
            if (!parseResult.Success)
            {
                continue;
            }

            var record = parseResult.Record!;
            warningsCount += record.ParseWarnings.Count;
            result.Add(record);

            progress?.Report(new MessageIngestProgress(
                FractionComplete: 0.03 + ((index + 1) / (double)messageNodes.Count) * 0.67,
                Phase: "Parsing HTML message export...",
                Processed: index + 1,
                Total: messageNodes.Count
            ));
        }

        LogParserResult(
            logContext,
            "HTML",
            originalFileName,
            containerLabel: null,
            fieldMap: null,
            processedRows: messageNodes.Count,
            parsedRows: result.Count,
            skippedRows: Math.Max(messageNodes.Count - result.Count, 0),
            warningsCount: warningsCount
        );

        return result.Count == 0
            ? ParseBatch.Empty("HTML content did not yield any canonical messages.")
            : new ParseBatch(result, EmptyStatusMessage: null);
    }

    private async Task<int> PersistAsync(
        Guid caseId,
        Guid evidenceItemId,
        IReadOnlyList<CanonicalMessageRecord> parsed,
        CancellationToken ct
    )
    {
        return await _workspaceWriteGate.ExecuteWriteWithResultAsync(
            operationName: "MessagesIngest.Persist",
            writeCt => PersistCoreAsync(caseId, evidenceItemId, parsed, writeCt),
            ct,
            correlationId: AppFileLogger.GetScopeValue("correlationId"),
            fields: new Dictionary<string, object?>
            {
                ["caseId"] = caseId.ToString("D"),
                ["evidenceItemId"] = evidenceItemId.ToString("D")
            }
        );
    }

    private async Task<int> PersistCoreAsync(
        Guid caseId,
        Guid evidenceItemId,
        IReadOnlyList<CanonicalMessageRecord> parsed,
        CancellationToken ct
    )
    {
        await using var db = await _dbContextFactory.CreateDbContextAsync(ct);
        await using var tx = await db.Database.BeginTransactionAsync(ct);

        var threadIds = await db.MessageThreads
            .Where(thread => thread.EvidenceItemId == evidenceItemId)
            .Select(thread => thread.ThreadId)
            .ToListAsync(ct);

        if (threadIds.Count > 0)
        {
            var participants = await db.MessageParticipants.Where(participant => threadIds.Contains(participant.ThreadId)).ToListAsync(ct);
            var events = await db.MessageEvents.Where(message => threadIds.Contains(message.ThreadId)).ToListAsync(ct);
            var threads = await db.MessageThreads.Where(thread => threadIds.Contains(thread.ThreadId)).ToListAsync(ct);
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

            var platform = CanonicalMessageRecord.NormalizePlatform(item.Platform);
            var threadKey = ResolveThreadKey(item);
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
                    CreatedAtUtc = item.SentUtc ?? _clock.UtcNow.ToUniversalTime(),
                    SourceLocator = item.SourceLocator,
                    IngestModuleVersion = item.ParserVersion
                };
                threadMap[compositeKey] = thread;
            }
            else if (string.IsNullOrWhiteSpace(thread.Title) && !string.IsNullOrWhiteSpace(item.ThreadTitle))
            {
                thread.Title = item.ThreadTitle;
            }

            eventRecords.Add(new MessageEventRecord
            {
                MessageEventId = Guid.NewGuid(),
                ThreadId = thread.ThreadId,
                CaseId = caseId,
                EvidenceItemId = evidenceItemId,
                Platform = platform,
                TimestampUtc = item.SentUtc,
                Direction = CanonicalMessageRecord.NormalizeDirection(item.Direction),
                Sender = item.SenderValue,
                Recipients = item.RecipientValue,
                Body = item.Body,
                IsDeleted = item.DeletedFlag,
                SourceLocator = item.SourceLocator,
                IngestModuleVersion = item.ParserVersion
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
        var byThread = events.GroupBy(message => message.ThreadId).ToDictionary(group => group.Key, group => group.ToList());
        var result = new List<MessageParticipantRecord>();
        foreach (var thread in threads)
        {
            if (!byThread.TryGetValue(thread.ThreadId, out var threadEvents))
            {
                continue;
            }

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var message in threadEvents)
            {
                foreach (var value in SplitIdentifiers(message.Sender).Concat(SplitIdentifiers(message.Recipients)))
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
                        SourceLocator = message.SourceLocator,
                        IngestModuleVersion = message.IngestModuleVersion
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
            .Select(value => value.Trim())
            .Where(value => value.Length > 0);
    }

    private static string IdentifierKind(string value)
    {
        if (value.Contains('@'))
        {
            return "email";
        }

        return value.Count(char.IsDigit) >= 7 ? "phone" : "handle";
    }

    private static void ParseJson(
        JsonElement element,
        string sourcePrefix,
        Guid evidenceItemId,
        List<CanonicalMessageRecord> output,
        ref int artifact,
        ref int candidateArtifacts,
        ref int warningsCount
    )
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                {
                    ParseJson(item, sourcePrefix, evidenceItemId, output, ref artifact, ref candidateArtifacts, ref warningsCount);
                }
                break;

            case JsonValueKind.Object:
                artifact++;
                var values = new Dictionary<CanonicalMessageField, string?>
                {
                    [CanonicalMessageField.Platform] = JsonValue(element, "platform", "app", "source"),
                    [CanonicalMessageField.ThreadExternalId] = JsonValue(element, "threadid", "conversationid", "chatid", "thread", "conversation"),
                    [CanonicalMessageField.ThreadTitle] = JsonValue(element, "threadtitle", "title", "chattitle"),
                    [CanonicalMessageField.SentUtc] = JsonValue(element, "timestamp", "time", "date", "createdat", "sentat"),
                    [CanonicalMessageField.Direction] = JsonValue(element, "direction", "type"),
                    [CanonicalMessageField.SenderDisplay] = JsonValue(element, "sender", "from", "author"),
                    [CanonicalMessageField.RecipientDisplays] = JsonValue(element, "recipients", "recipient", "to", "targets"),
                    [CanonicalMessageField.Body] = JsonValue(element, "body", "text", "message", "content"),
                    [CanonicalMessageField.DeletedFlag] = JsonValue(element, "deleted", "isdeleted", "removed")
                };

                if (values.Values.Any(value => !string.IsNullOrWhiteSpace(value)))
                {
                    candidateArtifacts++;
                    var parseResult = CanonicalMessageRecord.TryCreate(
                        values,
                        new CanonicalMessageContext(
                            evidenceItemId,
                            $"{sourcePrefix}#artifact:{artifact}",
                            "UFDR",
                            IngestModuleVersion,
                            PlatformHint: null
                        )
                    );
                    if (parseResult.Success)
                    {
                        var record = parseResult.Record!;
                        warningsCount += record.ParseWarnings.Count;
                        output.Add(record);
                    }
                }

                foreach (var property in element.EnumerateObject())
                {
                    ParseJson(property.Value, sourcePrefix, evidenceItemId, output, ref artifact, ref candidateArtifacts, ref warningsCount);
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
            var attribute = element.Attributes().FirstOrDefault(item =>
                string.Equals(item.Name.LocalName, name, StringComparison.OrdinalIgnoreCase));
            if (attribute is not null && !string.IsNullOrWhiteSpace(attribute.Value))
            {
                return attribute.Value.Trim();
            }

            var child = element.Descendants().FirstOrDefault(item =>
                string.Equals(item.Name.LocalName, name, StringComparison.OrdinalIgnoreCase));
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
            if (int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var index) && shared is not null)
            {
                return shared.ElementAtOrDefault(index)?.InnerText ?? string.Empty;
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

    private static List<string?> ReadWorksheetValues(Row row, SharedStringTable? shared)
    {
        var values = new List<string?>();
        foreach (var cell in row.Elements<Cell>())
        {
            var index = ColumnIndex(cell.CellReference?.Value);
            if (index < 0)
            {
                continue;
            }

            while (values.Count < index)
            {
                values.Add(null);
            }

            if (values.Count == index)
            {
                values.Add(CellText(cell, shared));
            }
            else
            {
                values[index] = CellText(cell, shared);
            }
        }

        return values;
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

    private static List<string?> ParseCsvLine(string line)
    {
        var values = new List<string?>();
        if (line.Length == 0)
        {
            values.Add(string.Empty);
            return values;
        }

        var current = new StringBuilder(line.Length);
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

    private static bool SheetNameLooksMessageLike(string? sheetName)
    {
        if (string.IsNullOrWhiteSpace(sheetName))
        {
            return false;
        }

        return MessageSheetKeywords.Any(keyword =>
            sheetName.Contains(keyword, StringComparison.OrdinalIgnoreCase));
    }

    private static int PreferredSheetRank(string? sheetName)
    {
        if (string.IsNullOrWhiteSpace(sheetName))
        {
            return int.MaxValue;
        }

        for (var index = 0; index < PreferredSheets.Length; index++)
        {
            if (string.Equals(PreferredSheets[index], sheetName, StringComparison.OrdinalIgnoreCase))
            {
                return index;
            }
        }

        return PreferredSheets.Length + 1;
    }

    private static MessageCanonicalFieldMap BuildAggregateFieldMap(IEnumerable<MessageCanonicalFieldMap> maps)
    {
        var matches = maps
            .SelectMany(map => map.Matches)
            .GroupBy(match => $"{match.Field}|{match.Header}", StringComparer.Ordinal)
            .Select(group => group.First())
            .OrderBy(match => match.Field.ToString(), StringComparer.Ordinal)
            .ThenBy(match => match.Header, StringComparer.Ordinal)
            .ToArray();
        var unmappedHeaders = maps
            .SelectMany(map => map.UnmappedHeaders)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(value => value, StringComparer.Ordinal)
            .ToArray();

        return new MessageCanonicalFieldMap(
            columns: new Dictionary<int, CanonicalMessageField>(),
            matches: matches,
            unmappedHeaders: unmappedHeaders,
            looksLikeMessageExport: true
        );
    }

    private static string? ResolveArchiveMessageParser(string relativePath)
    {
        return Path.GetExtension(relativePath).ToLowerInvariant() switch
        {
            ".csv" => "CSV",
            ".xlsx" or ".xls" => "XLSX",
            ".ufdr" => "UFDR",
            ".html" or ".htm" or ".xhtml" => "HTML",
            _ => null
        };
    }

    private static EvidenceItem ToEvidenceItem(EvidenceItemRecord evidence)
    {
        return new EvidenceItem
        {
            EvidenceItemId = evidence.EvidenceItemId,
            CaseId = evidence.CaseId,
            DisplayName = evidence.DisplayName,
            OriginalPath = evidence.OriginalPath,
            OriginalFileName = evidence.OriginalFileName,
            AddedAtUtc = evidence.AddedAtUtc,
            SizeBytes = evidence.SizeBytes,
            Sha256Hex = evidence.Sha256Hex,
            FileExtension = evidence.FileExtension,
            SourceType = evidence.SourceType,
            ManifestRelativePath = evidence.ManifestRelativePath,
            StoredRelativePath = evidence.StoredRelativePath
        };
    }

    private static string CanonicalizeArchiveMessageLocator(string relativePath, string rawLocator)
    {
        var extension = Path.GetExtension(relativePath).ToLowerInvariant();
        if (extension == ".csv")
        {
            var rowToken = rawLocator.Split("#row=", StringSplitOptions.None).LastOrDefault();
            return int.TryParse(rowToken, out var rowNumber)
                ? $"csv:{relativePath}#row:{rowNumber}"
                : $"csv:{relativePath}";
        }

        return rawLocator;
    }

    private static string BuildArchiveMessageSummary(
        int messagesExtracted,
        int filesWithMessages,
        int supportedEntryCount,
        int unsupportedEntryCount,
        int failedEntryCount,
        int archiveWarningCount
    )
    {
        var parts = new List<string>
        {
            $"Extracted {messagesExtracted} message(s) from {filesWithMessages} archive file(s)."
        };

        if (supportedEntryCount > filesWithMessages)
        {
            parts.Add($"Inspected {supportedEntryCount} supported archive file(s).");
        }

        if (unsupportedEntryCount > 0)
        {
            parts.Add($"Skipped {unsupportedEntryCount} unsupported archive file(s).");
        }

        if (failedEntryCount > 0)
        {
            parts.Add($"Failed {failedEntryCount} archive file(s) without aborting the ingest.");
        }

        if (archiveWarningCount > 0)
        {
            parts.Add($"Archive extraction reported {archiveWarningCount} warning(s).");
        }

        return string.Join(" ", parts);
    }

    private static void LogArchiveRouteSelected(
        string? logContext,
        string sourceLabel,
        string entryPath,
        string parserFamily
    )
    {
        AppFileLogger.LogEvent(
            eventName: "MessagesIngestArchiveEntryDetected",
            level: "INFO",
            message: BuildLogMessage(logContext, "Message ingest archive entry matched a parser."),
            fields: new Dictionary<string, object?>
            {
                ["sourceLabel"] = sourceLabel,
                ["entryPath"] = entryPath,
                ["parserFamily"] = parserFamily
            }
        );
    }

    private static bool LooksLikeHtmlMessageNode(XElement node)
    {
        var localName = node.Name.LocalName;
        if (string.Equals(localName, "message", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (!string.IsNullOrWhiteSpace(ReadAttribute(node, "data-message-id")))
        {
            return true;
        }

        var className = ReadAttribute(node, "class");
        return !string.IsNullOrWhiteSpace(className)
            && className.Contains("message", StringComparison.OrdinalIgnoreCase);
    }

    private static string ResolveHtmlStableId(XElement node, int index)
    {
        return PickFirstNonEmpty(
                ReadAttribute(node, "data-message-id"),
                ReadAttribute(node, "id"))
            ?? $"element:{index}";
    }

    private static string? ReadAttribute(XElement node, string name)
    {
        return node.Attributes()
            .FirstOrDefault(item => string.Equals(item.Name.LocalName, name, StringComparison.OrdinalIgnoreCase))
            ?.Value
            ?.Trim();
    }

    private static string? ReadDescendantValue(XElement node, string token)
    {
        return node
            .Descendants()
            .FirstOrDefault(item =>
                string.Equals(item.Name.LocalName, token, StringComparison.OrdinalIgnoreCase)
                || AttributeContainsToken(item, "class", token)
                || AttributeContainsToken(item, "data-role", token))
            ?.Value
            ?.Trim();
    }

    private static string? ReadDescendantTimeValue(XElement node)
    {
        var timeNode = node.Descendants()
            .FirstOrDefault(item => string.Equals(item.Name.LocalName, "time", StringComparison.OrdinalIgnoreCase));
        return PickFirstNonEmpty(ReadAttribute(timeNode ?? node, "datetime"), timeNode?.Value?.Trim());
    }

    private static string? NormalizeElementText(XElement node)
    {
        var value = string.Join(" ", node
            .DescendantNodesAndSelf()
            .OfType<XText>()
            .Select(text => text.Value.Trim())
            .Where(text => !string.IsNullOrWhiteSpace(text)));
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }

    private static string? PickFirstNonEmpty(params string?[] values)
    {
        return values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));
    }

    private static bool AttributeContainsToken(XElement node, string attributeName, string token)
    {
        var value = ReadAttribute(node, attributeName);
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        return value.Split([' ', ';', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Any(part => string.Equals(part, token, StringComparison.OrdinalIgnoreCase));
    }

    private static string ResolveThreadKey(CanonicalMessageRecord item)
    {
        if (!string.IsNullOrWhiteSpace(item.ThreadExternalId))
        {
            return item.ThreadExternalId;
        }

        if (!string.IsNullOrWhiteSpace(item.SenderValue) || !string.IsNullOrWhiteSpace(item.RecipientValue))
        {
            return BuildDeterministicThreadKey(
                item.Platform,
                item.SenderValue,
                item.RecipientValue
            );
        }

        if (!string.IsNullOrWhiteSpace(item.MessageExternalId))
        {
            return $"message:{item.MessageExternalId}";
        }

        return $"source:{item.SourceLocator}";
    }

    private static string BuildDeterministicThreadKey(
        string platform,
        string? sender,
        string? recipients
    )
    {
        var canonical = string.Join(
            "|",
            CanonicalMessageRecord.NormalizePlatform(platform),
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

    private static void ReportCsvProgress(
        IProgress<MessageIngestProgress>? progress,
        FileStream stream,
        int rowsVisited,
        int totalConsidered
    )
    {
        if (progress is null)
        {
            return;
        }

        if (rowsVisited > 0 && rowsVisited % 25 != 0)
        {
            return;
        }

        var fraction = stream.Length <= 0
            ? 0.7
            : 0.03 + (Math.Clamp(stream.Position / (double)stream.Length, 0, 1) * 0.67);
        ReportProgress(progress, fraction, "Parsing message export...", totalConsidered, null);
    }

    private static void ReportXlsxProgress(
        IProgress<MessageIngestProgress>? progress,
        int rowsVisited,
        int totalRows
    )
    {
        if (progress is null)
        {
            return;
        }

        if (rowsVisited > 0 && rowsVisited % 5 != 0 && rowsVisited != totalRows)
        {
            return;
        }

        var fraction = totalRows == 0
            ? 0.7
            : 0.03 + (rowsVisited / (double)totalRows) * 0.67;
        ReportProgress(progress, fraction, "Parsing message export...", rowsVisited, totalRows);
    }

    private static void LogParserSelected(
        string? logContext,
        string parserFamily,
        string sourceLabel,
        string? containerLabel,
        string detection
    )
    {
        AppFileLogger.LogEvent(
            eventName: "MessagesIngestParserSelected",
            level: "INFO",
            message: BuildLogMessage(logContext, "Message ingest parser selected."),
            fields: new Dictionary<string, object?>
            {
                ["parserFamily"] = parserFamily,
                ["sourceLabel"] = sourceLabel,
                ["containerLabel"] = containerLabel,
                ["detection"] = detection
            }
        );
    }

    private static void LogSchemaMatched(
        string? logContext,
        string parserFamily,
        string sourceLabel,
        string? containerLabel,
        MessageCanonicalFieldMap fieldMap
    )
    {
        AppFileLogger.LogEvent(
            eventName: "MessagesIngestSchemaMatched",
            level: "INFO",
            message: BuildLogMessage(logContext, "Message ingest schema matched canonical fields."),
            fields: new Dictionary<string, object?>
            {
                ["parserFamily"] = parserFamily,
                ["sourceLabel"] = sourceLabel,
                ["containerLabel"] = containerLabel,
                ["matchedFields"] = string.Join(",", fieldMap.MatchedFields.Select(field => field.ToString())),
                ["matchedColumns"] = string.Join(
                    " | ",
                    fieldMap.Matches
                        .OrderBy(match => match.ColumnIndex)
                        .Select(match => $"{match.Field}:{match.Header}")),
                ["unmappedHeaders"] = string.Join(",", fieldMap.UnmappedHeaders),
                ["recognizedHeaderCount"] = fieldMap.Matches.Count,
                ["looksLikeMessageExport"] = fieldMap.LooksLikeMessageExport
            }
        );
    }

    private static void LogParserResult(
        string? logContext,
        string parserFamily,
        string sourceLabel,
        string? containerLabel,
        MessageCanonicalFieldMap? fieldMap,
        int processedRows,
        int parsedRows,
        int skippedRows,
        int warningsCount
    )
    {
        AppFileLogger.LogEvent(
            eventName: "MessagesIngestParserResult",
            level: "INFO",
            message: BuildLogMessage(logContext, "Message ingest parser completed."),
            fields: new Dictionary<string, object?>
            {
                ["parserFamily"] = parserFamily,
                ["sourceLabel"] = sourceLabel,
                ["containerLabel"] = containerLabel,
                ["processedRows"] = processedRows,
                ["parsedRows"] = parsedRows,
                ["skippedRows"] = skippedRows,
                ["warningCount"] = warningsCount,
                ["matchedFields"] = fieldMap is null
                    ? null
                    : string.Join(",", fieldMap.MatchedFields.Select(field => field.ToString())),
                ["unmappedHeaders"] = fieldMap is null
                    ? null
                    : string.Join(",", fieldMap.UnmappedHeaders)
            }
        );
    }

    private static string BuildLogMessage(string? context, string message)
    {
        return string.IsNullOrWhiteSpace(context)
            ? message
            : $"{context} {message}";
    }

    private sealed record ParseBatch(
        IReadOnlyList<CanonicalMessageRecord> Messages,
        string? EmptyStatusMessage,
        string? SummaryOverride = null
    )
    {
        public static ParseBatch Empty(string statusMessage) => new(
            Array.Empty<CanonicalMessageRecord>(),
            statusMessage,
            SummaryOverride: statusMessage
        );
    }

    private sealed record WorksheetCandidate(
        Sheet Sheet,
        IReadOnlyList<Row> Rows,
        MessageCanonicalFieldMap FieldMap
    );
}
