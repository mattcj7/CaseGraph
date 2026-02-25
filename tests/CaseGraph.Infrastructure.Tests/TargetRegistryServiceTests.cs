using CaseGraph.Core.Abstractions;
using CaseGraph.Core.Models;
using CaseGraph.Infrastructure.Persistence;
using CaseGraph.Infrastructure.Persistence.Entities;
using CaseGraph.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace CaseGraph.Infrastructure.Tests;

public sealed class TargetRegistryServiceTests
{
    [Theory]
    [InlineData("   ")]
    [InlineData("()")]
    public async Task AddIdentifierAsync_EmptyOrNormalizedEmpty_ThrowsArgumentException(string valueRaw)
    {
        await using var fixture = await WorkspaceFixture.CreateAsync();
        var registry = fixture.Services.GetRequiredService<ITargetRegistryService>();

        var caseInfo = await fixture.CreateCaseAsync("Identifier Validation Case");
        var target = await registry.CreateTargetAsync(
            new CreateTargetRequest(caseInfo.CaseId, "Alpha", null, null),
            CancellationToken.None
        );

        var ex = await Assert.ThrowsAsync<ArgumentException>(() => registry.AddIdentifierAsync(
            new AddTargetIdentifierRequest(
                caseInfo.CaseId,
                target.TargetId,
                TargetIdentifierType.Phone,
                valueRaw,
                null,
                IsPrimary: true
            ),
            CancellationToken.None
        ));

        Assert.Equal("valueRaw", ex.ParamName);
        Assert.StartsWith("Identifier value is required.", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AddIdentifierAsync_DefaultConflictResolution_ThrowsConflict()
    {
        await using var fixture = await WorkspaceFixture.CreateAsync();
        var registry = fixture.Services.GetRequiredService<ITargetRegistryService>();

        var caseInfo = await fixture.CreateCaseAsync("Target Conflict Case");
        var alpha = await registry.CreateTargetAsync(
            new CreateTargetRequest(caseInfo.CaseId, "Alpha", null, null),
            CancellationToken.None
        );
        var bravo = await registry.CreateTargetAsync(
            new CreateTargetRequest(caseInfo.CaseId, "Bravo", null, null),
            CancellationToken.None
        );

        await registry.AddIdentifierAsync(
            new AddTargetIdentifierRequest(
                caseInfo.CaseId,
                alpha.TargetId,
                TargetIdentifierType.Phone,
                "(555) 123-0001",
                null,
                IsPrimary: true
            ),
            CancellationToken.None
        );

        var ex = await Assert.ThrowsAsync<IdentifierConflictException>(() => registry.AddIdentifierAsync(
            new AddTargetIdentifierRequest(
                caseInfo.CaseId,
                bravo.TargetId,
                TargetIdentifierType.Phone,
                "+1 (555) 123-0001",
                null,
                IsPrimary: false
            ),
            CancellationToken.None
        ));

        Assert.Equal(alpha.TargetId, ex.Conflict.ExistingTargetId);
        Assert.Equal("+15551230001", ex.Conflict.ValueNormalized);
    }

    [Fact]
    public async Task AddIdentifierAsync_MoveConflictResolution_MovesIdentifierToRequestedTarget()
    {
        await using var fixture = await WorkspaceFixture.CreateAsync();
        var registry = fixture.Services.GetRequiredService<ITargetRegistryService>();

        var caseInfo = await fixture.CreateCaseAsync("Target Move Case");
        var alpha = await registry.CreateTargetAsync(
            new CreateTargetRequest(caseInfo.CaseId, "Alpha", null, null),
            CancellationToken.None
        );
        var bravo = await registry.CreateTargetAsync(
            new CreateTargetRequest(caseInfo.CaseId, "Bravo", null, null),
            CancellationToken.None
        );

        var initial = await registry.AddIdentifierAsync(
            new AddTargetIdentifierRequest(
                caseInfo.CaseId,
                alpha.TargetId,
                TargetIdentifierType.Phone,
                "(555) 123-0001",
                null,
                IsPrimary: true
            ),
            CancellationToken.None
        );

        var moved = await registry.AddIdentifierAsync(
            new AddTargetIdentifierRequest(
                caseInfo.CaseId,
                bravo.TargetId,
                TargetIdentifierType.Phone,
                "5551230001",
                null,
                IsPrimary: true,
                ConflictResolution: IdentifierConflictResolution.MoveIdentifierToRequestedTarget
            ),
            CancellationToken.None
        );

        Assert.Equal(bravo.TargetId, moved.EffectiveTargetId);
        Assert.Equal(initial.Identifier.IdentifierId, moved.Identifier.IdentifierId);
        Assert.True(moved.MovedIdentifier);
        Assert.False(moved.UsedExistingTarget);

        await using var db = await fixture.CreateDbContextAsync();
        var links = await db.TargetIdentifierLinks
            .AsNoTracking()
            .Where(link => link.CaseId == caseInfo.CaseId && link.IdentifierId == moved.Identifier.IdentifierId)
            .ToListAsync();
        Assert.Single(links);
        Assert.Equal(bravo.TargetId, links[0].TargetId);

        var actionTypes = await db.AuditEvents
            .AsNoTracking()
            .Where(audit => audit.CaseId == caseInfo.CaseId)
            .Select(audit => audit.ActionType)
            .ToListAsync();
        Assert.Contains("IdentifierUnlinkedFromTarget", actionTypes);
        Assert.Contains("IdentifierLinkedToTarget", actionTypes);
    }

    [Fact]
    public async Task AddIdentifierAsync_UseExistingConflictResolution_KeepsExistingTarget()
    {
        await using var fixture = await WorkspaceFixture.CreateAsync();
        var registry = fixture.Services.GetRequiredService<ITargetRegistryService>();

        var caseInfo = await fixture.CreateCaseAsync("Target UseExisting Case");
        var alpha = await registry.CreateTargetAsync(
            new CreateTargetRequest(caseInfo.CaseId, "Alpha", null, null),
            CancellationToken.None
        );
        var bravo = await registry.CreateTargetAsync(
            new CreateTargetRequest(caseInfo.CaseId, "Bravo", null, null),
            CancellationToken.None
        );

        var initial = await registry.AddIdentifierAsync(
            new AddTargetIdentifierRequest(
                caseInfo.CaseId,
                alpha.TargetId,
                TargetIdentifierType.Phone,
                "(555) 123-0001",
                null,
                IsPrimary: true
            ),
            CancellationToken.None
        );

        var resolved = await registry.AddIdentifierAsync(
            new AddTargetIdentifierRequest(
                caseInfo.CaseId,
                bravo.TargetId,
                TargetIdentifierType.Phone,
                "+15551230001",
                null,
                IsPrimary: false,
                ConflictResolution: IdentifierConflictResolution.UseExistingTarget
            ),
            CancellationToken.None
        );

        Assert.Equal(alpha.TargetId, resolved.EffectiveTargetId);
        Assert.Equal(initial.Identifier.IdentifierId, resolved.Identifier.IdentifierId);
        Assert.True(resolved.UsedExistingTarget);
        Assert.False(resolved.MovedIdentifier);

        await using var db = await fixture.CreateDbContextAsync();
        var links = await db.TargetIdentifierLinks
            .AsNoTracking()
            .Where(link => link.CaseId == caseInfo.CaseId && link.IdentifierId == resolved.Identifier.IdentifierId)
            .ToListAsync();
        Assert.Single(links);
        Assert.Equal(alpha.TargetId, links[0].TargetId);
    }

    [Fact]
    public async Task LinkMessageParticipantAsync_WritesDerivedProvenanceAndParticipantAudit()
    {
        await using var fixture = await WorkspaceFixture.CreateAsync();
        var registry = fixture.Services.GetRequiredService<ITargetRegistryService>();

        var caseInfo = await fixture.CreateCaseAsync("Participant Link Case");
        var target = await registry.CreateTargetAsync(
            new CreateTargetRequest(caseInfo.CaseId, "Known Person", null, null),
            CancellationToken.None
        );

        var seeded = await fixture.SeedMessageEventAsync(caseInfo.CaseId, "+15551230001", "+15554445555");
        var linkResult = await registry.LinkMessageParticipantAsync(
            new LinkMessageParticipantRequest(
                caseInfo.CaseId,
                seeded.MessageEventId,
                MessageParticipantRole.Sender,
                "+1 (555) 123-0001",
                TargetIdentifierType.Phone,
                target.TargetId,
                null
            ),
            CancellationToken.None
        );

        await using var db = await fixture.CreateDbContextAsync();
        var participantLink = await db.MessageParticipantLinks
            .AsNoTracking()
            .FirstAsync(link => link.ParticipantLinkId == linkResult.ParticipantLinkId);

        Assert.Equal("Derived", participantLink.SourceType);
        Assert.Equal(seeded.EvidenceItemId, participantLink.SourceEvidenceItemId);
        Assert.Equal("UI-Link-v1", participantLink.IngestModuleVersion);
        Assert.Contains(";role=Sender", participantLink.SourceLocator, StringComparison.Ordinal);
        Assert.Equal(target.TargetId, participantLink.TargetId);

        var identifier = await db.Identifiers
            .AsNoTracking()
            .FirstAsync(item => item.IdentifierId == linkResult.IdentifierId);
        Assert.Equal("Derived", identifier.SourceType);
        Assert.Equal(seeded.EvidenceItemId, identifier.SourceEvidenceItemId);
        Assert.Equal("UI-Link-v1", identifier.IngestModuleVersion);

        var actionTypes = await db.AuditEvents
            .AsNoTracking()
            .Where(audit => audit.CaseId == caseInfo.CaseId)
            .Select(audit => audit.ActionType)
            .ToListAsync();
        Assert.Contains("LinkIdentifierToTarget", actionTypes);
        Assert.Contains("ParticipantLinked", actionTypes);
    }

    [Fact]
    public async Task LinkMessageParticipantAsync_CreateTarget_WritesCreateAudit()
    {
        await using var fixture = await WorkspaceFixture.CreateAsync();
        var registry = fixture.Services.GetRequiredService<ITargetRegistryService>();

        var caseInfo = await fixture.CreateCaseAsync("Participant Create Case");
        var seeded = await fixture.SeedMessageEventAsync(caseInfo.CaseId, "+15551230099", "+15554445555");
        var result = await registry.LinkMessageParticipantAsync(
            new LinkMessageParticipantRequest(
                caseInfo.CaseId,
                seeded.MessageEventId,
                MessageParticipantRole.Sender,
                "+1 (555) 123-0099",
                TargetIdentifierType.Phone,
                TargetId: null,
                NewTargetDisplayName: "Created from sender"
            ),
            CancellationToken.None
        );

        Assert.True(result.CreatedTarget);

        await using var db = await fixture.CreateDbContextAsync();
        var actionTypes = await db.AuditEvents
            .AsNoTracking()
            .Where(audit => audit.CaseId == caseInfo.CaseId)
            .Select(audit => audit.ActionType)
            .ToListAsync();
        Assert.Contains("CreateTargetFromParticipant", actionTypes);
        Assert.Contains("LinkIdentifierToTarget", actionTypes);
    }

    [Fact]
    public async Task LinkMessageParticipantAsync_ConflictRequiresExplicitResolution()
    {
        await using var fixture = await WorkspaceFixture.CreateAsync();
        var registry = fixture.Services.GetRequiredService<ITargetRegistryService>();

        var caseInfo = await fixture.CreateCaseAsync("Participant Conflict Case");
        var alpha = await registry.CreateTargetAsync(
            new CreateTargetRequest(caseInfo.CaseId, "Alpha", null, null),
            CancellationToken.None
        );
        var bravo = await registry.CreateTargetAsync(
            new CreateTargetRequest(caseInfo.CaseId, "Bravo", null, null),
            CancellationToken.None
        );
        var seeded = await fixture.SeedMessageEventAsync(caseInfo.CaseId, "+15551230001", "+15554445555");

        var initial = await registry.LinkMessageParticipantAsync(
            new LinkMessageParticipantRequest(
                caseInfo.CaseId,
                seeded.MessageEventId,
                MessageParticipantRole.Sender,
                "+1 (555) 123-0001",
                TargetIdentifierType.Phone,
                alpha.TargetId,
                null
            ),
            CancellationToken.None
        );

        await Assert.ThrowsAsync<IdentifierConflictException>(() => registry.LinkMessageParticipantAsync(
            new LinkMessageParticipantRequest(
                caseInfo.CaseId,
                seeded.MessageEventId,
                MessageParticipantRole.Sender,
                "+15551230001",
                TargetIdentifierType.Phone,
                bravo.TargetId,
                null
            ),
            CancellationToken.None
        ));

        var resolved = await registry.LinkMessageParticipantAsync(
            new LinkMessageParticipantRequest(
                caseInfo.CaseId,
                seeded.MessageEventId,
                MessageParticipantRole.Sender,
                "+15551230001",
                TargetIdentifierType.Phone,
                bravo.TargetId,
                null,
                IdentifierConflictResolution.KeepExistingAndAlsoLinkToRequestedTarget
            ),
            CancellationToken.None
        );

        Assert.Equal(bravo.TargetId, resolved.EffectiveTargetId);
        Assert.Equal(initial.IdentifierId, resolved.IdentifierId);
        Assert.False(resolved.MovedIdentifier);
        Assert.False(resolved.UsedExistingTarget);

        await using var db = await fixture.CreateDbContextAsync();
        var links = await db.TargetIdentifierLinks
            .AsNoTracking()
            .Where(link => link.CaseId == caseInfo.CaseId && link.IdentifierId == initial.IdentifierId)
            .OrderBy(link => link.TargetId)
            .ToListAsync();
        Assert.Equal(2, links.Count);
        Assert.Contains(links, link => link.TargetId == alpha.TargetId);
        Assert.Contains(links, link => link.TargetId == bravo.TargetId && !link.IsPrimary);
    }

    [Fact]
    public async Task LinkMessageParticipantAsync_FromSingleMessage_BackfillsPresenceAcrossAllMatchingMessages()
    {
        await using var fixture = await WorkspaceFixture.CreateAsync();
        var registry = fixture.Services.GetRequiredService<ITargetRegistryService>();
        var search = fixture.Services.GetRequiredService<IMessageSearchService>();

        var caseInfo = await fixture.CreateCaseAsync("Presence Backfill Case");
        var target = await registry.CreateTargetAsync(
            new CreateTargetRequest(caseInfo.CaseId, "Alpha Target", null, null),
            CancellationToken.None
        );

        var evidenceId = Guid.NewGuid();
        var threadId = Guid.NewGuid();
        var messageA = Guid.NewGuid();
        var messageB = Guid.NewGuid();
        var messageC = Guid.NewGuid();
        var t1 = new DateTimeOffset(2026, 2, 25, 10, 0, 0, TimeSpan.Zero);
        var t2 = t1.AddMinutes(5);
        var t3 = t1.AddMinutes(10);

        await using (var db = await fixture.CreateDbContextAsync())
        {
            db.EvidenceItems.Add(new EvidenceItemRecord
            {
                EvidenceItemId = evidenceId,
                CaseId = caseInfo.CaseId,
                DisplayName = "Synthetic",
                OriginalPath = "synthetic",
                OriginalFileName = "synthetic.txt",
                AddedAtUtc = t1,
                SizeBytes = 1,
                Sha256Hex = "ab",
                FileExtension = ".txt",
                SourceType = "OTHER",
                ManifestRelativePath = "manifest.json",
                StoredRelativePath = "stored.dat"
            });

            db.MessageThreads.Add(new MessageThreadRecord
            {
                ThreadId = threadId,
                CaseId = caseInfo.CaseId,
                EvidenceItemId = evidenceId,
                Platform = "SMS",
                ThreadKey = "thread-presence-backfill",
                CreatedAtUtc = t1,
                SourceLocator = "test:thread:presence-backfill",
                IngestModuleVersion = "test"
            });

            db.MessageEvents.AddRange(
                new MessageEventRecord
                {
                    MessageEventId = messageA,
                    ThreadId = threadId,
                    CaseId = caseInfo.CaseId,
                    EvidenceItemId = evidenceId,
                    Platform = "SMS",
                    TimestampUtc = t1,
                    Direction = "Incoming",
                    Sender = "+1 (555) 123-0001",
                    Recipients = "+15554440001",
                    Body = "checkpoint alpha",
                    IsDeleted = false,
                    SourceLocator = "xlsx:test#Messages:R10",
                    IngestModuleVersion = "test"
                },
                new MessageEventRecord
                {
                    MessageEventId = messageB,
                    ThreadId = threadId,
                    CaseId = caseInfo.CaseId,
                    EvidenceItemId = evidenceId,
                    Platform = "SMS",
                    TimestampUtc = t2,
                    Direction = "Incoming",
                    Sender = "5551230001",
                    Recipients = "+15554440002",
                    Body = "checkpoint bravo",
                    IsDeleted = false,
                    SourceLocator = "xlsx:test#Messages:R11",
                    IngestModuleVersion = "test"
                },
                new MessageEventRecord
                {
                    MessageEventId = messageC,
                    ThreadId = threadId,
                    CaseId = caseInfo.CaseId,
                    EvidenceItemId = evidenceId,
                    Platform = "SMS",
                    TimestampUtc = t3,
                    Direction = "Incoming",
                    Sender = "+15550009999",
                    Recipients = "+15554440003",
                    Body = "checkpoint charlie",
                    IsDeleted = false,
                    SourceLocator = "xlsx:test#Messages:R12",
                    IngestModuleVersion = "test"
                }
            );

            await db.SaveChangesAsync();
        }

        await registry.LinkMessageParticipantAsync(
            new LinkMessageParticipantRequest(
                caseInfo.CaseId,
                messageA,
                MessageParticipantRole.Sender,
                "+1 (555) 123-0001",
                TargetIdentifierType.Phone,
                target.TargetId,
                null
            ),
            CancellationToken.None
        );

        var summary = await search.GetTargetPresenceSummaryAsync(
            caseInfo.CaseId,
            target.TargetId,
            identifierTypeFilter: null,
            fromUtc: null,
            toUtc: null,
            CancellationToken.None
        );

        Assert.NotNull(summary);
        Assert.Equal(2, summary.TotalCount);
        var identifierSummary = Assert.Single(summary.ByIdentifier);
        Assert.Equal(2, identifierSummary.Count);

        await using var verifyDb = await fixture.CreateDbContextAsync();
        var identifierId = await verifyDb.Identifiers
            .AsNoTracking()
            .Where(record => record.CaseId == caseInfo.CaseId && record.ValueNormalized == "+15551230001")
            .Select(record => record.IdentifierId)
            .SingleAsync();

        var indexedEventIds = await verifyDb.TargetMessagePresences
            .AsNoTracking()
            .Where(row =>
                row.CaseId == caseInfo.CaseId
                && row.TargetId == target.TargetId
                && row.MatchedIdentifierId == identifierId)
            .Select(row => row.MessageEventId)
            .Distinct()
            .OrderBy(id => id)
            .ToListAsync();

        Assert.Equal(2, indexedEventIds.Count);
        Assert.Contains(messageA, indexedEventIds);
        Assert.Contains(messageB, indexedEventIds);
        Assert.DoesNotContain(messageC, indexedEventIds);
    }

    [Fact]
    public async Task SearchAsync_TargetFilter_AfterSingleMessageLink_ReturnsAllMatchingMessageEvents()
    {
        await using var fixture = await WorkspaceFixture.CreateAsync();
        var registry = fixture.Services.GetRequiredService<ITargetRegistryService>();
        var search = fixture.Services.GetRequiredService<IMessageSearchService>();

        var caseInfo = await fixture.CreateCaseAsync("Target Search Index Case");
        var target = await registry.CreateTargetAsync(
            new CreateTargetRequest(caseInfo.CaseId, "Indexed Target", null, null),
            CancellationToken.None
        );

        var evidenceId = Guid.NewGuid();
        var threadId = Guid.NewGuid();
        var messageA = Guid.NewGuid();
        var messageB = Guid.NewGuid();
        var messageOther = Guid.NewGuid();
        var t1 = new DateTimeOffset(2026, 2, 25, 11, 0, 0, TimeSpan.Zero);

        await using (var db = await fixture.CreateDbContextAsync())
        {
            db.EvidenceItems.Add(new EvidenceItemRecord
            {
                EvidenceItemId = evidenceId,
                CaseId = caseInfo.CaseId,
                DisplayName = "Synthetic",
                OriginalPath = "synthetic",
                OriginalFileName = "synthetic.txt",
                AddedAtUtc = t1,
                SizeBytes = 1,
                Sha256Hex = "ab",
                FileExtension = ".txt",
                SourceType = "OTHER",
                ManifestRelativePath = "manifest.json",
                StoredRelativePath = "stored.dat"
            });

            db.MessageThreads.Add(new MessageThreadRecord
            {
                ThreadId = threadId,
                CaseId = caseInfo.CaseId,
                EvidenceItemId = evidenceId,
                Platform = "SMS",
                ThreadKey = "thread-target-search-index",
                CreatedAtUtc = t1,
                SourceLocator = "test:thread:target-search-index",
                IngestModuleVersion = "test"
            });

            db.MessageEvents.AddRange(
                new MessageEventRecord
                {
                    MessageEventId = messageA,
                    ThreadId = threadId,
                    CaseId = caseInfo.CaseId,
                    EvidenceItemId = evidenceId,
                    Platform = "SMS",
                    TimestampUtc = t1,
                    Direction = "Incoming",
                    Sender = "+1 (555) 123-0001",
                    Recipients = "+15554441001",
                    Body = "checkpoint movement",
                    IsDeleted = false,
                    SourceLocator = "xlsx:test#Messages:R20",
                    IngestModuleVersion = "test"
                },
                new MessageEventRecord
                {
                    MessageEventId = messageB,
                    ThreadId = threadId,
                    CaseId = caseInfo.CaseId,
                    EvidenceItemId = evidenceId,
                    Platform = "SMS",
                    TimestampUtc = t1.AddMinutes(1),
                    Direction = "Outgoing",
                    Sender = "5551230001",
                    Recipients = "+15554441002",
                    Body = "checkpoint response",
                    IsDeleted = false,
                    SourceLocator = "xlsx:test#Messages:R21",
                    IngestModuleVersion = "test"
                },
                new MessageEventRecord
                {
                    MessageEventId = messageOther,
                    ThreadId = threadId,
                    CaseId = caseInfo.CaseId,
                    EvidenceItemId = evidenceId,
                    Platform = "SMS",
                    TimestampUtc = t1.AddMinutes(2),
                    Direction = "Incoming",
                    Sender = "+15550009999",
                    Recipients = "+15554441003",
                    Body = "checkpoint movement",
                    IsDeleted = false,
                    SourceLocator = "xlsx:test#Messages:R22",
                    IngestModuleVersion = "test"
                }
            );

            await db.SaveChangesAsync();
        }

        await registry.LinkMessageParticipantAsync(
            new LinkMessageParticipantRequest(
                caseInfo.CaseId,
                messageA,
                MessageParticipantRole.Sender,
                "+1 (555) 123-0001",
                TargetIdentifierType.Phone,
                target.TargetId,
                null
            ),
            CancellationToken.None
        );

        var hits = await search.SearchAsync(
            new MessageSearchRequest(
                caseInfo.CaseId,
                Query: "checkpoint",
                PlatformFilter: null,
                SenderFilter: null,
                RecipientFilter: null,
                TargetId: target.TargetId,
                IdentifierTypeFilter: null,
                DirectionFilter: MessageDirectionFilter.Any,
                FromUtc: null,
                ToUtc: null,
                Take: 20,
                Skip: 0
            ),
            CancellationToken.None
        );

        Assert.Equal(2, hits.Count);
        Assert.Contains(hits, hit => hit.MessageEventId == messageA);
        Assert.Contains(hits, hit => hit.MessageEventId == messageB);
        Assert.DoesNotContain(hits, hit => hit.MessageEventId == messageOther);

        await using var verifyDb = await fixture.CreateDbContextAsync();
        var provenanceLinks = await verifyDb.MessageParticipantLinks
            .AsNoTracking()
            .Where(link => link.CaseId == caseInfo.CaseId && link.TargetId == target.TargetId)
            .ToListAsync();
        Assert.Single(provenanceLinks);
        Assert.Equal(messageA, provenanceLinks[0].MessageEventId);
    }

    [Fact]
    public async Task GlobalPerson_CanLinkAcrossCases_ShowOtherCases_AndUnlink()
    {
        await using var fixture = await WorkspaceFixture.CreateAsync();
        var registry = fixture.Services.GetRequiredService<ITargetRegistryService>();

        var caseA = await fixture.CreateCaseAsync("Global Case A");
        var caseB = await fixture.CreateCaseAsync("Global Case B");

        var targetA = await registry.CreateTargetAsync(
            new CreateTargetRequest(
                caseA.CaseId,
                "Alpha A",
                "AA",
                null,
                CreateGlobalPerson: true
            ),
            CancellationToken.None
        );

        await registry.AddIdentifierAsync(
            new AddTargetIdentifierRequest(
                caseA.CaseId,
                targetA.TargetId,
                TargetIdentifierType.Phone,
                "+1 (555) 123-0001",
                null,
                IsPrimary: true
            ),
            CancellationToken.None
        );

        var detailsA = await registry.GetTargetDetailsAsync(
            caseA.CaseId,
            targetA.TargetId,
            CancellationToken.None
        );
        Assert.NotNull(detailsA);
        Assert.NotNull(detailsA!.GlobalPerson);
        var globalEntityId = detailsA.GlobalPerson!.GlobalEntityId;

        var targetB = await registry.CreateTargetAsync(
            new CreateTargetRequest(
                caseB.CaseId,
                "Alpha B",
                null,
                null,
                GlobalEntityId: globalEntityId
            ),
            CancellationToken.None
        );

        var detailsBAfterLink = await registry.GetTargetDetailsAsync(
            caseB.CaseId,
            targetB.TargetId,
            CancellationToken.None
        );
        Assert.NotNull(detailsBAfterLink);
        Assert.NotNull(detailsBAfterLink!.GlobalPerson);
        Assert.Equal(globalEntityId, detailsBAfterLink.GlobalPerson!.GlobalEntityId);
        Assert.Contains(
            detailsBAfterLink.GlobalPerson.Identifiers,
            identifier => identifier.Type == TargetIdentifierType.Phone
                && identifier.ValueNormalized == "+15551230001"
        );
        Assert.Contains(
            detailsBAfterLink.GlobalPerson.OtherCases,
            item => item.CaseId == caseA.CaseId && item.TargetId == targetA.TargetId
        );

        var globalSearchHits = await registry.SearchGlobalPersonsAsync(
            "5551230001",
            take: 10,
            CancellationToken.None
        );
        Assert.Contains(globalSearchHits, person => person.GlobalEntityId == globalEntityId);

        await registry.UnlinkTargetFromGlobalPersonAsync(
            caseB.CaseId,
            targetB.TargetId,
            CancellationToken.None
        );

        var detailsBAfterUnlink = await registry.GetTargetDetailsAsync(
            caseB.CaseId,
            targetB.TargetId,
            CancellationToken.None
        );
        Assert.NotNull(detailsBAfterUnlink);
        Assert.Null(detailsBAfterUnlink!.GlobalPerson);
    }

    [Fact]
    public async Task SearchAsync_GlobalPersonFilter_IsCaseScopedAcrossMultipleCases()
    {
        await using var fixture = await WorkspaceFixture.CreateAsync();
        var registry = fixture.Services.GetRequiredService<ITargetRegistryService>();
        var search = fixture.Services.GetRequiredService<IMessageSearchService>();

        var caseA = await fixture.CreateCaseAsync("Global Search Case A");
        var caseB = await fixture.CreateCaseAsync("Global Search Case B");

        var targetA = await registry.CreateTargetAsync(
            new CreateTargetRequest(caseA.CaseId, "Global Alpha", null, null, CreateGlobalPerson: true),
            CancellationToken.None
        );
        var detailsA = await registry.GetTargetDetailsAsync(caseA.CaseId, targetA.TargetId, CancellationToken.None);
        Assert.NotNull(detailsA);
        Assert.NotNull(detailsA!.GlobalPerson);
        var globalEntityId = detailsA.GlobalPerson!.GlobalEntityId;

        var targetB = await registry.CreateTargetAsync(
            new CreateTargetRequest(caseB.CaseId, "Global Bravo", null, null, GlobalEntityId: globalEntityId),
            CancellationToken.None
        );

        var seededA1 = await fixture.SeedMessageEventAsync(caseA.CaseId, "+1 (555) 123-0001", "+15550000001");
        var seededA2 = await fixture.SeedMessageEventAsync(caseA.CaseId, "5551230001", "+15550000002");
        var seededB1 = await fixture.SeedMessageEventAsync(caseB.CaseId, "+15551230001", "+15550000003");

        await registry.LinkMessageParticipantAsync(
            new LinkMessageParticipantRequest(
                caseA.CaseId,
                seededA1.MessageEventId,
                MessageParticipantRole.Sender,
                "+1 (555) 123-0001",
                TargetIdentifierType.Phone,
                targetA.TargetId,
                null
            ),
            CancellationToken.None
        );

        await registry.LinkMessageParticipantAsync(
            new LinkMessageParticipantRequest(
                caseB.CaseId,
                seededB1.MessageEventId,
                MessageParticipantRole.Sender,
                "+1 (555) 123-0001",
                TargetIdentifierType.Phone,
                targetB.TargetId,
                null
            ),
            CancellationToken.None
        );

        var caseAHits = await search.SearchAsync(
            new MessageSearchRequest(
                caseA.CaseId,
                Query: "synthetic",
                PlatformFilter: null,
                SenderFilter: null,
                RecipientFilter: null,
                TargetId: null,
                IdentifierTypeFilter: TargetIdentifierType.Phone,
                DirectionFilter: MessageDirectionFilter.Any,
                FromUtc: null,
                ToUtc: null,
                Take: 20,
                Skip: 0,
                GlobalEntityId: globalEntityId
            ),
            CancellationToken.None
        );

        Assert.Equal(2, caseAHits.Count);
        Assert.Contains(caseAHits, hit => hit.MessageEventId == seededA1.MessageEventId);
        Assert.Contains(caseAHits, hit => hit.MessageEventId == seededA2.MessageEventId);
        Assert.DoesNotContain(caseAHits, hit => hit.MessageEventId == seededB1.MessageEventId);

        var caseBHits = await search.SearchAsync(
            new MessageSearchRequest(
                caseB.CaseId,
                Query: "synthetic",
                PlatformFilter: null,
                SenderFilter: null,
                RecipientFilter: null,
                TargetId: null,
                IdentifierTypeFilter: TargetIdentifierType.Phone,
                DirectionFilter: MessageDirectionFilter.Any,
                FromUtc: null,
                ToUtc: null,
                Take: 20,
                Skip: 0,
                GlobalEntityId: globalEntityId
            ),
            CancellationToken.None
        );

        var single = Assert.Single(caseBHits);
        Assert.Equal(seededB1.MessageEventId, single.MessageEventId);
    }

    [Fact]
    public async Task CreateAndLinkGlobalPersonAsync_IdentifierConflict_RequiresExplicitResolution()
    {
        await using var fixture = await WorkspaceFixture.CreateAsync();
        var registry = fixture.Services.GetRequiredService<ITargetRegistryService>();

        var caseA = await fixture.CreateCaseAsync("Global Conflict Case A");
        var caseB = await fixture.CreateCaseAsync("Global Conflict Case B");

        var targetA = await registry.CreateTargetAsync(
            new CreateTargetRequest(caseA.CaseId, "Conflict A", null, null, CreateGlobalPerson: true),
            CancellationToken.None
        );
        await registry.AddIdentifierAsync(
            new AddTargetIdentifierRequest(
                caseA.CaseId,
                targetA.TargetId,
                TargetIdentifierType.Phone,
                "+1 (555) 123-0001",
                null,
                IsPrimary: true
            ),
            CancellationToken.None
        );

        var targetB = await registry.CreateTargetAsync(
            new CreateTargetRequest(caseB.CaseId, "Conflict B", null, null),
            CancellationToken.None
        );
        await registry.AddIdentifierAsync(
            new AddTargetIdentifierRequest(
                caseB.CaseId,
                targetB.TargetId,
                TargetIdentifierType.Phone,
                "5551230001",
                null,
                IsPrimary: true
            ),
            CancellationToken.None
        );

        await Assert.ThrowsAsync<GlobalPersonIdentifierConflictException>(() =>
            registry.CreateAndLinkGlobalPersonAsync(
                new CreateGlobalPersonForTargetRequest(
                    caseB.CaseId,
                    targetB.TargetId,
                    "Conflict B Person"
                ),
                CancellationToken.None
            )
        );

        var detailsA = await registry.GetTargetDetailsAsync(caseA.CaseId, targetA.TargetId, CancellationToken.None);
        Assert.NotNull(detailsA);
        Assert.NotNull(detailsA!.GlobalPerson);

        var linked = await registry.CreateAndLinkGlobalPersonAsync(
            new CreateGlobalPersonForTargetRequest(
                caseB.CaseId,
                targetB.TargetId,
                "Conflict B Person",
                GlobalPersonIdentifierConflictResolution.UseExistingPerson
            ),
            CancellationToken.None
        );

        Assert.Equal(detailsA.GlobalPerson!.GlobalEntityId, linked.GlobalEntityId);
    }

    [Fact]
    public async Task GetTargetsAsync_UsesSqliteProvider_OrdersInMemoryWithoutDateTimeOffsetOrderByFailure()
    {
        await using var fixture = await WorkspaceFixture.CreateAsync();
        var registry = fixture.Services.GetRequiredService<ITargetRegistryService>();
        var caseInfo = await fixture.CreateCaseAsync("Target Ordering Case");

        var now = new DateTimeOffset(2026, 2, 20, 12, 0, 0, TimeSpan.Zero);
        var targetA = Guid.NewGuid();
        var targetB = Guid.NewGuid();
        var targetC = Guid.NewGuid();

        await using (var db = await fixture.CreateDbContextAsync())
        {
            db.Targets.AddRange(
                new TargetRecord
                {
                    TargetId = targetA,
                    CaseId = caseInfo.CaseId,
                    DisplayName = "Zulu",
                    PrimaryAlias = null,
                    Notes = null,
                    CreatedAtUtc = now.AddHours(-4),
                    UpdatedAtUtc = now.AddHours(-1),
                    SourceType = "Manual",
                    SourceEvidenceItemId = null,
                    SourceLocator = "test:target-a",
                    IngestModuleVersion = "test"
                },
                new TargetRecord
                {
                    TargetId = targetB,
                    CaseId = caseInfo.CaseId,
                    DisplayName = "alpha",
                    PrimaryAlias = null,
                    Notes = null,
                    CreatedAtUtc = now.AddHours(-3),
                    UpdatedAtUtc = now.AddHours(-1),
                    SourceType = "Manual",
                    SourceEvidenceItemId = null,
                    SourceLocator = "test:target-b",
                    IngestModuleVersion = "test"
                },
                new TargetRecord
                {
                    TargetId = targetC,
                    CaseId = caseInfo.CaseId,
                    DisplayName = "Bravo",
                    PrimaryAlias = null,
                    Notes = null,
                    CreatedAtUtc = now.AddMinutes(-30),
                    UpdatedAtUtc = default,
                    SourceType = "Manual",
                    SourceEvidenceItemId = null,
                    SourceLocator = "test:target-c",
                    IngestModuleVersion = "test"
                }
            );

            await db.SaveChangesAsync();
        }

        var exception = await Record.ExceptionAsync(() => registry.GetTargetsAsync(
            caseInfo.CaseId,
            search: null,
            CancellationToken.None
        ));
        Assert.Null(exception);

        var targets = await registry.GetTargetsAsync(
            caseInfo.CaseId,
            search: null,
            CancellationToken.None
        );

        Assert.Equal(
            [targetC, targetB, targetA],
            targets.Select(target => target.TargetId).ToArray()
        );
    }

    [Fact]
    public async Task TargetCreateUpdateAndIdentifierEdit_WriteAuditTrail()
    {
        await using var fixture = await WorkspaceFixture.CreateAsync();
        var registry = fixture.Services.GetRequiredService<ITargetRegistryService>();

        var caseInfo = await fixture.CreateCaseAsync("Audit Trail Case");
        var target = await registry.CreateTargetAsync(
            new CreateTargetRequest(caseInfo.CaseId, "Initial Name", "Alias One", "Notes A"),
            CancellationToken.None
        );

        await registry.UpdateTargetAsync(
            new UpdateTargetRequest(caseInfo.CaseId, target.TargetId, "Updated Name", "Alias Two", "Notes B"),
            CancellationToken.None
        );

        var createdIdentifier = await registry.AddIdentifierAsync(
            new AddTargetIdentifierRequest(
                caseInfo.CaseId,
                target.TargetId,
                TargetIdentifierType.Username,
                "AliasTwo",
                "first pass",
                IsPrimary: true
            ),
            CancellationToken.None
        );

        await registry.UpdateIdentifierAsync(
            new UpdateTargetIdentifierRequest(
                caseInfo.CaseId,
                target.TargetId,
                createdIdentifier.Identifier.IdentifierId,
                TargetIdentifierType.Username,
                "AliasTwo",
                "updated notes",
                IsPrimary: true
            ),
            CancellationToken.None
        );

        await using var db = await fixture.CreateDbContextAsync();
        var actionTypes = await db.AuditEvents
            .AsNoTracking()
            .Where(audit => audit.CaseId == caseInfo.CaseId)
            .Select(audit => audit.ActionType)
            .ToListAsync();

        Assert.Contains("TargetCreated", actionTypes);
        Assert.Contains("TargetUpdated", actionTypes);
        Assert.Contains("IdentifierCreated", actionTypes);
        Assert.Contains("IdentifierUpdated", actionTypes);
    }

    private sealed class WorkspaceFixture : IAsyncDisposable
    {
        private readonly ServiceProvider _provider;
        private readonly TestWorkspacePathProvider _pathProvider;

        private WorkspaceFixture(ServiceProvider provider, TestWorkspacePathProvider pathProvider)
        {
            _provider = provider;
            _pathProvider = pathProvider;
        }

        public IServiceProvider Services => _provider;

        public static async Task<WorkspaceFixture> CreateAsync()
        {
            var workspaceRoot = Path.Combine(
                Path.GetTempPath(),
                "CaseGraph.Infrastructure.Tests",
                Guid.NewGuid().ToString("N")
            );
            Directory.CreateDirectory(workspaceRoot);

            var pathProvider = new TestWorkspacePathProvider(workspaceRoot);
            var services = new ServiceCollection();
            services.AddSingleton<IClock>(
                new FixedClock(new DateTimeOffset(2026, 2, 15, 12, 0, 0, TimeSpan.Zero))
            );
            services.AddSingleton<IWorkspacePathProvider>(pathProvider);
            services.AddDbContextFactory<WorkspaceDbContext>(options =>
            {
                Directory.CreateDirectory(pathProvider.WorkspaceRoot);
                options.UseSqlite($"Data Source={pathProvider.WorkspaceDbPath}");
            });
            services.AddSingleton<WorkspaceDbRebuilder>();
            services.AddSingleton<WorkspaceDbInitializer>();
            services.AddSingleton<IWorkspaceDbInitializer>(
                provider => provider.GetRequiredService<WorkspaceDbInitializer>()
            );
            services.AddSingleton<IWorkspaceDatabaseInitializer>(
                provider => provider.GetRequiredService<WorkspaceDbInitializer>()
            );
            services.AddSingleton<IWorkspaceWriteGate, WorkspaceWriteGate>();
            services.AddSingleton<IAuditLogService, AuditLogService>();
            services.AddSingleton<ICaseWorkspaceService, CaseWorkspaceService>();
            services.AddSingleton<IMessageSearchService, MessageSearchService>();
            services.AddSingleton<ITargetMessagePresenceIndexService, TargetMessagePresenceIndexService>();
            services.AddSingleton<ITargetRegistryService, TargetRegistryService>();

            var provider = services.BuildServiceProvider();
            var initializer = provider.GetRequiredService<IWorkspaceDatabaseInitializer>();
            await initializer.EnsureInitializedAsync(CancellationToken.None);

            return new WorkspaceFixture(provider, pathProvider);
        }

        public Task<WorkspaceDbContext> CreateDbContextAsync()
        {
            var factory = _provider.GetRequiredService<IDbContextFactory<WorkspaceDbContext>>();
            return factory.CreateDbContextAsync(CancellationToken.None);
        }

        public Task<CaseInfo> CreateCaseAsync(string name)
        {
            var workspace = _provider.GetRequiredService<ICaseWorkspaceService>();
            return workspace.CreateCaseAsync(name, CancellationToken.None);
        }

        public async Task<(Guid MessageEventId, Guid EvidenceItemId)> SeedMessageEventAsync(
            Guid caseId,
            string sender,
            string recipients
        )
        {
            var evidenceItemId = Guid.NewGuid();
            var threadId = Guid.NewGuid();
            var messageEventId = Guid.NewGuid();

            await using var db = await CreateDbContextAsync();
            db.EvidenceItems.Add(new EvidenceItemRecord
            {
                EvidenceItemId = evidenceItemId,
                CaseId = caseId,
                DisplayName = "Synthetic",
                OriginalPath = "synthetic",
                OriginalFileName = "synthetic.txt",
                AddedAtUtc = DateTimeOffset.UtcNow,
                SizeBytes = 1,
                Sha256Hex = "ab",
                FileExtension = ".txt",
                SourceType = "OTHER",
                ManifestRelativePath = "manifest.json",
                StoredRelativePath = "stored.dat"
            });

            db.MessageThreads.Add(new MessageThreadRecord
            {
                ThreadId = threadId,
                CaseId = caseId,
                EvidenceItemId = evidenceItemId,
                Platform = "SMS",
                ThreadKey = "thread-participant-link",
                CreatedAtUtc = DateTimeOffset.UtcNow,
                SourceLocator = "test:thread",
                IngestModuleVersion = "test"
            });

            db.MessageEvents.Add(new MessageEventRecord
            {
                MessageEventId = messageEventId,
                ThreadId = threadId,
                CaseId = caseId,
                EvidenceItemId = evidenceItemId,
                Platform = "SMS",
                TimestampUtc = DateTimeOffset.UtcNow,
                Direction = "Incoming",
                Sender = sender,
                Recipients = recipients,
                Body = "synthetic body",
                IsDeleted = false,
                SourceLocator = "xlsx:test#Messages:R2",
                IngestModuleVersion = "test"
            });

            await db.SaveChangesAsync();
            return (messageEventId, evidenceItemId);
        }

        public async ValueTask DisposeAsync()
        {
            await _provider.DisposeAsync();

            if (!Directory.Exists(_pathProvider.WorkspaceRoot))
            {
                return;
            }

            for (var attempt = 1; attempt <= 5; attempt++)
            {
                try
                {
                    Directory.Delete(_pathProvider.WorkspaceRoot, recursive: true);
                    return;
                }
                catch (IOException)
                {
                    if (attempt == 5)
                    {
                        return;
                    }

                    await Task.Delay(50);
                }
                catch (UnauthorizedAccessException)
                {
                    if (attempt == 5)
                    {
                        return;
                    }

                    await Task.Delay(50);
                }
            }
        }
    }

    private sealed class TestWorkspacePathProvider : IWorkspacePathProvider
    {
        public TestWorkspacePathProvider(string workspaceRoot)
        {
            WorkspaceRoot = workspaceRoot;
            WorkspaceDbPath = Path.Combine(workspaceRoot, "workspace.db");
            CasesRoot = Path.Combine(workspaceRoot, "cases");
        }

        public string WorkspaceRoot { get; }

        public string WorkspaceDbPath { get; }

        public string CasesRoot { get; }
    }

    private sealed class FixedClock : IClock
    {
        public FixedClock(DateTimeOffset utcNow)
        {
            UtcNow = utcNow;
        }

        public DateTimeOffset UtcNow { get; }
    }
}

