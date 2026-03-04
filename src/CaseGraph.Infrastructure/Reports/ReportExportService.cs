using System.Net;
using System.Text;

namespace CaseGraph.Infrastructure.Reports;

public sealed class ReportExportService
{
    public async Task<ReportExportResult> ExportHtmlAsync(
        DossierReportModel model,
        string outputPath,
        CancellationToken ct
    )
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);

        var resolvedPath = ResolveOutputPath(outputPath);
        var directory = Path.GetDirectoryName(resolvedPath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        await using var stream = new FileStream(
            resolvedPath,
            FileMode.Create,
            FileAccess.Write,
            FileShare.None,
            bufferSize: 16 * 1024,
            options: FileOptions.Asynchronous
        );
        await using var writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

        await writer.WriteLineAsync("<!DOCTYPE html>");
        await writer.WriteLineAsync("<html lang=\"en\">");
        await writer.WriteLineAsync("<head>");
        await writer.WriteLineAsync("  <meta charset=\"utf-8\" />");
        await writer.WriteLineAsync($"  <title>{Encode(model.CaseName)} - {Encode(model.SubjectDisplayName)} Dossier</title>");
        await writer.WriteLineAsync("  <style>");
        await writer.WriteLineAsync("    @page { size: A4; margin: 16mm; }");
        await writer.WriteLineAsync("    body { font-family: Georgia, 'Times New Roman', serif; color: #1c1b18; line-height: 1.45; }");
        await writer.WriteLineAsync("    h1, h2, h3 { font-family: 'Segoe UI', Tahoma, sans-serif; color: #20252c; }");
        await writer.WriteLineAsync("    h1 { margin-bottom: 0.2rem; }");
        await writer.WriteLineAsync("    h2 { margin-top: 2rem; border-bottom: 1px solid #c8ccd2; padding-bottom: 0.35rem; }");
        await writer.WriteLineAsync("    .meta { color: #47505b; margin-bottom: 1.25rem; }");
        await writer.WriteLineAsync("    .card { border: 1px solid #c8ccd2; border-radius: 6px; padding: 1rem; margin-top: 1rem; }");
        await writer.WriteLineAsync("    table { width: 100%; border-collapse: collapse; margin-top: 0.75rem; }");
        await writer.WriteLineAsync("    th, td { border: 1px solid #d7dbe0; padding: 0.45rem 0.55rem; vertical-align: top; text-align: left; }");
        await writer.WriteLineAsync("    th { background: #f3f4f6; font-family: 'Segoe UI', Tahoma, sans-serif; }");
        await writer.WriteLineAsync("    .muted { color: #5f6a75; }");
        await writer.WriteLineAsync("    .excerpt { white-space: pre-wrap; background: #faf8f4; border-left: 3px solid #c2a878; padding: 0.75rem; }");
        await writer.WriteLineAsync("    ul.citations { margin: 0.45rem 0 0 1rem; padding: 0; }");
        await writer.WriteLineAsync("    li { margin: 0.2rem 0; }");
        await writer.WriteLineAsync("  </style>");
        await writer.WriteLineAsync("</head>");
        await writer.WriteLineAsync("<body>");

        await writer.WriteLineAsync($"  <h1>{Encode(model.SubjectDisplayName)} Dossier</h1>");
        await writer.WriteLineAsync("  <div class=\"meta\">");
        await writer.WriteLineAsync($"    <div>Case: {Encode(model.CaseName)} ({model.CaseId:D})</div>");
        await writer.WriteLineAsync($"    <div>Subject: {Encode(model.SubjectCaption)} ({model.SubjectId:D})</div>");
        await writer.WriteLineAsync($"    <div>Generated (UTC): {FormatTimestamp(model.GeneratedAtUtc)} by {Encode(model.Operator)}</div>");
        await writer.WriteLineAsync($"    <div>Date Range: {FormatRange(model.FromUtc, model.ToUtc)}</div>");
        await writer.WriteLineAsync($"    <div>Format: Print-ready HTML</div>");
        await writer.WriteLineAsync("  </div>");

        if (model.SubjectIdentifiers is not null)
        {
            await WriteSubjectIdentifiersAsync(writer, model.SubjectIdentifiers, ct);
        }

        if (model.WhereSeenSummary is not null)
        {
            await WriteWhereSeenAsync(writer, model.WhereSeenSummary, ct);
        }

        if (model.TimelineExcerpt is not null)
        {
            await WriteTimelineAsync(writer, model.TimelineExcerpt, ct);
        }

        if (model.NotableExcerpts is not null)
        {
            await WriteNotableExcerptsAsync(writer, model.NotableExcerpts, ct);
        }

        if (model.IncludedSections.IncludeAppendix)
        {
            await WriteAppendixAsync(writer, model.AppendixCitations, ct);
        }

        await writer.WriteLineAsync("</body>");
        await writer.WriteLineAsync("</html>");
        await writer.FlushAsync(ct);

        return new ReportExportResult(resolvedPath, "html");
    }

    private static async Task WriteSubjectIdentifiersAsync(
        StreamWriter writer,
        DossierSubjectIdentifiersSection section,
        CancellationToken ct
    )
    {
        await writer.WriteLineAsync("  <h2>Subject Identifiers</h2>");
        await writer.WriteLineAsync($"  <p class=\"muted\">{Encode(section.Description)}</p>");
        if (section.Entries.Count == 0)
        {
            return;
        }

        await writer.WriteLineAsync("  <table>");
        await writer.WriteLineAsync("    <thead><tr><th>Type</th><th>Value</th><th>Primary</th><th>Why Linked</th><th>Citations</th></tr></thead>");
        await writer.WriteLineAsync("    <tbody>");
        foreach (var entry in section.Entries)
        {
            ct.ThrowIfCancellationRequested();
            await writer.WriteLineAsync("      <tr>");
            await writer.WriteLineAsync($"        <td>{Encode(entry.Type.ToString())}</td>");
            await writer.WriteLineAsync($"        <td>{Encode(entry.ValueDisplay)}<div class=\"muted\">{Encode(entry.ValueNormalized)}</div></td>");
            await writer.WriteLineAsync($"        <td>{(entry.IsPrimary ? "Yes" : "No")}</td>");
            await writer.WriteLineAsync($"        <td>{Encode(entry.WhyLinked)}</td>");
            await writer.WriteLineAsync("        <td>");
            await WriteCitationsAsync(writer, entry.Citations, emptyText: "No evidence citation available.", ct);
            await writer.WriteLineAsync("        </td>");
            await writer.WriteLineAsync("      </tr>");
        }

        await writer.WriteLineAsync("    </tbody>");
        await writer.WriteLineAsync("  </table>");
    }

    private static async Task WriteWhereSeenAsync(
        StreamWriter writer,
        DossierWhereSeenSection section,
        CancellationToken ct
    )
    {
        await writer.WriteLineAsync("  <h2>Where Seen Summary</h2>");
        await writer.WriteLineAsync("  <div class=\"card\">");
        await writer.WriteLineAsync($"    <div>Total linked messages: {section.TotalMessages:0}</div>");
        await writer.WriteLineAsync($"    <div>First seen (UTC): {FormatNullableTimestamp(section.FirstSeenUtc)}</div>");
        await writer.WriteLineAsync($"    <div>Last seen (UTC): {FormatNullableTimestamp(section.LastSeenUtc)}</div>");
        await writer.WriteLineAsync($"    <div class=\"muted\">{Encode(section.Notes)}</div>");
        if (section.TopCounterparties.Count > 0)
        {
            await writer.WriteLineAsync("    <h3>Top Counterparties</h3>");
            await writer.WriteLineAsync("    <ul>");
            foreach (var counterparty in section.TopCounterparties)
            {
                ct.ThrowIfCancellationRequested();
                await writer.WriteLineAsync($"      <li>{Encode(counterparty.Counterparty)} ({counterparty.Count:0})</li>");
            }

            await writer.WriteLineAsync("    </ul>");
        }

        await writer.WriteLineAsync("    <h3>Representative Citations</h3>");
        await WriteCitationsAsync(writer, section.RepresentativeCitations, emptyText: "No citations available.", ct);
        await writer.WriteLineAsync("  </div>");
    }

    private static async Task WriteTimelineAsync(
        StreamWriter writer,
        DossierTimelineSection section,
        CancellationToken ct
    )
    {
        await writer.WriteLineAsync("  <h2>Timeline Excerpt</h2>");
        await writer.WriteLineAsync($"  <p class=\"muted\">{Encode(section.Notes)}</p>");
        if (section.Entries.Count == 0)
        {
            return;
        }

        await writer.WriteLineAsync("  <table>");
        await writer.WriteLineAsync("    <thead><tr><th>Time (UTC)</th><th>Direction</th><th>Participants</th><th>Preview</th><th>Citations</th></tr></thead>");
        await writer.WriteLineAsync("    <tbody>");
        foreach (var entry in section.Entries)
        {
            ct.ThrowIfCancellationRequested();
            await writer.WriteLineAsync("      <tr>");
            await writer.WriteLineAsync($"        <td>{FormatNullableTimestamp(entry.TimestampUtc)}</td>");
            await writer.WriteLineAsync($"        <td>{Encode(entry.Direction)}</td>");
            await writer.WriteLineAsync($"        <td>{Encode(entry.Participants)}</td>");
            await writer.WriteLineAsync($"        <td>{Encode(entry.Preview)}</td>");
            await writer.WriteLineAsync("        <td>");
            await WriteCitationsAsync(writer, entry.Citations, emptyText: "No citations available.", ct);
            await writer.WriteLineAsync("        </td>");
            await writer.WriteLineAsync("      </tr>");
        }

        await writer.WriteLineAsync("    </tbody>");
        await writer.WriteLineAsync("  </table>");
    }

    private static async Task WriteNotableExcerptsAsync(
        StreamWriter writer,
        DossierNotableExcerptsSection section,
        CancellationToken ct
    )
    {
        await writer.WriteLineAsync("  <h2>Notable Message Excerpts</h2>");
        await writer.WriteLineAsync($"  <p class=\"muted\">{Encode(section.Notes)}</p>");
        foreach (var entry in section.Entries)
        {
            ct.ThrowIfCancellationRequested();
            await writer.WriteLineAsync("  <div class=\"card\">");
            await writer.WriteLineAsync($"    <div><strong>Message Event:</strong> {entry.MessageEventId:D}</div>");
            await writer.WriteLineAsync($"    <div><strong>Time (UTC):</strong> {FormatNullableTimestamp(entry.TimestampUtc)}</div>");
            await writer.WriteLineAsync($"    <div><strong>Sender:</strong> {Encode(entry.Sender)}</div>");
            await writer.WriteLineAsync($"    <div><strong>Recipients:</strong> {Encode(entry.Recipients)}</div>");
            await writer.WriteLineAsync($"    <div class=\"excerpt\">{Encode(entry.Excerpt)}</div>");
            await WriteCitationsAsync(writer, entry.Citations, emptyText: "No citations available.", ct);
            await writer.WriteLineAsync("  </div>");
        }
    }

    private static async Task WriteAppendixAsync(
        StreamWriter writer,
        IReadOnlyList<DossierCitation> citations,
        CancellationToken ct
    )
    {
        await writer.WriteLineAsync("  <h2>Appendix: Citations</h2>");
        if (citations.Count == 0)
        {
            await writer.WriteLineAsync("  <p class=\"muted\">No appendix citations were generated.</p>");
            return;
        }

        await writer.WriteLineAsync("  <table>");
        await writer.WriteLineAsync("    <thead><tr><th>Evidence Item</th><th>Source Locator</th><th>Message Event</th><th>Citation</th></tr></thead>");
        await writer.WriteLineAsync("    <tbody>");
        foreach (var citation in citations)
        {
            ct.ThrowIfCancellationRequested();
            await writer.WriteLineAsync("      <tr>");
            await writer.WriteLineAsync($"        <td>{Encode(citation.EvidenceDisplayName ?? citation.EvidenceItemId.ToString("D"))}</td>");
            await writer.WriteLineAsync($"        <td>{Encode(citation.SourceLocator)}</td>");
            await writer.WriteLineAsync($"        <td>{(citation.MessageEventId.HasValue ? citation.MessageEventId.Value.ToString("D") : "(none)")}</td>");
            await writer.WriteLineAsync($"        <td>{Encode(citation.CitationText)}</td>");
            await writer.WriteLineAsync("      </tr>");
        }

        await writer.WriteLineAsync("    </tbody>");
        await writer.WriteLineAsync("  </table>");
    }

    private static async Task WriteCitationsAsync(
        StreamWriter writer,
        IReadOnlyList<DossierCitation> citations,
        string emptyText,
        CancellationToken ct
    )
    {
        if (citations.Count == 0)
        {
            await writer.WriteLineAsync($"          <div class=\"muted\">{Encode(emptyText)}</div>");
            return;
        }

        await writer.WriteLineAsync("          <ul class=\"citations\">");
        foreach (var citation in citations)
        {
            ct.ThrowIfCancellationRequested();
            await writer.WriteLineAsync($"            <li>{Encode(citation.CitationText)}</li>");
        }

        await writer.WriteLineAsync("          </ul>");
    }

    private static string ResolveOutputPath(string outputPath)
    {
        var directory = Path.GetDirectoryName(outputPath);
        if (string.IsNullOrWhiteSpace(directory))
        {
            directory = Environment.CurrentDirectory;
        }

        var fileNameWithoutExtension = Path.GetFileNameWithoutExtension(outputPath);
        if (string.IsNullOrWhiteSpace(fileNameWithoutExtension))
        {
            fileNameWithoutExtension = "dossier";
        }

        var extension = Path.GetExtension(outputPath);
        if (string.IsNullOrWhiteSpace(extension))
        {
            extension = ".html";
        }

        var sanitizedFileName = SanitizeFileName(fileNameWithoutExtension);
        return Path.Combine(directory, sanitizedFileName + extension);
    }

    private static string SanitizeFileName(string value)
    {
        var invalidCharacters = Path.GetInvalidFileNameChars();
        var builder = new StringBuilder(value.Length);
        foreach (var character in value.Trim())
        {
            builder.Append(invalidCharacters.Contains(character) ? '_' : character);
        }

        var sanitized = builder.ToString().Trim();
        return string.IsNullOrWhiteSpace(sanitized)
            ? "dossier"
            : sanitized;
    }

    private static string Encode(string value)
    {
        return WebUtility.HtmlEncode(value ?? string.Empty);
    }

    private static string FormatTimestamp(DateTimeOffset value)
    {
        return value.ToUniversalTime().ToString("u");
    }

    private static string FormatNullableTimestamp(DateTimeOffset? value)
    {
        return value.HasValue ? FormatTimestamp(value.Value) : "(none)";
    }

    private static string FormatRange(DateTimeOffset? fromUtc, DateTimeOffset? toUtc)
    {
        return $"{FormatNullableTimestamp(fromUtc)} to {FormatNullableTimestamp(toUtc)}";
    }
}
