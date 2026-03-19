namespace CaseGraph.Infrastructure.GangDocumentation;

public sealed record GangDocumentationPacketBuildRequest(
    Guid CaseId,
    Guid DocumentationId,
    string Operator
);

public sealed record GangDocumentationPacketModel(
    Guid CaseId,
    string CaseName,
    Guid DocumentationId,
    Guid TargetId,
    string SuggestedFileName,
    DateTimeOffset GeneratedAtUtc,
    string Operator,
    GangDocumentationPacketSubjectSummary Subject,
    GangDocumentationPacketAnalystSection AnalystContent,
    GangDocumentationPacketWorkflowSection Workflow,
    GangDocumentationPacketEvidenceSection Evidence,
    GangDocumentationPacketContextSection Context
)
{
    public IReadOnlyList<GangDocumentationPacketCitation> AllCitations =>
        [
            .. Evidence.SupportingItems.Select(item => item.Citation),
            .. AnalystContent.Criteria
                .Where(item => !string.IsNullOrWhiteSpace(item.SourceNote))
                .Select(item => item.AnalystReferenceCitation)
                .Where(item => item is not null)
                .Cast<GangDocumentationPacketCitation>()
        ];
}

public sealed record GangDocumentationPacketSubjectSummary(
    string SubjectDisplayName,
    string? PrimaryAlias,
    string? GlobalDisplayName,
    string OrganizationName,
    string? SubgroupOrganizationName,
    string AffiliationRole,
    string WorkflowStatusDisplay
);

public sealed record GangDocumentationPacketAnalystSection(
    string Summary,
    string? Notes,
    IReadOnlyList<GangDocumentationPacketCriterionItem> Criteria,
    string SectionLabel,
    string SectionDescription
);

public sealed record GangDocumentationPacketCriterionItem(
    string CriterionType,
    bool IsMet,
    string BasisSummary,
    DateTimeOffset? ObservedDateUtc,
    string? SourceNote
)
{
    public string StatusDisplay => IsMet ? "Met" : "Not met";

    public GangDocumentationPacketCitation? AnalystReferenceCitation => string.IsNullOrWhiteSpace(SourceNote)
        ? null
        : new GangDocumentationPacketCitation(
            EvidenceItemId: null,
            SourceLocator: SourceNote!,
            MessageEventId: null,
            EvidenceDisplayName: "Analyst Reference",
            CitationText: SourceNote!,
            SourceKindLabel: "Analyst-entered reference"
        );
}

public sealed record GangDocumentationPacketWorkflowSection(
    string WorkflowStatus,
    string WorkflowStatusDisplay,
    string ApprovalStatus,
    string? ReviewerName,
    string? ReviewerIdentity,
    DateTimeOffset? SubmittedForReviewAtUtc,
    DateTimeOffset? ReviewedAtUtc,
    DateTimeOffset? ApprovedAtUtc,
    string? DecisionNote,
    string SectionLabel,
    string SectionDescription
);

public sealed record GangDocumentationPacketEvidenceSection(
    string SectionLabel,
    string SectionDescription,
    IReadOnlyList<GangDocumentationPacketEvidenceItem> SupportingItems
);

public sealed record GangDocumentationPacketEvidenceItem(
    Guid MessageEventId,
    DateTimeOffset? TimestampUtc,
    string Direction,
    string Participants,
    string Preview,
    GangDocumentationPacketCitation Citation
);

public sealed record GangDocumentationPacketContextSection(
    IReadOnlyList<GangDocumentationPacketCaseReference> LinkedCases,
    IReadOnlyList<string> LinkedIncidents,
    string SectionLabel,
    string SectionDescription
);

public sealed record GangDocumentationPacketCaseReference(
    Guid CaseId,
    string CaseName,
    string SubjectDisplayName
);

public sealed record GangDocumentationPacketCitation(
    Guid? EvidenceItemId,
    string SourceLocator,
    Guid? MessageEventId,
    string? EvidenceDisplayName,
    string CitationText,
    string SourceKindLabel
);

public sealed record GangDocumentationPacketExportResult(string OutputPath, string Format);
