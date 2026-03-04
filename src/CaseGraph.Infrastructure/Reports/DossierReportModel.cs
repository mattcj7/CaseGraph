using CaseGraph.Core.Models;

namespace CaseGraph.Infrastructure.Reports;

public static class DossierSubjectTypes
{
    public const string Target = "Target";
    public const string GlobalPerson = "GlobalPerson";

    public static string Normalize(string? subjectType)
    {
        if (string.Equals(subjectType, Target, StringComparison.OrdinalIgnoreCase))
        {
            return Target;
        }

        if (string.Equals(subjectType, GlobalPerson, StringComparison.OrdinalIgnoreCase))
        {
            return GlobalPerson;
        }

        throw new ArgumentException(
            $"Unsupported dossier subject type '{subjectType ?? "(null)"}'.",
            nameof(subjectType)
        );
    }
}

public sealed record DossierSectionSelection(
    bool IncludeSubjectIdentifiers = true,
    bool IncludeWhereSeenSummary = true,
    bool IncludeTimelineExcerpt = true,
    bool IncludeNotableMessageExcerpts = true,
    bool IncludeAppendix = true
)
{
    public bool HasAnySelectedSection =>
        IncludeSubjectIdentifiers
        || IncludeWhereSeenSummary
        || IncludeTimelineExcerpt
        || IncludeNotableMessageExcerpts
        || IncludeAppendix;

    public IReadOnlyList<string> ToIncludedSectionNames()
    {
        var names = new List<string>();
        if (IncludeSubjectIdentifiers)
        {
            names.Add("SubjectIdentifiers");
        }

        if (IncludeWhereSeenSummary)
        {
            names.Add("WhereSeenSummary");
        }

        if (IncludeTimelineExcerpt)
        {
            names.Add("TimelineExcerpt");
        }

        if (IncludeNotableMessageExcerpts)
        {
            names.Add("NotableMessageExcerpts");
        }

        if (IncludeAppendix)
        {
            names.Add("Appendix");
        }

        return names;
    }
}

public sealed record DossierBuildRequest(
    Guid CaseId,
    string SubjectType,
    Guid SubjectId,
    DateTimeOffset? FromUtc,
    DateTimeOffset? ToUtc,
    DossierSectionSelection Sections,
    string Operator
)
{
    public string NormalizedSubjectType => DossierSubjectTypes.Normalize(SubjectType);
}

public sealed record DossierExportJobPayload(
    int SchemaVersion,
    Guid CaseId,
    string SubjectType,
    Guid SubjectId,
    DateTimeOffset? FromUtc,
    DateTimeOffset? ToUtc,
    DossierSectionSelection Sections,
    string OutputPath,
    string RequestedBy
)
{
    public string NormalizedSubjectType => DossierSubjectTypes.Normalize(SubjectType);
}

public sealed record DossierReportModel(
    Guid CaseId,
    string CaseName,
    string SubjectType,
    Guid SubjectId,
    string SubjectDisplayName,
    string SubjectCaption,
    DateTimeOffset GeneratedAtUtc,
    string Operator,
    DateTimeOffset? FromUtc,
    DateTimeOffset? ToUtc,
    DossierSectionSelection IncludedSections,
    DossierSubjectIdentifiersSection? SubjectIdentifiers,
    DossierWhereSeenSection? WhereSeenSummary,
    DossierTimelineSection? TimelineExcerpt,
    DossierNotableExcerptsSection? NotableExcerpts,
    IReadOnlyList<DossierCitation> AppendixCitations
);

public sealed record DossierSubjectIdentifiersSection(
    string Description,
    IReadOnlyList<DossierIdentifierEntry> Entries
);

public sealed record DossierIdentifierEntry(
    TargetIdentifierType Type,
    string ValueDisplay,
    string ValueNormalized,
    bool IsPrimary,
    string WhyLinked,
    IReadOnlyList<string> DecisionIds,
    IReadOnlyList<DossierCitation> Citations
);

public sealed record DossierWhereSeenSection(
    int TotalMessages,
    DateTimeOffset? FirstSeenUtc,
    DateTimeOffset? LastSeenUtc,
    IReadOnlyList<DossierCounterpartySummary> TopCounterparties,
    IReadOnlyList<DossierCitation> RepresentativeCitations,
    string Notes
);

public sealed record DossierCounterpartySummary(string Counterparty, int Count);

public sealed record DossierTimelineSection(
    int TotalAvailable,
    IReadOnlyList<DossierTimelineEntry> Entries,
    string Notes
);

public sealed record DossierTimelineEntry(
    Guid MessageEventId,
    DateTimeOffset? TimestampUtc,
    string Direction,
    string Participants,
    string Preview,
    IReadOnlyList<DossierCitation> Citations
);

public sealed record DossierNotableExcerptsSection(
    IReadOnlyList<DossierExcerptEntry> Entries,
    string Notes
);

public sealed record DossierExcerptEntry(
    Guid MessageEventId,
    DateTimeOffset? TimestampUtc,
    string Sender,
    string Recipients,
    string Excerpt,
    IReadOnlyList<DossierCitation> Citations
);

public sealed record DossierCitation(
    Guid EvidenceItemId,
    string SourceLocator,
    Guid? MessageEventId = null,
    string? EvidenceDisplayName = null
)
{
    public string CitationText => MessageEventId.HasValue
        ? $"{EvidenceItemId:D} | {SourceLocator} | {MessageEventId.Value:D}"
        : $"{EvidenceItemId:D} | {SourceLocator}";
}

public sealed record ReportExportResult(string OutputPath, string Format);
