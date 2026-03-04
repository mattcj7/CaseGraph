using CaseGraph.Core.Abstractions;
using CaseGraph.Core.Models;
using CaseGraph.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using System.Text.Json;

namespace CaseGraph.Infrastructure.Reports;

public sealed class DossierBuilder
{
    private const int TimelineEntryLimit = 50;
    private const int NotableExcerptLimit = 10;
    private const int RepresentativeCitationLimit = 25;
    private const int IdentifierCitationLimit = 3;

    private readonly IDbContextFactory<WorkspaceDbContext> _dbContextFactory;
    private readonly IWorkspaceDatabaseInitializer _databaseInitializer;
    private readonly IClock _clock;

    public DossierBuilder(
        IDbContextFactory<WorkspaceDbContext> dbContextFactory,
        IWorkspaceDatabaseInitializer databaseInitializer,
        IClock clock
    )
    {
        _dbContextFactory = dbContextFactory;
        _databaseInitializer = databaseInitializer;
        _clock = clock;
    }

    public async Task<DossierReportModel> BuildAsync(
        DossierBuildRequest request,
        CancellationToken ct
    )
    {
        if (request.CaseId == Guid.Empty)
        {
            throw new ArgumentException("CaseId is required.", nameof(request));
        }

        if (request.SubjectId == Guid.Empty)
        {
            throw new ArgumentException("SubjectId is required.", nameof(request));
        }

        if (!request.Sections.HasAnySelectedSection)
        {
            throw new ArgumentException("At least one dossier section must be selected.", nameof(request));
        }

        var subjectType = request.NormalizedSubjectType;
        var (fromUtc, toUtc) = NormalizeDateRange(request.FromUtc, request.ToUtc);

        await _databaseInitializer.EnsureInitializedAsync(ct);
        await using var db = await _dbContextFactory.CreateDbContextAsync(ct);

        var caseRecord = await db.Cases
            .AsNoTracking()
            .FirstOrDefaultAsync(record => record.CaseId == request.CaseId, ct);
        if (caseRecord is null)
        {
            throw new InvalidOperationException("Case not found.");
        }

        return subjectType == DossierSubjectTypes.Target
            ? await BuildTargetReportAsync(db, caseRecord, request, fromUtc, toUtc, ct)
            : await BuildGlobalPersonReportAsync(db, caseRecord, request, fromUtc, toUtc, ct);
    }

    private async Task<DossierReportModel> BuildTargetReportAsync(
        WorkspaceDbContext db,
        Persistence.Entities.CaseRecord caseRecord,
        DossierBuildRequest request,
        DateTimeOffset? fromUtc,
        DateTimeOffset? toUtc,
        CancellationToken ct
    )
    {
        var target = await db.Targets
            .AsNoTracking()
            .FirstOrDefaultAsync(
                record => record.CaseId == request.CaseId && record.TargetId == request.SubjectId,
                ct
            );
        if (target is null)
        {
            throw new InvalidOperationException("Target subject not found.");
        }

        var subjectIdentifiers = request.Sections.IncludeSubjectIdentifiers
            ? await BuildTargetIdentifierSectionAsync(db, request.CaseId, target.TargetId, ct)
            : null;

        var matchedMessages = request.Sections.IncludeWhereSeenSummary
            || request.Sections.IncludeTimelineExcerpt
            || request.Sections.IncludeNotableMessageExcerpts
            || request.Sections.IncludeAppendix
            ? await LoadMatchedMessagesAsync(
                db,
                request.CaseId,
                DossierSubjectTypes.Target,
                target.TargetId,
                fromUtc,
                toUtc,
                ct
            )
            : Array.Empty<MatchedMessage>();

        var whereSeen = request.Sections.IncludeWhereSeenSummary
            ? BuildWhereSeenSection(matchedMessages)
            : null;
        var timeline = request.Sections.IncludeTimelineExcerpt
            ? BuildTimelineSection(matchedMessages)
            : null;
        var notableExcerpts = request.Sections.IncludeNotableMessageExcerpts
            ? BuildNotableExcerptsSection(matchedMessages)
            : null;
        var appendix = request.Sections.IncludeAppendix
            ? BuildAppendixCitations(subjectIdentifiers, whereSeen, timeline, notableExcerpts)
            : Array.Empty<DossierCitation>();

        return new DossierReportModel(
            CaseId: caseRecord.CaseId,
            CaseName: caseRecord.Name,
            SubjectType: DossierSubjectTypes.Target,
            SubjectId: target.TargetId,
            SubjectDisplayName: target.DisplayName,
            SubjectCaption: "Target",
            GeneratedAtUtc: _clock.UtcNow.ToUniversalTime(),
            Operator: string.IsNullOrWhiteSpace(request.Operator)
                ? Environment.UserName
                : request.Operator.Trim(),
            FromUtc: fromUtc,
            ToUtc: toUtc,
            IncludedSections: request.Sections,
            SubjectIdentifiers: subjectIdentifiers,
            WhereSeenSummary: whereSeen,
            TimelineExcerpt: timeline,
            NotableExcerpts: notableExcerpts,
            AppendixCitations: appendix
        );
    }

    private async Task<DossierReportModel> BuildGlobalPersonReportAsync(
        WorkspaceDbContext db,
        Persistence.Entities.CaseRecord caseRecord,
        DossierBuildRequest request,
        DateTimeOffset? fromUtc,
        DateTimeOffset? toUtc,
        CancellationToken ct
    )
    {
        var person = await db.PersonEntities
            .AsNoTracking()
            .FirstOrDefaultAsync(
                record => record.GlobalEntityId == request.SubjectId,
                ct
            );
        if (person is null)
        {
            throw new InvalidOperationException("Global person subject not found.");
        }

        var caseTargets = await db.Targets
            .AsNoTracking()
            .Where(record => record.CaseId == request.CaseId && record.GlobalEntityId == request.SubjectId)
            .OrderBy(record => record.DisplayName)
            .ToListAsync(ct);
        if (caseTargets.Count == 0)
        {
            throw new InvalidOperationException("Global person is not linked to the current case.");
        }

        var subjectIdentifiers = request.Sections.IncludeSubjectIdentifiers
            ? await BuildGlobalPersonIdentifierSectionAsync(
                db,
                request.CaseId,
                request.SubjectId,
                caseTargets,
                ct
            )
            : null;

        var matchedMessages = request.Sections.IncludeWhereSeenSummary
            || request.Sections.IncludeTimelineExcerpt
            || request.Sections.IncludeNotableMessageExcerpts
            || request.Sections.IncludeAppendix
            ? await LoadMatchedMessagesAsync(
                db,
                request.CaseId,
                DossierSubjectTypes.GlobalPerson,
                request.SubjectId,
                fromUtc,
                toUtc,
                ct
            )
            : Array.Empty<MatchedMessage>();

        var whereSeen = request.Sections.IncludeWhereSeenSummary
            ? BuildWhereSeenSection(matchedMessages)
            : null;
        var timeline = request.Sections.IncludeTimelineExcerpt
            ? BuildTimelineSection(matchedMessages)
            : null;
        var notableExcerpts = request.Sections.IncludeNotableMessageExcerpts
            ? BuildNotableExcerptsSection(matchedMessages)
            : null;
        var appendix = request.Sections.IncludeAppendix
            ? BuildAppendixCitations(subjectIdentifiers, whereSeen, timeline, notableExcerpts)
            : Array.Empty<DossierCitation>();

        return new DossierReportModel(
            CaseId: caseRecord.CaseId,
            CaseName: caseRecord.Name,
            SubjectType: DossierSubjectTypes.GlobalPerson,
            SubjectId: person.GlobalEntityId,
            SubjectDisplayName: person.DisplayName,
            SubjectCaption: "Global Person",
            GeneratedAtUtc: _clock.UtcNow.ToUniversalTime(),
            Operator: string.IsNullOrWhiteSpace(request.Operator)
                ? Environment.UserName
                : request.Operator.Trim(),
            FromUtc: fromUtc,
            ToUtc: toUtc,
            IncludedSections: request.Sections,
            SubjectIdentifiers: subjectIdentifiers,
            WhereSeenSummary: whereSeen,
            TimelineExcerpt: timeline,
            NotableExcerpts: notableExcerpts,
            AppendixCitations: appendix
        );
    }

    private async Task<DossierSubjectIdentifiersSection> BuildTargetIdentifierSectionAsync(
        WorkspaceDbContext db,
        Guid caseId,
        Guid targetId,
        CancellationToken ct
    )
    {
        var identifierRows = await (
            from link in db.TargetIdentifierLinks.AsNoTracking()
            join identifier in db.Identifiers.AsNoTracking()
                on new { link.CaseId, link.IdentifierId } equals new { identifier.CaseId, identifier.IdentifierId }
            where link.CaseId == caseId && link.TargetId == targetId
            orderby link.IsPrimary ? 0 : 1, identifier.Type, identifier.ValueRaw
            select new TargetIdentifierRow(
                identifier.IdentifierId,
                targetId,
                identifier.Type,
                identifier.ValueRaw,
                identifier.ValueNormalized,
                link.IsPrimary,
                identifier.SourceEvidenceItemId,
                identifier.SourceLocator,
                link.SourceEvidenceItemId,
                link.SourceLocator
            )
        )
            .ToListAsync(ct);

        var decisionIdsByIdentifier = await LoadIdentifierDecisionIdsAsync(
            db,
            caseId,
            targetIds: new HashSet<Guid> { targetId },
            globalEntityId: null,
            identifierIds: identifierRows.Select(row => row.IdentifierId).ToHashSet(),
            ct
        );
        var presenceCitationsByIdentifier = await LoadPresenceCitationsByIdentifierAsync(
            db,
            caseId,
            targetIds: new HashSet<Guid> { targetId },
            identifierRows.Select(row => row.IdentifierId).ToHashSet(),
            ct
        );

        var entries = identifierRows
            .Select(row =>
            {
                var citations = CollectIdentifierCitations(
                    row.IdentifierSourceEvidenceItemId,
                    row.IdentifierSourceLocator,
                    row.LinkSourceEvidenceItemId,
                    row.LinkSourceLocator,
                    presenceCitationsByIdentifier.TryGetValue(row.IdentifierId, out var presenceCitations)
                        ? presenceCitations
                        : null
                );
                var decisionIds = decisionIdsByIdentifier.TryGetValue(row.IdentifierId, out var ids)
                    ? ids
                    : Array.Empty<string>();

                return new DossierIdentifierEntry(
                    Type: ParseIdentifierType(row.Type),
                    ValueDisplay: row.ValueRaw,
                    ValueNormalized: row.ValueNormalized,
                    IsPrimary: row.IsPrimary,
                    WhyLinked: BuildTargetIdentifierWhyLinked(row, decisionIds, citations.Count > 0),
                    DecisionIds: decisionIds,
                    Citations: citations
                );
            })
            .ToList();

        return new DossierSubjectIdentifiersSection(
            Description: entries.Count == 0
                ? "No identifiers are linked to this target."
                : "Identifiers linked to the selected target. Manual link decision ids are included when available.",
            Entries: entries
        );
    }

    private async Task<DossierSubjectIdentifiersSection> BuildGlobalPersonIdentifierSectionAsync(
        WorkspaceDbContext db,
        Guid caseId,
        Guid globalEntityId,
        IReadOnlyList<Persistence.Entities.TargetRecord> caseTargets,
        CancellationToken ct
    )
    {
        var targetIds = caseTargets.Select(target => target.TargetId).ToHashSet();
        var targetNameById = caseTargets.ToDictionary(target => target.TargetId, target => target.DisplayName);

        var identifierRows = await (
            from target in db.Targets.AsNoTracking()
            join link in db.TargetIdentifierLinks.AsNoTracking()
                on new { target.CaseId, target.TargetId } equals new { link.CaseId, link.TargetId }
            join identifier in db.Identifiers.AsNoTracking()
                on new { link.CaseId, link.IdentifierId } equals new { identifier.CaseId, identifier.IdentifierId }
            where target.CaseId == caseId
                  && target.GlobalEntityId == globalEntityId
            orderby identifier.Type, identifier.ValueNormalized, target.DisplayName
            select new TargetIdentifierRow(
                identifier.IdentifierId,
                target.TargetId,
                identifier.Type,
                identifier.ValueRaw,
                identifier.ValueNormalized,
                link.IsPrimary,
                identifier.SourceEvidenceItemId,
                identifier.SourceLocator,
                link.SourceEvidenceItemId,
                link.SourceLocator
            )
        )
            .ToListAsync(ct);

        var decisionIdsByIdentifier = await LoadIdentifierDecisionIdsAsync(
            db,
            caseId,
            targetIds,
            globalEntityId,
            identifierRows.Select(row => row.IdentifierId).ToHashSet(),
            ct
        );
        var presenceCitationsByIdentifier = await LoadPresenceCitationsByIdentifierAsync(
            db,
            caseId,
            targetIds,
            identifierRows.Select(row => row.IdentifierId).ToHashSet(),
            ct
        );

        var entries = identifierRows
            .GroupBy(row => (row.Type, row.ValueNormalized), StringTupleComparer.Instance)
            .Select(group =>
            {
                var first = group.First();
                var contributingTargets = group
                    .Select(row => targetNameById.TryGetValue(row.TargetId, out var name) ? name : row.TargetId.ToString("D"))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
                    .ToList();
                var citations = group
                    .SelectMany(row => CollectIdentifierCitations(
                        row.IdentifierSourceEvidenceItemId,
                        row.IdentifierSourceLocator,
                        row.LinkSourceEvidenceItemId,
                        row.LinkSourceLocator,
                        presenceCitationsByIdentifier.TryGetValue(row.IdentifierId, out var presenceCitations)
                            ? presenceCitations
                            : null
                    ))
                    .Distinct(DossierCitationComparer.Instance)
                    .Take(IdentifierCitationLimit)
                    .ToList();
                var decisionIds = group
                    .SelectMany(row => decisionIdsByIdentifier.TryGetValue(row.IdentifierId, out var ids)
                        ? ids
                        : Array.Empty<string>())
                    .Distinct(StringComparer.Ordinal)
                    .ToList();

                return new DossierIdentifierEntry(
                    Type: ParseIdentifierType(first.Type),
                    ValueDisplay: first.ValueRaw,
                    ValueNormalized: first.ValueNormalized,
                    IsPrimary: group.Any(row => row.IsPrimary),
                    WhyLinked: BuildGlobalPersonIdentifierWhyLinked(contributingTargets, decisionIds, citations.Count > 0),
                    DecisionIds: decisionIds,
                    Citations: citations
                );
            })
            .OrderBy(entry => entry.IsPrimary ? 0 : 1)
            .ThenBy(entry => entry.Type.ToString(), StringComparer.OrdinalIgnoreCase)
            .ThenBy(entry => entry.ValueDisplay, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return new DossierSubjectIdentifiersSection(
            Description: entries.Count == 0
                ? "No case-linked identifiers are available for this global person."
                : "Identifiers consolidated from targets in the current case that are linked to the selected global person.",
            Entries: entries
        );
    }

    private DossierWhereSeenSection BuildWhereSeenSection(IReadOnlyList<MatchedMessage> matchedMessages)
    {
        if (matchedMessages.Count == 0)
        {
            return new DossierWhereSeenSection(
                TotalMessages: 0,
                FirstSeenUtc: null,
                LastSeenUtc: null,
                TopCounterparties: Array.Empty<DossierCounterpartySummary>(),
                RepresentativeCitations: Array.Empty<DossierCitation>(),
                Notes: "No linked messages were found for the selected subject and date range."
            );
        }

        var timestamps = matchedMessages
            .Where(message => message.TimestampUtc.HasValue)
            .Select(message => message.TimestampUtc!.Value)
            .OrderBy(value => value)
            .ToList();
        var counterparties = matchedMessages
            .Select(message => ResolveCounterparty(message))
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .GroupBy(value => value!, StringComparer.OrdinalIgnoreCase)
            .Select(group => new DossierCounterpartySummary(group.Key, group.Count()))
            .OrderByDescending(item => item.Count)
            .ThenBy(item => item.Counterparty, StringComparer.OrdinalIgnoreCase)
            .Take(5)
            .ToList();

        return new DossierWhereSeenSection(
            TotalMessages: matchedMessages.Count,
            FirstSeenUtc: timestamps.Count == 0 ? null : timestamps[0],
            LastSeenUtc: timestamps.Count == 0 ? null : timestamps[^1],
            TopCounterparties: counterparties,
            RepresentativeCitations: matchedMessages
                .Take(RepresentativeCitationLimit)
                .Select(BuildCitation)
                .Distinct(DossierCitationComparer.Instance)
                .ToList(),
            Notes: $"Counts are based on {matchedMessages.Count:0} distinct linked message event(s) within the selected range."
        );
    }

    private DossierTimelineSection BuildTimelineSection(IReadOnlyList<MatchedMessage> matchedMessages)
    {
        var entries = matchedMessages
            .Take(TimelineEntryLimit)
            .Select(message => new DossierTimelineEntry(
                MessageEventId: message.MessageEventId,
                TimestampUtc: message.TimestampUtc,
                Direction: string.IsNullOrWhiteSpace(message.Direction) ? "Unknown" : message.Direction,
                Participants: BuildParticipants(message.Sender, message.Recipients),
                Preview: BuildPreview(message.Body),
                Citations: [BuildCitation(message)]
            ))
            .ToList();

        return new DossierTimelineSection(
            TotalAvailable: matchedMessages.Count,
            Entries: entries,
            Notes: entries.Count == 0
                ? "No timeline entries were found for the selected subject and date range."
                : $"Showing the {entries.Count:0} most recent linked message event(s) out of {matchedMessages.Count:0}."
        );
    }

    private DossierNotableExcerptsSection BuildNotableExcerptsSection(IReadOnlyList<MatchedMessage> matchedMessages)
    {
        var entries = matchedMessages
            .Where(message => !string.IsNullOrWhiteSpace(message.Body))
            .Take(NotableExcerptLimit)
            .Select(message => new DossierExcerptEntry(
                MessageEventId: message.MessageEventId,
                TimestampUtc: message.TimestampUtc,
                Sender: string.IsNullOrWhiteSpace(message.Sender) ? "(unknown sender)" : message.Sender!,
                Recipients: string.IsNullOrWhiteSpace(message.Recipients) ? "(unknown recipients)" : message.Recipients!,
                Excerpt: BuildExcerpt(message.Body),
                Citations: [BuildCitation(message)]
            ))
            .ToList();

        return new DossierNotableExcerptsSection(
            Entries: entries,
            Notes: entries.Count == 0
                ? "No message excerpts are available for the selected subject and date range."
                : $"Showing the {entries.Count:0} most recent bounded excerpt(s)."
        );
    }

    private static IReadOnlyList<DossierCitation> BuildAppendixCitations(
        DossierSubjectIdentifiersSection? identifiers,
        DossierWhereSeenSection? whereSeen,
        DossierTimelineSection? timeline,
        DossierNotableExcerptsSection? notableExcerpts
    )
    {
        var citations = new List<DossierCitation>();
        if (identifiers is not null)
        {
            citations.AddRange(identifiers.Entries.SelectMany(entry => entry.Citations));
        }

        if (whereSeen is not null)
        {
            citations.AddRange(whereSeen.RepresentativeCitations);
        }

        if (timeline is not null)
        {
            citations.AddRange(timeline.Entries.SelectMany(entry => entry.Citations));
        }

        if (notableExcerpts is not null)
        {
            citations.AddRange(notableExcerpts.Entries.SelectMany(entry => entry.Citations));
        }

        return citations
            .Distinct(DossierCitationComparer.Instance)
            .OrderBy(citation => citation.EvidenceDisplayName ?? string.Empty, StringComparer.OrdinalIgnoreCase)
            .ThenBy(citation => citation.SourceLocator, StringComparer.OrdinalIgnoreCase)
            .ThenBy(citation => citation.MessageEventId)
            .ToList();
    }

    private async Task<IReadOnlyList<MatchedMessage>> LoadMatchedMessagesAsync(
        WorkspaceDbContext db,
        Guid caseId,
        string subjectType,
        Guid subjectId,
        DateTimeOffset? fromUtc,
        DateTimeOffset? toUtc,
        CancellationToken ct
    )
    {
        var query =
            from presence in db.TargetMessagePresences.AsNoTracking()
            join target in db.Targets.AsNoTracking()
                on new { presence.CaseId, presence.TargetId } equals new { target.CaseId, target.TargetId }
            join message in db.MessageEvents.AsNoTracking()
                on presence.MessageEventId equals message.MessageEventId
            join evidence in db.EvidenceItems.AsNoTracking()
                on message.EvidenceItemId equals evidence.EvidenceItemId
            where presence.CaseId == caseId
            select new
            {
                presence.TargetId,
                target.GlobalEntityId,
                message.MessageEventId,
                message.EvidenceItemId,
                EvidenceDisplayName = evidence.DisplayName,
                message.SourceLocator,
                message.TimestampUtc,
                message.Direction,
                message.Sender,
                message.Recipients,
                message.Body,
                presence.Role
            };

        query = subjectType == DossierSubjectTypes.Target
            ? query.Where(row => row.TargetId == subjectId)
            : query.Where(row => row.GlobalEntityId == subjectId);

        var rows = await query
            .Select(row => new MatchedMessageRow(
                row.MessageEventId,
                row.EvidenceItemId,
                row.EvidenceDisplayName,
                row.SourceLocator,
                row.TimestampUtc,
                row.Direction,
                row.Sender,
                row.Recipients,
                row.Body,
                row.Role
            ))
            .ToListAsync(ct);

        var messages = rows
            .GroupBy(row => row.MessageEventId)
            .Select(group =>
            {
                var first = group.First();
                return new MatchedMessage(
                    first.MessageEventId,
                    first.EvidenceItemId,
                    first.EvidenceDisplayName,
                    first.SourceLocator,
                    first.TimestampUtc,
                    first.Direction,
                    first.Sender,
                    first.Recipients,
                    first.Body,
                    group.Select(item => item.Role)
                        .Where(role => !string.IsNullOrWhiteSpace(role))
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .ToList()
                );
            })
            .Where(message =>
                (!fromUtc.HasValue || (message.TimestampUtc.HasValue && message.TimestampUtc.Value >= fromUtc.Value))
                && (!toUtc.HasValue || (message.TimestampUtc.HasValue && message.TimestampUtc.Value <= toUtc.Value)))
            .OrderBy(message => message.TimestampUtc.HasValue ? 0 : 1)
            .ThenByDescending(message => message.TimestampUtc)
            .ThenByDescending(message => message.MessageEventId)
            .ToList();

        return messages;
    }

    private async Task<Dictionary<Guid, IReadOnlyList<string>>> LoadIdentifierDecisionIdsAsync(
        WorkspaceDbContext db,
        Guid caseId,
        IReadOnlySet<Guid> targetIds,
        Guid? globalEntityId,
        IReadOnlySet<Guid> identifierIds,
        CancellationToken ct
    )
    {
        if (identifierIds.Count == 0)
        {
            return new Dictionary<Guid, IReadOnlyList<string>>();
        }

        var actions = (await db.AuditEvents
            .AsNoTracking()
            .Where(record => record.CaseId == caseId)
            .Where(record =>
                record.ActionType == "IdentifierLinkedToTarget"
                || record.ActionType == "LinkIdentifierToTarget"
                || record.ActionType == "TargetLinkedToGlobalPerson")
            .ToListAsync(ct))
            .OrderBy(record => record.TimestampUtc)
            .ToList();

        var decisionIds = new Dictionary<Guid, HashSet<string>>();
        foreach (var action in actions)
        {
            if (string.IsNullOrWhiteSpace(action.JsonPayload))
            {
                continue;
            }

            try
            {
                using var document = JsonDocument.Parse(action.JsonPayload);
                var root = document.RootElement;
                if (action.ActionType == "TargetLinkedToGlobalPerson"
                    && globalEntityId.HasValue
                    && TryReadGuid(root, "GlobalEntityId", out var payloadGlobalEntityId)
                    && payloadGlobalEntityId == globalEntityId.Value
                    && TryReadGuid(root, "TargetId", out var globalTargetId)
                    && targetIds.Contains(globalTargetId))
                {
                    foreach (var identifierId in identifierIds)
                    {
                        AddDecisionId(decisionIds, identifierId, action.AuditEventId);
                    }
                }

                if (!TryReadGuid(root, "IdentifierId", out var identifierIdValue)
                    || !identifierIds.Contains(identifierIdValue))
                {
                    continue;
                }

                if (TryReadGuid(root, "TargetId", out var payloadTargetId)
                    && targetIds.Contains(payloadTargetId))
                {
                    AddDecisionId(decisionIds, identifierIdValue, action.AuditEventId);
                }
            }
            catch (JsonException)
            {
                // Audit payload parsing failures must not block report generation.
            }
        }

        return decisionIds.ToDictionary(
            pair => pair.Key,
            pair => (IReadOnlyList<string>)pair.Value.OrderBy(value => value, StringComparer.Ordinal).ToList()
        );
    }

    private static void AddDecisionId(
        Dictionary<Guid, HashSet<string>> decisionIds,
        Guid identifierId,
        Guid auditEventId
    )
    {
        if (!decisionIds.TryGetValue(identifierId, out var ids))
        {
            ids = new HashSet<string>(StringComparer.Ordinal);
            decisionIds[identifierId] = ids;
        }

        ids.Add(auditEventId.ToString("D"));
    }

    private async Task<Dictionary<Guid, IReadOnlyList<DossierCitation>>> LoadPresenceCitationsByIdentifierAsync(
        WorkspaceDbContext db,
        Guid caseId,
        IReadOnlySet<Guid> targetIds,
        IReadOnlySet<Guid> identifierIds,
        CancellationToken ct
    )
    {
        if (targetIds.Count == 0 || identifierIds.Count == 0)
        {
            return new Dictionary<Guid, IReadOnlyList<DossierCitation>>();
        }

        var rows = (await (
            from presence in db.TargetMessagePresences.AsNoTracking()
            join message in db.MessageEvents.AsNoTracking()
                on presence.MessageEventId equals message.MessageEventId
            join evidence in db.EvidenceItems.AsNoTracking()
                on message.EvidenceItemId equals evidence.EvidenceItemId
            where presence.CaseId == caseId
                  && targetIds.Contains(presence.TargetId)
                  && identifierIds.Contains(presence.MatchedIdentifierId)
            select new
            {
                presence.MatchedIdentifierId,
                presence.MessageTimestampUtc,
                presence.MessageEventId,
                Citation = new DossierCitation(
                    message.EvidenceItemId,
                    message.SourceLocator,
                    message.MessageEventId,
                    evidence.DisplayName
                )
            }
        )
            .ToListAsync(ct))
            .OrderByDescending(row => row.MessageTimestampUtc)
            .ThenByDescending(row => row.MessageEventId)
            .ToList();

        return rows
            .GroupBy(row => row.MatchedIdentifierId)
            .ToDictionary(
                group => group.Key,
                group => (IReadOnlyList<DossierCitation>)group
                    .Select(row => row.Citation)
                    .Distinct(DossierCitationComparer.Instance)
                    .Take(IdentifierCitationLimit)
                    .ToList()
            );
    }

    private static List<DossierCitation> CollectIdentifierCitations(
        Guid? identifierSourceEvidenceItemId,
        string identifierSourceLocator,
        Guid? linkSourceEvidenceItemId,
        string linkSourceLocator,
        IReadOnlyList<DossierCitation>? presenceCitations
    )
    {
        var citations = new List<DossierCitation>();
        if (identifierSourceEvidenceItemId.HasValue
            && !string.IsNullOrWhiteSpace(identifierSourceLocator))
        {
            citations.Add(new DossierCitation(
                identifierSourceEvidenceItemId.Value,
                identifierSourceLocator
            ));
        }

        if (linkSourceEvidenceItemId.HasValue
            && !string.IsNullOrWhiteSpace(linkSourceLocator))
        {
            citations.Add(new DossierCitation(
                linkSourceEvidenceItemId.Value,
                linkSourceLocator
            ));
        }

        if (presenceCitations is not null)
        {
            citations.AddRange(presenceCitations);
        }

        return citations
            .Distinct(DossierCitationComparer.Instance)
            .Take(IdentifierCitationLimit)
            .ToList();
    }

    private static string BuildTargetIdentifierWhyLinked(
        TargetIdentifierRow row,
        IReadOnlyList<string> decisionIds,
        bool hasCitations
    )
    {
        if (decisionIds.Count > 0)
        {
            return $"Manual link decision id(s): {string.Join(", ", decisionIds)}.";
        }

        if (hasCitations)
        {
            if (!string.IsNullOrWhiteSpace(row.LinkSourceLocator))
            {
                return $"Linked via source locator {row.LinkSourceLocator}.";
            }

            if (!string.IsNullOrWhiteSpace(row.IdentifierSourceLocator))
            {
                return $"Derived from identifier source locator {row.IdentifierSourceLocator}.";
            }

            return "Linked from evidence-backed message presence.";
        }

        return "Analyst-entered identifier with no linked message citation available.";
    }

    private static string BuildGlobalPersonIdentifierWhyLinked(
        IReadOnlyList<string> contributingTargets,
        IReadOnlyList<string> decisionIds,
        bool hasCitations
    )
    {
        var targetSummary = contributingTargets.Count == 0
            ? "case-linked targets"
            : string.Join(", ", contributingTargets);

        if (decisionIds.Count > 0)
        {
            return $"Observed on linked target(s): {targetSummary}. Global-person link decision id(s): {string.Join(", ", decisionIds)}.";
        }

        if (hasCitations)
        {
            return $"Observed on linked target(s): {targetSummary}.";
        }

        return $"Consolidated from linked target(s): {targetSummary}.";
    }

    private static string ResolveCounterparty(MatchedMessage message)
    {
        var sender = string.IsNullOrWhiteSpace(message.Sender) ? "(unknown sender)" : message.Sender!;
        var recipients = string.IsNullOrWhiteSpace(message.Recipients) ? "(unknown recipients)" : message.Recipients!;
        var roleSet = message.Roles;
        var isSender = roleSet.Any(role => string.Equals(role, "Sender", StringComparison.OrdinalIgnoreCase));
        var isRecipient = roleSet.Any(role => string.Equals(role, "Recipient", StringComparison.OrdinalIgnoreCase));

        if (isSender && !isRecipient)
        {
            return recipients;
        }

        if (isRecipient && !isSender)
        {
            return sender;
        }

        return $"{sender} | {recipients}";
    }

    private static string BuildParticipants(string? sender, string? recipients)
    {
        var left = string.IsNullOrWhiteSpace(sender) ? "(unknown sender)" : sender.Trim();
        var right = string.IsNullOrWhiteSpace(recipients) ? "(unknown recipients)" : recipients.Trim();
        return $"{left} -> {right}";
    }

    private static string BuildPreview(string? body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return "(no message body)";
        }

        var normalized = NormalizeWhitespace(body);
        return normalized.Length <= 240
            ? normalized
            : normalized[..237] + "...";
    }

    private static string BuildExcerpt(string? body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return "(no message body)";
        }

        var normalized = NormalizeWhitespace(body);
        return normalized.Length <= 360
            ? normalized
            : normalized[..357] + "...";
    }

    private static string NormalizeWhitespace(string value)
    {
        return string.Join(
            " ",
            value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
        );
    }

    private static DossierCitation BuildCitation(MatchedMessage message)
    {
        return new DossierCitation(
            message.EvidenceItemId,
            message.SourceLocator,
            message.MessageEventId,
            message.EvidenceDisplayName
        );
    }

    private static (DateTimeOffset? FromUtc, DateTimeOffset? ToUtc) NormalizeDateRange(
        DateTimeOffset? fromUtc,
        DateTimeOffset? toUtc
    )
    {
        var normalizedFrom = fromUtc?.ToUniversalTime();
        var normalizedTo = toUtc?.ToUniversalTime();
        if (normalizedFrom.HasValue
            && normalizedTo.HasValue
            && normalizedFrom.Value > normalizedTo.Value)
        {
            (normalizedFrom, normalizedTo) = (normalizedTo, normalizedFrom);
        }

        return (normalizedFrom, normalizedTo);
    }

    private static TargetIdentifierType ParseIdentifierType(string? value)
    {
        return Enum.TryParse<TargetIdentifierType>(value, ignoreCase: true, out var parsed)
            ? parsed
            : TargetIdentifierType.Other;
    }

    private static bool TryReadGuid(JsonElement root, string propertyName, out Guid value)
    {
        value = Guid.Empty;
        if (!root.TryGetProperty(propertyName, out var property))
        {
            return false;
        }

        return property.ValueKind switch
        {
            JsonValueKind.String => Guid.TryParse(property.GetString(), out value),
            _ => Guid.TryParse(property.ToString(), out value)
        };
    }

    private sealed record TargetIdentifierRow(
        Guid IdentifierId,
        Guid TargetId,
        string Type,
        string ValueRaw,
        string ValueNormalized,
        bool IsPrimary,
        Guid? IdentifierSourceEvidenceItemId,
        string IdentifierSourceLocator,
        Guid? LinkSourceEvidenceItemId,
        string LinkSourceLocator
    );

    private sealed record MatchedMessageRow(
        Guid MessageEventId,
        Guid EvidenceItemId,
        string EvidenceDisplayName,
        string SourceLocator,
        DateTimeOffset? TimestampUtc,
        string Direction,
        string? Sender,
        string? Recipients,
        string? Body,
        string Role
    );

    private sealed record MatchedMessage(
        Guid MessageEventId,
        Guid EvidenceItemId,
        string EvidenceDisplayName,
        string SourceLocator,
        DateTimeOffset? TimestampUtc,
        string Direction,
        string? Sender,
        string? Recipients,
        string? Body,
        IReadOnlyList<string> Roles
    );

    private sealed class DossierCitationComparer : IEqualityComparer<DossierCitation>
    {
        public static DossierCitationComparer Instance { get; } = new();

        public bool Equals(DossierCitation? x, DossierCitation? y)
        {
            if (ReferenceEquals(x, y))
            {
                return true;
            }

            if (x is null || y is null)
            {
                return false;
            }

            return x.EvidenceItemId == y.EvidenceItemId
                && x.MessageEventId == y.MessageEventId
                && string.Equals(x.SourceLocator, y.SourceLocator, StringComparison.Ordinal);
        }

        public int GetHashCode(DossierCitation obj)
        {
            return HashCode.Combine(
                obj.EvidenceItemId,
                obj.MessageEventId,
                obj.SourceLocator
            );
        }
    }

    private sealed class StringTupleComparer : IEqualityComparer<(string Type, string ValueNormalized)>
    {
        public static StringTupleComparer Instance { get; } = new();

        public bool Equals((string Type, string ValueNormalized) x, (string Type, string ValueNormalized) y)
        {
            return string.Equals(x.Type, y.Type, StringComparison.OrdinalIgnoreCase)
                && string.Equals(x.ValueNormalized, y.ValueNormalized, StringComparison.OrdinalIgnoreCase);
        }

        public int GetHashCode((string Type, string ValueNormalized) obj)
        {
            return HashCode.Combine(
                obj.Type.ToUpperInvariant(),
                obj.ValueNormalized.ToUpperInvariant()
            );
        }
    }
}
