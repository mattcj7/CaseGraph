using CaseGraph.Core.Abstractions;
using CaseGraph.Core.Models;
using CaseGraph.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using System.Net;
using System.Text;
using System.Text.Json;

namespace CaseGraph.Infrastructure.GangDocumentation;

public interface IGangDocumentationPacketExportService
{
    Task<GangDocumentationPacketModel> BuildPreviewAsync(
        GangDocumentationPacketBuildRequest request,
        CancellationToken ct
    );

    Task<GangDocumentationPacketExportResult> ExportHtmlAsync(
        GangDocumentationPacketModel model,
        string outputPath,
        CancellationToken ct
    );
}

public sealed class GangDocumentationPacketExportService : IGangDocumentationPacketExportService
{
    private const int MaxSupportingItems = 12;

    private readonly IDbContextFactory<WorkspaceDbContext> _dbContextFactory;
    private readonly IWorkspaceDatabaseInitializer _databaseInitializer;
    private readonly IAuditLogService _auditLogService;
    private readonly IClock _clock;

    public GangDocumentationPacketExportService(
        IDbContextFactory<WorkspaceDbContext> dbContextFactory,
        IWorkspaceDatabaseInitializer databaseInitializer,
        IAuditLogService auditLogService,
        IClock clock
    )
    {
        _dbContextFactory = dbContextFactory;
        _databaseInitializer = databaseInitializer;
        _auditLogService = auditLogService;
        _clock = clock;
    }

    public async Task<GangDocumentationPacketModel> BuildPreviewAsync(
        GangDocumentationPacketBuildRequest request,
        CancellationToken ct
    )
    {
        if (request.CaseId == Guid.Empty || request.DocumentationId == Guid.Empty)
        {
            throw new ArgumentException("CaseId and DocumentationId are required.", nameof(request));
        }

        var operatorName = NormalizeOperator(request.Operator);

        await _databaseInitializer.EnsureInitializedAsync(ct);
        await using var db = await _dbContextFactory.CreateDbContextAsync(ct);

        var documentation = await db.GangDocumentationRecords
            .AsNoTracking()
            .Include(record => record.Target)
            .Include(record => record.GlobalPerson)
            .Include(record => record.Organization)
            .Include(record => record.SubgroupOrganization)
            .Include(record => record.Review)
            .FirstOrDefaultAsync(
                record => record.CaseId == request.CaseId && record.DocumentationId == request.DocumentationId,
                ct
            );
        if (documentation is null)
        {
            throw new InvalidOperationException("Gang documentation record not found.");
        }

        var workflowStatus = NormalizeWorkflowStatus(
            documentation.Review?.WorkflowStatus ?? documentation.DocumentationStatus
        );
        if (!string.Equals(workflowStatus, GangDocumentationCatalog.WorkflowStatusApproved, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "Only Approved gang documentation records can be exported as a packet."
            );
        }

        var review = documentation.Review;
        var caseName = await db.Cases
            .AsNoTracking()
            .Where(record => record.CaseId == request.CaseId)
            .Select(record => record.Name)
            .FirstOrDefaultAsync(ct)
            ?? $"Case {request.CaseId:D}";

        var criteria = (await db.GangDocumentationCriteria
            .AsNoTracking()
            .Where(record => record.DocumentationId == request.DocumentationId)
            .ToListAsync(ct))
            .OrderBy(record => record.SortOrder)
            .ThenBy(record => record.CreatedAtUtc)
            .Select(record => new GangDocumentationPacketCriterionItem(
                record.CriterionType,
                record.IsMet,
                record.BasisSummary,
                record.ObservedDateUtc,
                record.SourceNote
            ))
            .ToList();

        var supportingItems = await GetSupportingEvidenceAsync(
            db,
            request.CaseId,
            documentation.TargetId,
            ct
        );
        var linkedCases = await GetLinkedCasesAsync(
            db,
            documentation.GlobalEntityId,
            documentation.TargetId,
            ct
        );

        var subjectDisplayName = documentation.Target?.DisplayName ?? $"Target {documentation.TargetId:D}";
        var generatedAtUtc = _clock.UtcNow.ToUniversalTime();

        return new GangDocumentationPacketModel(
            request.CaseId,
            caseName,
            documentation.DocumentationId,
            documentation.TargetId,
            BuildSuggestedFileName(caseName, subjectDisplayName),
            generatedAtUtc,
            operatorName,
            new GangDocumentationPacketSubjectSummary(
                subjectDisplayName,
                documentation.Target?.PrimaryAlias,
                documentation.GlobalPerson?.DisplayName,
                documentation.Organization?.Name ?? $"Organization {documentation.OrganizationId:D}",
                documentation.SubgroupOrganization?.Name,
                documentation.AffiliationRole,
                GangDocumentationCatalog.GetWorkflowStatusDisplayName(workflowStatus)
            ),
            new GangDocumentationPacketAnalystSection(
                documentation.Summary,
                documentation.Notes,
                criteria,
                SectionLabel: "Analyst-entered basis and criteria",
                SectionDescription: "This section contains analyst-entered gang documentation narrative and criteria basis. It is distinct from evidence-derived references and supervisor workflow metadata."
            ),
            new GangDocumentationPacketWorkflowSection(
                workflowStatus,
                GangDocumentationCatalog.GetWorkflowStatusDisplayName(workflowStatus),
                GangDocumentationCatalog.GetApprovalStatus(workflowStatus),
                review?.ReviewerName,
                review?.ReviewerIdentity,
                review?.SubmittedForReviewAtUtc,
                review?.ReviewedAtUtc,
                review?.ApprovedAtUtc,
                review?.DecisionNote,
                SectionLabel: "Supervisor approval metadata",
                SectionDescription: "This section contains workflow state, reviewer attribution, approval timing, and decision notes. It is distinct from analyst-entered basis and evidence-derived references."
            ),
            new GangDocumentationPacketEvidenceSection(
                SectionLabel: "Evidence-derived supporting references",
                SectionDescription: $"This section lists up to {MaxSupportingItems} linked message references drawn from indexed evidence-backed presence records.",
                SupportingItems: supportingItems
            ),
            new GangDocumentationPacketContextSection(
                linkedCases,
                LinkedIncidents: [],
                SectionLabel: "Linked case context",
                SectionDescription: "This section lists other linked cases already available through the shared person linkage. No new incident relationships are inferred during export."
            )
        );
    }

    public async Task<GangDocumentationPacketExportResult> ExportHtmlAsync(
        GangDocumentationPacketModel model,
        string outputPath,
        CancellationToken ct
    )
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);

        if (!string.Equals(model.Workflow.WorkflowStatus, GangDocumentationCatalog.WorkflowStatusApproved, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "Only Approved gang documentation records can be exported as a packet."
            );
        }

        var resolvedPath = ResolveOutputPath(outputPath, model.SuggestedFileName);
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
        await using var writer = new StreamWriter(
            stream,
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)
        );

        await writer.WriteLineAsync("<!DOCTYPE html>");
        await writer.WriteLineAsync("<html lang=\"en\">");
        await writer.WriteLineAsync("<head>");
        await writer.WriteLineAsync("  <meta charset=\"utf-8\" />");
        await writer.WriteLineAsync($"  <title>{Encode(model.Subject.SubjectDisplayName)} Gang Documentation Packet</title>");
        await writer.WriteLineAsync("  <style>");
        await writer.WriteLineAsync("    @page { size: A4; margin: 16mm; }");
        await writer.WriteLineAsync("    body { font-family: Georgia, 'Times New Roman', serif; color: #1c1b18; line-height: 1.45; }");
        await writer.WriteLineAsync("    h1, h2, h3 { font-family: 'Segoe UI', Tahoma, sans-serif; color: #20252c; }");
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

        await writer.WriteLineAsync($"  <h1>{Encode(model.Subject.SubjectDisplayName)} Gang Documentation Packet</h1>");
        await writer.WriteLineAsync("  <div class=\"meta\">");
        await writer.WriteLineAsync($"    <div>Case: {Encode(model.CaseName)} ({model.CaseId:D})</div>");
        await writer.WriteLineAsync($"    <div>Documentation Record: {model.DocumentationId:D}</div>");
        await writer.WriteLineAsync($"    <div>Subject Target: {model.TargetId:D}</div>");
        await writer.WriteLineAsync($"    <div>Generated (UTC): {FormatTimestamp(model.GeneratedAtUtc)} by {Encode(model.Operator)}</div>");
        await writer.WriteLineAsync($"    <div>Current Status: {Encode(model.Workflow.WorkflowStatusDisplay)}</div>");
        await writer.WriteLineAsync("  </div>");

        await WriteSubjectSummaryAsync(writer, model.Subject, ct);
        await WriteAnalystSectionAsync(writer, model.AnalystContent, ct);
        await WriteWorkflowSectionAsync(writer, model.Workflow, ct);
        await WriteEvidenceSectionAsync(writer, model.Evidence, ct);
        await WriteContextSectionAsync(writer, model.Context, ct);
        await WriteCitationsAppendixAsync(writer, model.AllCitations, ct);

        await writer.WriteLineAsync("</body>");
        await writer.WriteLineAsync("</html>");
        await writer.FlushAsync();

        await WriteAuditAsync(model, resolvedPath, ct);
        return new GangDocumentationPacketExportResult(resolvedPath, "html");
    }

    private static async Task WriteSubjectSummaryAsync(
        StreamWriter writer,
        GangDocumentationPacketSubjectSummary subject,
        CancellationToken ct
    )
    {
        await writer.WriteLineAsync("  <h2>Subject Summary</h2>");
        await writer.WriteLineAsync("  <table>");
        await writer.WriteLineAsync("    <tbody>");
        await WriteKeyValueRowAsync(writer, "Subject", subject.SubjectDisplayName, ct);
        await WriteKeyValueRowAsync(writer, "Primary Alias", subject.PrimaryAlias, ct);
        await WriteKeyValueRowAsync(writer, "Linked Global Person", subject.GlobalDisplayName, ct);
        await WriteKeyValueRowAsync(writer, "Organization", subject.OrganizationName, ct);
        await WriteKeyValueRowAsync(writer, "Subgroup / Set / Clique", subject.SubgroupOrganizationName, ct);
        await WriteKeyValueRowAsync(writer, "Affiliation Role", subject.AffiliationRole, ct);
        await WriteKeyValueRowAsync(writer, "Workflow Status", subject.WorkflowStatusDisplay, ct);
        await writer.WriteLineAsync("    </tbody>");
        await writer.WriteLineAsync("  </table>");
    }

    private static async Task WriteAnalystSectionAsync(
        StreamWriter writer,
        GangDocumentationPacketAnalystSection section,
        CancellationToken ct
    )
    {
        await writer.WriteLineAsync($"  <h2>{Encode(section.SectionLabel)}</h2>");
        await writer.WriteLineAsync($"  <p class=\"muted\">{Encode(section.SectionDescription)}</p>");
        await writer.WriteLineAsync("  <div class=\"card\">");
        await writer.WriteLineAsync($"    <div><strong>Summary:</strong> {Encode(section.Summary)}</div>");
        await writer.WriteLineAsync($"    <div style=\"margin-top:0.5rem;\"><strong>Notes:</strong> {Encode(section.Notes ?? "(none)")}</div>");
        await writer.WriteLineAsync("  </div>");

        await writer.WriteLineAsync("  <table>");
        await writer.WriteLineAsync("    <thead><tr><th>Criterion</th><th>Status</th><th>Basis Summary</th><th>Observed (UTC)</th><th>Analyst Reference</th></tr></thead>");
        await writer.WriteLineAsync("    <tbody>");
        if (section.Criteria.Count == 0)
        {
            await writer.WriteLineAsync("      <tr><td colspan=\"5\" class=\"muted\">No criteria were recorded for this documentation record.</td></tr>");
        }
        else
        {
            foreach (var item in section.Criteria)
            {
                ct.ThrowIfCancellationRequested();
                await writer.WriteLineAsync("      <tr>");
                await writer.WriteLineAsync($"        <td>{Encode(item.CriterionType)}</td>");
                await writer.WriteLineAsync($"        <td>{Encode(item.StatusDisplay)}</td>");
                await writer.WriteLineAsync($"        <td>{Encode(item.BasisSummary)}</td>");
                await writer.WriteLineAsync($"        <td>{FormatNullableTimestamp(item.ObservedDateUtc)}</td>");
                await writer.WriteLineAsync($"        <td>{Encode(item.SourceNote ?? "(none)")}</td>");
                await writer.WriteLineAsync("      </tr>");
            }
        }

        await writer.WriteLineAsync("    </tbody>");
        await writer.WriteLineAsync("  </table>");
    }

    private static async Task WriteWorkflowSectionAsync(
        StreamWriter writer,
        GangDocumentationPacketWorkflowSection section,
        CancellationToken ct
    )
    {
        await writer.WriteLineAsync($"  <h2>{Encode(section.SectionLabel)}</h2>");
        await writer.WriteLineAsync($"  <p class=\"muted\">{Encode(section.SectionDescription)}</p>");
        await writer.WriteLineAsync("  <table>");
        await writer.WriteLineAsync("    <tbody>");
        await WriteKeyValueRowAsync(writer, "Workflow Status", section.WorkflowStatusDisplay, ct);
        await WriteKeyValueRowAsync(writer, "Approval Status", section.ApprovalStatus, ct);
        await WriteKeyValueRowAsync(writer, "Reviewer Name", section.ReviewerName, ct);
        await WriteKeyValueRowAsync(writer, "Reviewer Identity", section.ReviewerIdentity, ct);
        await WriteKeyValueRowAsync(writer, "Submitted For Review (UTC)", FormatNullableTimestamp(section.SubmittedForReviewAtUtc), ct);
        await WriteKeyValueRowAsync(writer, "Reviewed (UTC)", FormatNullableTimestamp(section.ReviewedAtUtc), ct);
        await WriteKeyValueRowAsync(writer, "Approved (UTC)", FormatNullableTimestamp(section.ApprovedAtUtc), ct);
        await WriteKeyValueRowAsync(writer, "Decision Note", section.DecisionNote, ct);
        await writer.WriteLineAsync("    </tbody>");
        await writer.WriteLineAsync("  </table>");
    }

    private static async Task WriteEvidenceSectionAsync(
        StreamWriter writer,
        GangDocumentationPacketEvidenceSection section,
        CancellationToken ct
    )
    {
        await writer.WriteLineAsync($"  <h2>{Encode(section.SectionLabel)}</h2>");
        await writer.WriteLineAsync($"  <p class=\"muted\">{Encode(section.SectionDescription)}</p>");
        if (section.SupportingItems.Count == 0)
        {
            await writer.WriteLineAsync("  <p class=\"muted\">No evidence-derived supporting references were available from indexed target message presence records.</p>");
            return;
        }

        await writer.WriteLineAsync("  <table>");
        await writer.WriteLineAsync("    <thead><tr><th>Time (UTC)</th><th>Direction</th><th>Participants</th><th>Preview</th><th>Citation</th></tr></thead>");
        await writer.WriteLineAsync("    <tbody>");
        foreach (var item in section.SupportingItems)
        {
            ct.ThrowIfCancellationRequested();
            await writer.WriteLineAsync("      <tr>");
            await writer.WriteLineAsync($"        <td>{FormatNullableTimestamp(item.TimestampUtc)}</td>");
            await writer.WriteLineAsync($"        <td>{Encode(item.Direction)}</td>");
            await writer.WriteLineAsync($"        <td>{Encode(item.Participants)}</td>");
            await writer.WriteLineAsync($"        <td>{Encode(item.Preview)}</td>");
            await writer.WriteLineAsync($"        <td>{Encode(item.Citation.CitationText)}</td>");
            await writer.WriteLineAsync("      </tr>");
        }

        await writer.WriteLineAsync("    </tbody>");
        await writer.WriteLineAsync("  </table>");
    }

    private static async Task WriteContextSectionAsync(
        StreamWriter writer,
        GangDocumentationPacketContextSection section,
        CancellationToken ct
    )
    {
        await writer.WriteLineAsync($"  <h2>{Encode(section.SectionLabel)}</h2>");
        await writer.WriteLineAsync($"  <p class=\"muted\">{Encode(section.SectionDescription)}</p>");

        await writer.WriteLineAsync("  <h3>Linked Cases</h3>");
        if (section.LinkedCases.Count == 0)
        {
            await writer.WriteLineAsync("  <p class=\"muted\">No linked cases were available through existing person linkage.</p>");
        }
        else
        {
            await writer.WriteLineAsync("  <ul>");
            foreach (var linkedCase in section.LinkedCases)
            {
                ct.ThrowIfCancellationRequested();
                await writer.WriteLineAsync($"    <li>{Encode(linkedCase.CaseName)} ({linkedCase.CaseId:D}) - {Encode(linkedCase.SubjectDisplayName)}</li>");
            }

            await writer.WriteLineAsync("  </ul>");
        }

        await writer.WriteLineAsync("  <h3>Linked Incidents</h3>");
        if (section.LinkedIncidents.Count == 0)
        {
            await writer.WriteLineAsync("  <p class=\"muted\">No linked incidents were directly available for this export.</p>");
        }
        else
        {
            await writer.WriteLineAsync("  <ul>");
            foreach (var incident in section.LinkedIncidents)
            {
                ct.ThrowIfCancellationRequested();
                await writer.WriteLineAsync($"    <li>{Encode(incident)}</li>");
            }

            await writer.WriteLineAsync("  </ul>");
        }
    }

    private static async Task WriteCitationsAppendixAsync(
        StreamWriter writer,
        IReadOnlyList<GangDocumentationPacketCitation> citations,
        CancellationToken ct
    )
    {
        await writer.WriteLineAsync("  <h2>Appendix: Citations</h2>");
        if (citations.Count == 0)
        {
            await writer.WriteLineAsync("  <p class=\"muted\">No citations were assembled for this packet.</p>");
            return;
        }

        await writer.WriteLineAsync("  <table>");
        await writer.WriteLineAsync("    <thead><tr><th>Source Kind</th><th>Evidence Item</th><th>Source Locator</th><th>Message Event</th><th>Citation</th></tr></thead>");
        await writer.WriteLineAsync("    <tbody>");
        foreach (var citation in citations
            .OrderBy(item => item.SourceKindLabel, StringComparer.Ordinal)
            .ThenBy(item => item.SourceLocator, StringComparer.Ordinal)
            .ThenBy(item => item.MessageEventId))
        {
            ct.ThrowIfCancellationRequested();
            await writer.WriteLineAsync("      <tr>");
            await writer.WriteLineAsync($"        <td>{Encode(citation.SourceKindLabel)}</td>");
            await writer.WriteLineAsync($"        <td>{Encode(citation.EvidenceDisplayName ?? citation.EvidenceItemId?.ToString("D") ?? "(none)")}</td>");
            await writer.WriteLineAsync($"        <td>{Encode(citation.SourceLocator)}</td>");
            await writer.WriteLineAsync($"        <td>{Encode(citation.MessageEventId?.ToString("D") ?? "(none)")}</td>");
            await writer.WriteLineAsync($"        <td>{Encode(citation.CitationText)}</td>");
            await writer.WriteLineAsync("      </tr>");
        }

        await writer.WriteLineAsync("    </tbody>");
        await writer.WriteLineAsync("  </table>");
    }

    private async Task<IReadOnlyList<GangDocumentationPacketEvidenceItem>> GetSupportingEvidenceAsync(
        WorkspaceDbContext db,
        Guid caseId,
        Guid targetId,
        CancellationToken ct
    )
    {
        var rows = await (
            from presence in db.TargetMessagePresences.AsNoTracking()
            join message in db.MessageEvents.AsNoTracking()
                on presence.MessageEventId equals message.MessageEventId
            join evidence in db.EvidenceItems.AsNoTracking()
                on message.EvidenceItemId equals evidence.EvidenceItemId
            where presence.CaseId == caseId && presence.TargetId == targetId
            select new SupportingEvidenceRow(
                message.MessageEventId,
                message.EvidenceItemId,
                message.TimestampUtc,
                message.Direction,
                message.Sender,
                message.Recipients,
                message.Body,
                message.SourceLocator,
                evidence.DisplayName
            )
        ).ToListAsync(ct);

        return rows
            .GroupBy(row => row.MessageEventId)
            .Select(group =>
            {
                var first = group
                    .OrderByDescending(item => item.TimestampUtc)
                    .ThenByDescending(item => item.MessageEventId)
                    .First();
                return new GangDocumentationPacketEvidenceItem(
                    first.MessageEventId,
                    first.TimestampUtc,
                    NormalizeDirection(first.Direction),
                    BuildParticipants(first.Sender, first.Recipients),
                    BuildPreviewText(first.Body),
                    new GangDocumentationPacketCitation(
                        first.EvidenceItemId,
                        first.SourceLocator,
                        first.MessageEventId,
                        first.EvidenceDisplayName,
                        BuildEvidenceCitationText(first.EvidenceDisplayName, first.SourceLocator, first.MessageEventId),
                        "Evidence-derived message reference"
                    )
                );
            })
            .OrderByDescending(item => item.TimestampUtc)
            .ThenByDescending(item => item.MessageEventId)
            .Take(MaxSupportingItems)
            .ToList();
    }

    private static async Task<IReadOnlyList<GangDocumentationPacketCaseReference>> GetLinkedCasesAsync(
        WorkspaceDbContext db,
        Guid? globalEntityId,
        Guid currentTargetId,
        CancellationToken ct
    )
    {
        if (!globalEntityId.HasValue)
        {
            return [];
        }

        var rows = await (
            from target in db.Targets.AsNoTracking()
            join caseRecord in db.Cases.AsNoTracking()
                on target.CaseId equals caseRecord.CaseId
            where target.GlobalEntityId == globalEntityId.Value
                && target.TargetId != currentTargetId
            select new LinkedCaseRow(
                target.CaseId,
                caseRecord.Name,
                target.DisplayName,
                target.UpdatedAtUtc
            )
        ).ToListAsync(ct);

        return rows
            .GroupBy(row => row.CaseId)
            .Select(group => group
                .OrderByDescending(item => item.TargetUpdatedAtUtc)
                .ThenBy(item => item.SubjectDisplayName, StringComparer.OrdinalIgnoreCase)
                .First())
            .OrderBy(item => item.CaseName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.SubjectDisplayName, StringComparer.OrdinalIgnoreCase)
            .Select(item => new GangDocumentationPacketCaseReference(
                item.CaseId,
                item.CaseName,
                item.SubjectDisplayName
            ))
            .ToList();
    }

    private async Task WriteAuditAsync(
        GangDocumentationPacketModel model,
        string outputPath,
        CancellationToken ct
    )
    {
        await _auditLogService.AddAsync(
            new AuditEvent
            {
                TimestampUtc = _clock.UtcNow.ToUniversalTime(),
                Operator = model.Operator,
                ActionType = "GangDocumentationPacketExported",
                CaseId = model.CaseId,
                Summary = $"Gang documentation packet exported to {outputPath}.",
                JsonPayload = JsonSerializer.Serialize(new
                {
                    model.DocumentationId,
                    model.TargetId,
                    model.Subject.SubjectDisplayName,
                    WorkflowStatus = model.Workflow.WorkflowStatus,
                    model.Workflow.ReviewerName,
                    model.Workflow.ReviewedAtUtc,
                    model.Workflow.ApprovedAtUtc,
                    OutputPath = outputPath,
                    Format = "html",
                    model.Operator
                })
            },
            ct
        );
    }

    private static async Task WriteKeyValueRowAsync(
        StreamWriter writer,
        string label,
        string? value,
        CancellationToken ct
    )
    {
        ct.ThrowIfCancellationRequested();
        await writer.WriteLineAsync("      <tr>");
        await writer.WriteLineAsync($"        <th>{Encode(label)}</th>");
        await writer.WriteLineAsync($"        <td>{Encode(string.IsNullOrWhiteSpace(value) ? "(none)" : value)}</td>");
        await writer.WriteLineAsync("      </tr>");
    }

    private static string NormalizeOperator(string? value)
    {
        var normalized = value?.Trim();
        return string.IsNullOrWhiteSpace(normalized) ? Environment.UserName : normalized;
    }

    private static string NormalizeWorkflowStatus(string? workflowStatus)
    {
        return workflowStatus?.Trim().ToLowerInvariant() switch
        {
            "pending review" => GangDocumentationCatalog.WorkflowStatusPendingSupervisorReview,
            null or "" => GangDocumentationCatalog.WorkflowStatusDraft,
            var normalized when GangDocumentationCatalog.WorkflowStatuses.Contains(normalized, StringComparer.Ordinal) => normalized,
            _ => GangDocumentationCatalog.WorkflowStatusDraft
        };
    }

    private static string BuildSuggestedFileName(string caseName, string subjectDisplayName)
    {
        return $"{caseName}-{subjectDisplayName}-gang-documentation-packet";
    }

    private static string NormalizeDirection(string? direction)
    {
        return string.IsNullOrWhiteSpace(direction) ? "Unknown" : direction.Trim();
    }

    private static string BuildParticipants(string? sender, string? recipients)
    {
        var normalizedSender = string.IsNullOrWhiteSpace(sender) ? "(unknown sender)" : sender.Trim();
        var normalizedRecipients = string.IsNullOrWhiteSpace(recipients) ? "(unknown recipients)" : recipients.Trim();
        return $"{normalizedSender} -> {normalizedRecipients}";
    }

    private static string BuildPreviewText(string? body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return "(no message body available)";
        }

        var normalized = string.Join(' ', body.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return normalized.Length <= 220 ? normalized : normalized[..217] + "...";
    }

    private static string BuildEvidenceCitationText(
        string? evidenceDisplayName,
        string sourceLocator,
        Guid messageEventId
    )
    {
        return $"{(string.IsNullOrWhiteSpace(evidenceDisplayName) ? "Evidence item" : evidenceDisplayName)} | {sourceLocator} | {messageEventId:D}";
    }

    private static string ResolveOutputPath(string outputPath, string suggestedFileName)
    {
        var directory = Path.GetDirectoryName(outputPath);
        if (string.IsNullOrWhiteSpace(directory))
        {
            directory = Environment.CurrentDirectory;
        }

        var fileNameWithoutExtension = Path.GetFileNameWithoutExtension(outputPath);
        if (string.IsNullOrWhiteSpace(fileNameWithoutExtension))
        {
            fileNameWithoutExtension = suggestedFileName;
        }

        var extension = Path.GetExtension(outputPath);
        if (string.IsNullOrWhiteSpace(extension))
        {
            extension = ".html";
        }

        return Path.Combine(directory, SanitizeFileName(fileNameWithoutExtension) + extension);
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
            ? "gang-documentation-packet"
            : sanitized;
    }

    private static string Encode(string? value)
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

    private sealed record SupportingEvidenceRow(
        Guid MessageEventId,
        Guid EvidenceItemId,
        DateTimeOffset? TimestampUtc,
        string Direction,
        string? Sender,
        string? Recipients,
        string? Body,
        string SourceLocator,
        string EvidenceDisplayName
    );

    private sealed record LinkedCaseRow(
        Guid CaseId,
        string CaseName,
        string SubjectDisplayName,
        DateTimeOffset TargetUpdatedAtUtc
    );
}

