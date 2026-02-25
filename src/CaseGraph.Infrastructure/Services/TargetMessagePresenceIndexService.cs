using CaseGraph.Core.Abstractions;
using CaseGraph.Core.Diagnostics;
using CaseGraph.Core.Models;
using CaseGraph.Infrastructure.Persistence;
using CaseGraph.Infrastructure.Persistence.Entities;
using Microsoft.EntityFrameworkCore;

namespace CaseGraph.Infrastructure.Services;

public sealed class TargetMessagePresenceIndexService : ITargetMessagePresenceIndexService
{
    private static readonly char[] ParticipantSeparators = [',', ';', '|', '\n', '\r'];

    private readonly IDbContextFactory<WorkspaceDbContext> _dbContextFactory;
    private readonly IWorkspaceDatabaseInitializer _databaseInitializer;
    private readonly IWorkspaceWriteGate _workspaceWriteGate;
    private readonly IClock _clock;

    public TargetMessagePresenceIndexService(
        IDbContextFactory<WorkspaceDbContext> dbContextFactory,
        IWorkspaceDatabaseInitializer databaseInitializer,
        IWorkspaceWriteGate workspaceWriteGate,
        IClock clock
    )
    {
        _dbContextFactory = dbContextFactory;
        _databaseInitializer = databaseInitializer;
        _workspaceWriteGate = workspaceWriteGate;
        _clock = clock;
    }

    public Task RebuildCaseAsync(Guid caseId, CancellationToken ct)
    {
        if (caseId == Guid.Empty)
        {
            throw new ArgumentException("CaseId is required.", nameof(caseId));
        }

        return RefreshInternalAsync(
            caseId,
            evidenceItemId: null,
            identifierId: null,
            operationName: "TargetPresenceIndex.RebuildCase",
            ct
        );
    }

    public Task RefreshForEvidenceAsync(Guid caseId, Guid evidenceItemId, CancellationToken ct)
    {
        if (caseId == Guid.Empty)
        {
            throw new ArgumentException("CaseId is required.", nameof(caseId));
        }

        if (evidenceItemId == Guid.Empty)
        {
            throw new ArgumentException("EvidenceItemId is required.", nameof(evidenceItemId));
        }

        return RefreshInternalAsync(
            caseId,
            evidenceItemId,
            identifierId: null,
            operationName: "TargetPresenceIndex.RefreshEvidence",
            ct
        );
    }

    public Task RefreshForIdentifierAsync(Guid caseId, Guid identifierId, CancellationToken ct)
    {
        if (caseId == Guid.Empty)
        {
            throw new ArgumentException("CaseId is required.", nameof(caseId));
        }

        if (identifierId == Guid.Empty)
        {
            throw new ArgumentException("IdentifierId is required.", nameof(identifierId));
        }

        return RefreshInternalAsync(
            caseId,
            evidenceItemId: null,
            identifierId,
            operationName: "TargetPresenceIndex.RefreshIdentifier",
            ct
        );
    }

    private async Task RefreshInternalAsync(
        Guid caseId,
        Guid? evidenceItemId,
        Guid? identifierId,
        string operationName,
        CancellationToken ct
    )
    {
        await _workspaceWriteGate.ExecuteWriteAsync(
            operationName,
            async writeCt =>
            {
                await _databaseInitializer.EnsureInitializedAsync(writeCt);
                await using var db = await _dbContextFactory.CreateDbContextAsync(writeCt);
                var now = _clock.UtcNow.ToUniversalTime();

                IQueryable<TargetMessagePresenceRecord> deleteScope = db.TargetMessagePresences
                    .Where(row => row.CaseId == caseId);
                if (evidenceItemId.HasValue)
                {
                    deleteScope = deleteScope.Where(row => row.EvidenceItemId == evidenceItemId.Value);
                }

                if (identifierId.HasValue)
                {
                    deleteScope = deleteScope.Where(row => row.MatchedIdentifierId == identifierId.Value);
                }

                var existingRows = await deleteScope.ToListAsync(writeCt);
                if (existingRows.Count > 0)
                {
                    db.TargetMessagePresences.RemoveRange(existingRows);
                }

                var linksQuery = db.TargetIdentifierLinks
                    .AsNoTracking()
                    .Include(link => link.Identifier)
                    .Where(link => link.CaseId == caseId);
                if (identifierId.HasValue)
                {
                    linksQuery = linksQuery.Where(link => link.IdentifierId == identifierId.Value);
                }

                var linkedIdentifiers = await linksQuery
                    .Where(link => link.Identifier != null)
                    .Select(link => new LinkedIdentifier(
                        link.TargetId,
                        link.IdentifierId,
                        ParseIdentifierType(link.Identifier!.Type),
                        link.Identifier!.ValueNormalized
                    ))
                    .ToListAsync(writeCt);

                if (linkedIdentifiers.Count == 0)
                {
                    await db.SaveChangesAsync(writeCt);
                    return;
                }

                var messagesQuery = db.MessageEvents
                    .AsNoTracking()
                    .Where(message => message.CaseId == caseId);
                if (evidenceItemId.HasValue)
                {
                    messagesQuery = messagesQuery.Where(message => message.EvidenceItemId == evidenceItemId.Value);
                }

                var messages = await messagesQuery
                    .Select(message => new MessagePresenceSource(
                        message.MessageEventId,
                        message.EvidenceItemId,
                        message.TimestampUtc,
                        message.SourceLocator,
                        message.Sender,
                        message.Recipients
                    ))
                    .ToListAsync(writeCt);

                if (messages.Count == 0)
                {
                    await db.SaveChangesAsync(writeCt);
                    return;
                }

                var presenceRows = BuildPresenceRows(caseId, linkedIdentifiers, messages, now);
                if (presenceRows.Count > 0)
                {
                    db.TargetMessagePresences.AddRange(presenceRows);
                }

                await db.SaveChangesAsync(writeCt);
            },
            ct,
            correlationId: AppFileLogger.GetScopeValue("correlationId"),
            fields: new Dictionary<string, object?>
            {
                ["caseId"] = caseId.ToString("D"),
                ["evidenceItemId"] = evidenceItemId?.ToString("D"),
                ["identifierId"] = identifierId?.ToString("D")
            }
        );
    }

    private static List<TargetMessagePresenceRecord> BuildPresenceRows(
        Guid caseId,
        IReadOnlyList<LinkedIdentifier> linkedIdentifiers,
        IReadOnlyList<MessagePresenceSource> messages,
        DateTimeOffset indexedAtUtc
    )
    {
        var rows = new List<TargetMessagePresenceRecord>();
        foreach (var link in linkedIdentifiers)
        {
            if (string.IsNullOrWhiteSpace(link.ValueNormalized))
            {
                continue;
            }

            foreach (var message in messages)
            {
                var senderMatch = MatchesIdentifier(link.Type, link.ValueNormalized, message.Sender);
                var recipientMatch = MatchesAnyIdentifierToken(link.Type, link.ValueNormalized, message.Recipients);

                if (!senderMatch && !recipientMatch)
                {
                    continue;
                }

                if (senderMatch)
                {
                    rows.Add(new TargetMessagePresenceRecord
                    {
                        PresenceId = Guid.NewGuid(),
                        CaseId = caseId,
                        TargetId = link.TargetId,
                        MessageEventId = message.MessageEventId,
                        MatchedIdentifierId = link.IdentifierId,
                        Role = "Sender",
                        EvidenceItemId = message.EvidenceItemId,
                        SourceLocator = message.SourceLocator,
                        MessageTimestampUtc = message.TimestampUtc,
                        FirstSeenUtc = indexedAtUtc,
                        LastSeenUtc = indexedAtUtc
                    });
                }

                if (recipientMatch)
                {
                    rows.Add(new TargetMessagePresenceRecord
                    {
                        PresenceId = Guid.NewGuid(),
                        CaseId = caseId,
                        TargetId = link.TargetId,
                        MessageEventId = message.MessageEventId,
                        MatchedIdentifierId = link.IdentifierId,
                        Role = "Recipient",
                        EvidenceItemId = message.EvidenceItemId,
                        SourceLocator = message.SourceLocator,
                        MessageTimestampUtc = message.TimestampUtc,
                        FirstSeenUtc = indexedAtUtc,
                        LastSeenUtc = indexedAtUtc
                    });
                }
            }
        }

        return rows;
    }

    private static bool MatchesAnyIdentifierToken(
        TargetIdentifierType type,
        string expectedNormalized,
        string? participantsRaw
    )
    {
        foreach (var token in SplitParticipantTokens(participantsRaw))
        {
            if (MatchesIdentifier(type, expectedNormalized, token))
            {
                return true;
            }
        }

        return false;
    }

    private static IEnumerable<string> SplitParticipantTokens(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return Array.Empty<string>();
        }

        var tokens = raw.Split(
                ParticipantSeparators,
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries
            )
            .Where(token => token.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (tokens.Count > 0)
        {
            return tokens;
        }

        return [raw.Trim()];
    }

    private static bool MatchesIdentifier(
        TargetIdentifierType type,
        string expectedNormalized,
        string? participantRaw
    )
    {
        if (string.IsNullOrWhiteSpace(participantRaw))
        {
            return false;
        }

        var normalized = IdentifierNormalizer.Normalize(type, participantRaw);
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return false;
        }

        return string.Equals(normalized, expectedNormalized, StringComparison.OrdinalIgnoreCase);
    }

    private static TargetIdentifierType ParseIdentifierType(string? typeText)
    {
        return Enum.TryParse<TargetIdentifierType>(typeText, ignoreCase: true, out var parsed)
            ? parsed
            : TargetIdentifierType.Other;
    }

    private sealed record LinkedIdentifier(
        Guid TargetId,
        Guid IdentifierId,
        TargetIdentifierType Type,
        string ValueNormalized
    );

    private sealed record MessagePresenceSource(
        Guid MessageEventId,
        Guid EvidenceItemId,
        DateTimeOffset? TimestampUtc,
        string SourceLocator,
        string? Sender,
        string? Recipients
    );
}
