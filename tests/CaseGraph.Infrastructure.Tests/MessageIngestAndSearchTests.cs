using CaseGraph.Core.Abstractions;
using CaseGraph.Core.Models;
using CaseGraph.Infrastructure.Persistence;
using CaseGraph.Infrastructure.Persistence.Entities;
using CaseGraph.Infrastructure.Services;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using System.Diagnostics;
using System.IO.Compression;
using System.Text;
using System.Text.Json;

namespace CaseGraph.Infrastructure.Tests;

public sealed class MessageIngestAndSearchTests
{
    [Fact]
    public async Task SearchAsync_Fts_ReturnsInsertedMessageRows()
    {
        await using var fixture = await WorkspaceFixture.CreateAsync();
        var workspace = fixture.Services.GetRequiredService<ICaseWorkspaceService>();
        var search = fixture.Services.GetRequiredService<IMessageSearchService>();

        var caseInfo = await workspace.CreateCaseAsync("FTS Case", CancellationToken.None);
        var threadId = Guid.NewGuid();
        var evidenceItemId = Guid.NewGuid();

        await using (var db = await fixture.CreateDbContextAsync())
        {
            db.EvidenceItems.Add(new EvidenceItemRecord
            {
                EvidenceItemId = evidenceItemId,
                CaseId = caseInfo.CaseId,
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
                CaseId = caseInfo.CaseId,
                EvidenceItemId = evidenceItemId,
                Platform = "SMS",
                ThreadKey = "thread-1",
                CreatedAtUtc = DateTimeOffset.UtcNow,
                SourceLocator = "test:thread",
                IngestModuleVersion = "test"
            });

            db.MessageEvents.Add(new MessageEventRecord
            {
                MessageEventId = Guid.NewGuid(),
                ThreadId = threadId,
                CaseId = caseInfo.CaseId,
                EvidenceItemId = evidenceItemId,
                Platform = "SMS",
                TimestampUtc = DateTimeOffset.UtcNow,
                Direction = "Incoming",
                Sender = "+15551212",
                Recipients = "+15554321",
                Body = "This is a confiscated burner phone message.",
                IsDeleted = false,
                SourceLocator = "xlsx:test#Messages:R2",
                IngestModuleVersion = "test"
            });

            await db.SaveChangesAsync();
        }

        var hits = await search.SearchAsync(
            caseInfo.CaseId,
            "burner",
            platformFilter: null,
            senderFilter: null,
            recipientFilter: null,
            take: 20,
            skip: 0,
            CancellationToken.None
        );
        Assert.NotEmpty(hits);
        Assert.Contains(hits, h => (h.Snippet ?? string.Empty).Contains("burner", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task SearchAsync_Fts_RespectsPlatformFilter()
    {
        await using var fixture = await WorkspaceFixture.CreateAsync();
        var workspace = fixture.Services.GetRequiredService<ICaseWorkspaceService>();
        var search = fixture.Services.GetRequiredService<IMessageSearchService>();

        var caseInfo = await workspace.CreateCaseAsync("Filter Case", CancellationToken.None);
        var evidenceItemId = Guid.NewGuid();
        var threadSms = Guid.NewGuid();
        var threadSignal = Guid.NewGuid();

        await using (var db = await fixture.CreateDbContextAsync())
        {
            db.EvidenceItems.Add(new EvidenceItemRecord
            {
                EvidenceItemId = evidenceItemId,
                CaseId = caseInfo.CaseId,
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

            db.MessageThreads.AddRange(
                new MessageThreadRecord
                {
                    ThreadId = threadSms,
                    CaseId = caseInfo.CaseId,
                    EvidenceItemId = evidenceItemId,
                    Platform = "SMS",
                    ThreadKey = "thread-sms",
                    CreatedAtUtc = DateTimeOffset.UtcNow,
                    SourceLocator = "test:thread:sms",
                    IngestModuleVersion = "test"
                },
                new MessageThreadRecord
                {
                    ThreadId = threadSignal,
                    CaseId = caseInfo.CaseId,
                    EvidenceItemId = evidenceItemId,
                    Platform = "Signal",
                    ThreadKey = "thread-signal",
                    CreatedAtUtc = DateTimeOffset.UtcNow,
                    SourceLocator = "test:thread:signal",
                    IngestModuleVersion = "test"
                }
            );

            db.MessageEvents.AddRange(
                new MessageEventRecord
                {
                    MessageEventId = Guid.NewGuid(),
                    ThreadId = threadSms,
                    CaseId = caseInfo.CaseId,
                    EvidenceItemId = evidenceItemId,
                    Platform = "SMS",
                    TimestampUtc = DateTimeOffset.UtcNow,
                    Direction = "Incoming",
                    Sender = "+15550100",
                    Recipients = "+15550101",
                    Body = "checkpoint update",
                    IsDeleted = false,
                    SourceLocator = "xlsx:test#Messages:R2",
                    IngestModuleVersion = "test"
                },
                new MessageEventRecord
                {
                    MessageEventId = Guid.NewGuid(),
                    ThreadId = threadSignal,
                    CaseId = caseInfo.CaseId,
                    EvidenceItemId = evidenceItemId,
                    Platform = "Signal",
                    TimestampUtc = DateTimeOffset.UtcNow,
                    Direction = "Incoming",
                    Sender = "+15550100",
                    Recipients = "+15550101",
                    Body = "checkpoint update",
                    IsDeleted = false,
                    SourceLocator = "xlsx:test#Messages:R3",
                    IngestModuleVersion = "test"
                }
            );

            await db.SaveChangesAsync();
        }

        var hits = await search.SearchAsync(
            caseInfo.CaseId,
            "checkpoint",
            platformFilter: "SMS",
            senderFilter: null,
            recipientFilter: null,
            take: 20,
            skip: 0,
            CancellationToken.None
        );

        Assert.NotEmpty(hits);
        Assert.All(hits, hit => Assert.Equal("SMS", hit.Platform));
    }

    [Fact]
    public async Task SearchAsync_Fts_RespectsSenderAndRecipientFilters()
    {
        await using var fixture = await WorkspaceFixture.CreateAsync();
        var workspace = fixture.Services.GetRequiredService<ICaseWorkspaceService>();
        var search = fixture.Services.GetRequiredService<IMessageSearchService>();

        var caseInfo = await workspace.CreateCaseAsync("Sender Recipient Filter Case", CancellationToken.None);
        var evidenceItemId = Guid.NewGuid();
        var threadId = Guid.NewGuid();

        await using (var db = await fixture.CreateDbContextAsync())
        {
            db.EvidenceItems.Add(new EvidenceItemRecord
            {
                EvidenceItemId = evidenceItemId,
                CaseId = caseInfo.CaseId,
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
                CaseId = caseInfo.CaseId,
                EvidenceItemId = evidenceItemId,
                Platform = "SMS",
                ThreadKey = "thread-filter",
                CreatedAtUtc = DateTimeOffset.UtcNow,
                SourceLocator = "test:thread:filter",
                IngestModuleVersion = "test"
            });

            db.MessageEvents.AddRange(
                new MessageEventRecord
                {
                    MessageEventId = Guid.NewGuid(),
                    ThreadId = threadId,
                    CaseId = caseInfo.CaseId,
                    EvidenceItemId = evidenceItemId,
                    Platform = "SMS",
                    TimestampUtc = DateTimeOffset.UtcNow,
                    Direction = "Incoming",
                    Sender = "+15551230001",
                    Recipients = "+15551230077",
                    Body = "checkpoint update",
                    IsDeleted = false,
                    SourceLocator = "xlsx:test#Messages:R10",
                    IngestModuleVersion = "test"
                },
                new MessageEventRecord
                {
                    MessageEventId = Guid.NewGuid(),
                    ThreadId = threadId,
                    CaseId = caseInfo.CaseId,
                    EvidenceItemId = evidenceItemId,
                    Platform = "SMS",
                    TimestampUtc = DateTimeOffset.UtcNow,
                    Direction = "Incoming",
                    Sender = "+15559990000",
                    Recipients = "+15554443333",
                    Body = "checkpoint update",
                    IsDeleted = false,
                    SourceLocator = "xlsx:test#Messages:R11",
                    IngestModuleVersion = "test"
                }
            );

            await db.SaveChangesAsync();
        }

        var senderFiltered = await search.SearchAsync(
            caseInfo.CaseId,
            "checkpoint",
            platformFilter: null,
            senderFilter: "1230001",
            recipientFilter: null,
            take: 20,
            skip: 0,
            CancellationToken.None
        );

        Assert.Single(senderFiltered);
        Assert.Contains("1230001", senderFiltered[0].Sender);

        var recipientFiltered = await search.SearchAsync(
            caseInfo.CaseId,
            "checkpoint",
            platformFilter: null,
            senderFilter: null,
            recipientFilter: "4443333",
            take: 20,
            skip: 0,
            CancellationToken.None
        );

        Assert.Single(recipientFiltered);
        Assert.Contains("4443333", recipientFiltered[0].Recipients);
    }

    [Fact]
    public async Task GetTargetPresenceSummaryAsync_ReturnsCountsAndLastSeenByIdentifier()
    {
        await using var fixture = await WorkspaceFixture.CreateAsync();
        var workspace = fixture.Services.GetRequiredService<ICaseWorkspaceService>();
        var search = fixture.Services.GetRequiredService<IMessageSearchService>();

        var caseInfo = await workspace.CreateCaseAsync("Presence Summary Case", CancellationToken.None);
        var evidenceItemId = Guid.NewGuid();
        var threadId = Guid.NewGuid();
        var targetId = Guid.NewGuid();
        var phoneIdentifierId = Guid.NewGuid();
        var emailIdentifierId = Guid.NewGuid();
        var handleIdentifierId = Guid.NewGuid();
        var messageOneId = Guid.NewGuid();
        var messageTwoId = Guid.NewGuid();
        var messageThreeId = Guid.NewGuid();
        var t1 = new DateTimeOffset(2026, 2, 13, 10, 0, 0, TimeSpan.Zero);
        var t2 = new DateTimeOffset(2026, 2, 13, 11, 30, 0, TimeSpan.Zero);

        await using (var db = await fixture.CreateDbContextAsync())
        {
            db.EvidenceItems.Add(new EvidenceItemRecord
            {
                EvidenceItemId = evidenceItemId,
                CaseId = caseInfo.CaseId,
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
                CaseId = caseInfo.CaseId,
                EvidenceItemId = evidenceItemId,
                Platform = "SMS",
                ThreadKey = "thread-presence",
                CreatedAtUtc = DateTimeOffset.UtcNow,
                SourceLocator = "test:thread:presence",
                IngestModuleVersion = "test"
            });

            db.MessageEvents.AddRange(
                new MessageEventRecord
                {
                    MessageEventId = messageOneId,
                    ThreadId = threadId,
                    CaseId = caseInfo.CaseId,
                    EvidenceItemId = evidenceItemId,
                    Platform = "SMS",
                    TimestampUtc = t1,
                    Direction = "Incoming",
                    Sender = "+15551230001",
                    Recipients = "alpha@example.com",
                    Body = "message one",
                    IsDeleted = false,
                    SourceLocator = "xlsx:test#Messages:R1",
                    IngestModuleVersion = "test"
                },
                new MessageEventRecord
                {
                    MessageEventId = messageTwoId,
                    ThreadId = threadId,
                    CaseId = caseInfo.CaseId,
                    EvidenceItemId = evidenceItemId,
                    Platform = "SMS",
                    TimestampUtc = t2,
                    Direction = "Outgoing",
                    Sender = "+15551230001",
                    Recipients = "alpha@example.com",
                    Body = "message two",
                    IsDeleted = false,
                    SourceLocator = "xlsx:test#Messages:R2",
                    IngestModuleVersion = "test"
                },
                new MessageEventRecord
                {
                    MessageEventId = messageThreeId,
                    ThreadId = threadId,
                    CaseId = caseInfo.CaseId,
                    EvidenceItemId = evidenceItemId,
                    Platform = "SMS",
                    TimestampUtc = null,
                    Direction = "Incoming",
                    Sender = "alpha@example.com",
                    Recipients = "+15551230099",
                    Body = "message three",
                    IsDeleted = false,
                    SourceLocator = "xlsx:test#Messages:R3",
                    IngestModuleVersion = "test"
                }
            );

            db.Targets.Add(new TargetRecord
            {
                TargetId = targetId,
                CaseId = caseInfo.CaseId,
                DisplayName = "Alpha",
                PrimaryAlias = null,
                Notes = null,
                CreatedAtUtc = t1,
                UpdatedAtUtc = t1,
                SourceType = "Manual",
                SourceEvidenceItemId = null,
                SourceLocator = "manual:test:target",
                IngestModuleVersion = "test"
            });

            db.Identifiers.AddRange(
                new IdentifierRecord
                {
                    IdentifierId = phoneIdentifierId,
                    CaseId = caseInfo.CaseId,
                    Type = "Phone",
                    ValueRaw = "+1 (555) 123-0001",
                    ValueNormalized = "+15551230001",
                    Notes = null,
                    CreatedAtUtc = t1,
                    SourceType = "Manual",
                    SourceEvidenceItemId = null,
                    SourceLocator = "manual:test:id:phone",
                    IngestModuleVersion = "test"
                },
                new IdentifierRecord
                {
                    IdentifierId = emailIdentifierId,
                    CaseId = caseInfo.CaseId,
                    Type = "Email",
                    ValueRaw = "alpha@example.com",
                    ValueNormalized = "alpha@example.com",
                    Notes = null,
                    CreatedAtUtc = t1,
                    SourceType = "Manual",
                    SourceEvidenceItemId = null,
                    SourceLocator = "manual:test:id:email",
                    IngestModuleVersion = "test"
                },
                new IdentifierRecord
                {
                    IdentifierId = handleIdentifierId,
                    CaseId = caseInfo.CaseId,
                    Type = "SocialHandle",
                    ValueRaw = "@alpha",
                    ValueNormalized = "@alpha",
                    Notes = null,
                    CreatedAtUtc = t1,
                    SourceType = "Manual",
                    SourceEvidenceItemId = null,
                    SourceLocator = "manual:test:id:handle",
                    IngestModuleVersion = "test"
                }
            );

            db.TargetIdentifierLinks.AddRange(
                new TargetIdentifierLinkRecord
                {
                    LinkId = Guid.NewGuid(),
                    CaseId = caseInfo.CaseId,
                    TargetId = targetId,
                    IdentifierId = phoneIdentifierId,
                    IsPrimary = true,
                    CreatedAtUtc = t1,
                    SourceType = "Manual",
                    SourceEvidenceItemId = null,
                    SourceLocator = "manual:test:link:phone",
                    IngestModuleVersion = "test"
                },
                new TargetIdentifierLinkRecord
                {
                    LinkId = Guid.NewGuid(),
                    CaseId = caseInfo.CaseId,
                    TargetId = targetId,
                    IdentifierId = emailIdentifierId,
                    IsPrimary = false,
                    CreatedAtUtc = t1,
                    SourceType = "Manual",
                    SourceEvidenceItemId = null,
                    SourceLocator = "manual:test:link:email",
                    IngestModuleVersion = "test"
                },
                new TargetIdentifierLinkRecord
                {
                    LinkId = Guid.NewGuid(),
                    CaseId = caseInfo.CaseId,
                    TargetId = targetId,
                    IdentifierId = handleIdentifierId,
                    IsPrimary = false,
                    CreatedAtUtc = t1,
                    SourceType = "Manual",
                    SourceEvidenceItemId = null,
                    SourceLocator = "manual:test:link:handle",
                    IngestModuleVersion = "test"
                }
            );

            db.TargetMessagePresences.AddRange(
                new TargetMessagePresenceRecord
                {
                    PresenceId = Guid.NewGuid(),
                    CaseId = caseInfo.CaseId,
                    TargetId = targetId,
                    MessageEventId = messageOneId,
                    MatchedIdentifierId = phoneIdentifierId,
                    Role = "Sender",
                    EvidenceItemId = evidenceItemId,
                    SourceLocator = "xlsx:test#Messages:R1",
                    MessageTimestampUtc = t1,
                    FirstSeenUtc = t1,
                    LastSeenUtc = t1
                },
                new TargetMessagePresenceRecord
                {
                    PresenceId = Guid.NewGuid(),
                    CaseId = caseInfo.CaseId,
                    TargetId = targetId,
                    MessageEventId = messageTwoId,
                    MatchedIdentifierId = phoneIdentifierId,
                    Role = "Sender",
                    EvidenceItemId = evidenceItemId,
                    SourceLocator = "xlsx:test#Messages:R2",
                    MessageTimestampUtc = t2,
                    FirstSeenUtc = t2,
                    LastSeenUtc = t2
                },
                new TargetMessagePresenceRecord
                {
                    PresenceId = Guid.NewGuid(),
                    CaseId = caseInfo.CaseId,
                    TargetId = targetId,
                    MessageEventId = messageTwoId,
                    MatchedIdentifierId = emailIdentifierId,
                    Role = "Recipient",
                    EvidenceItemId = evidenceItemId,
                    SourceLocator = "xlsx:test#Messages:R2",
                    MessageTimestampUtc = t2,
                    FirstSeenUtc = t2,
                    LastSeenUtc = t2
                },
                new TargetMessagePresenceRecord
                {
                    PresenceId = Guid.NewGuid(),
                    CaseId = caseInfo.CaseId,
                    TargetId = targetId,
                    MessageEventId = messageThreeId,
                    MatchedIdentifierId = emailIdentifierId,
                    Role = "Sender",
                    EvidenceItemId = evidenceItemId,
                    SourceLocator = "xlsx:test#Messages:R3",
                    MessageTimestampUtc = null,
                    FirstSeenUtc = t2.AddMinutes(1),
                    LastSeenUtc = t2.AddMinutes(1)
                }
            );

            await db.SaveChangesAsync();
        }

        var summary = await search.GetTargetPresenceSummaryAsync(
            caseInfo.CaseId,
            targetId,
            identifierTypeFilter: null,
            fromUtc: null,
            toUtc: null,
            CancellationToken.None
        );

        Assert.NotNull(summary);
        Assert.Equal(3, summary.TotalCount);
        Assert.Equal(t2, summary.LastSeenUtc);

        var phoneSummary = Assert.Single(summary.ByIdentifier.Where(item => item.IdentifierId == phoneIdentifierId));
        Assert.Equal(2, phoneSummary.Count);
        Assert.Equal(t2, phoneSummary.LastSeenUtc);

        var emailSummary = Assert.Single(summary.ByIdentifier.Where(item => item.IdentifierId == emailIdentifierId));
        Assert.Equal(2, emailSummary.Count);
        Assert.Equal(t2, emailSummary.LastSeenUtc);

        var handleSummary = Assert.Single(summary.ByIdentifier.Where(item => item.IdentifierId == handleIdentifierId));
        Assert.Equal(0, handleSummary.Count);
        Assert.Null(handleSummary.LastSeenUtc);
    }

    [Fact]
    public async Task SearchAsync_TargetFilter_CanCombineWithKeyword()
    {
        await using var fixture = await WorkspaceFixture.CreateAsync();
        var workspace = fixture.Services.GetRequiredService<ICaseWorkspaceService>();
        var search = fixture.Services.GetRequiredService<IMessageSearchService>();

        var caseInfo = await workspace.CreateCaseAsync("Target Filter Keyword Case", CancellationToken.None);
        var evidenceItemId = Guid.NewGuid();
        var threadId = Guid.NewGuid();
        var targetId = Guid.NewGuid();
        var targetIdentifierId = Guid.NewGuid();
        var otherIdentifierId = Guid.NewGuid();
        var targetKeywordMessageId = Guid.NewGuid();
        var targetNonKeywordMessageId = Guid.NewGuid();
        var otherKeywordMessageId = Guid.NewGuid();
        var now = new DateTimeOffset(2026, 2, 13, 9, 0, 0, TimeSpan.Zero);

        await using (var db = await fixture.CreateDbContextAsync())
        {
            db.EvidenceItems.Add(new EvidenceItemRecord
            {
                EvidenceItemId = evidenceItemId,
                CaseId = caseInfo.CaseId,
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
                CaseId = caseInfo.CaseId,
                EvidenceItemId = evidenceItemId,
                Platform = "SMS",
                ThreadKey = "thread-target-filter",
                CreatedAtUtc = now,
                SourceLocator = "test:thread:target-filter",
                IngestModuleVersion = "test"
            });

            db.MessageEvents.AddRange(
                new MessageEventRecord
                {
                    MessageEventId = targetKeywordMessageId,
                    ThreadId = threadId,
                    CaseId = caseInfo.CaseId,
                    EvidenceItemId = evidenceItemId,
                    Platform = "SMS",
                    TimestampUtc = now,
                    Direction = "Incoming",
                    Sender = "+15551230001",
                    Recipients = "+15554440001",
                    Body = "checkpoint strap detail",
                    IsDeleted = false,
                    SourceLocator = "xlsx:test#Messages:R10",
                    IngestModuleVersion = "test"
                },
                new MessageEventRecord
                {
                    MessageEventId = targetNonKeywordMessageId,
                    ThreadId = threadId,
                    CaseId = caseInfo.CaseId,
                    EvidenceItemId = evidenceItemId,
                    Platform = "SMS",
                    TimestampUtc = now.AddMinutes(1),
                    Direction = "Incoming",
                    Sender = "+15551230001",
                    Recipients = "+15554440002",
                    Body = "routine ping",
                    IsDeleted = false,
                    SourceLocator = "xlsx:test#Messages:R11",
                    IngestModuleVersion = "test"
                },
                new MessageEventRecord
                {
                    MessageEventId = otherKeywordMessageId,
                    ThreadId = threadId,
                    CaseId = caseInfo.CaseId,
                    EvidenceItemId = evidenceItemId,
                    Platform = "SMS",
                    TimestampUtc = now.AddMinutes(2),
                    Direction = "Incoming",
                    Sender = "+15559990000",
                    Recipients = "+15554440003",
                    Body = "checkpoint strap detail",
                    IsDeleted = false,
                    SourceLocator = "xlsx:test#Messages:R12",
                    IngestModuleVersion = "test"
                }
            );

            db.Targets.Add(new TargetRecord
            {
                TargetId = targetId,
                CaseId = caseInfo.CaseId,
                DisplayName = "Target Alpha",
                PrimaryAlias = null,
                Notes = null,
                CreatedAtUtc = now,
                UpdatedAtUtc = now,
                SourceType = "Manual",
                SourceEvidenceItemId = null,
                SourceLocator = "manual:test:target-alpha",
                IngestModuleVersion = "test"
            });

            db.Identifiers.AddRange(
                new IdentifierRecord
                {
                    IdentifierId = targetIdentifierId,
                    CaseId = caseInfo.CaseId,
                    Type = "Phone",
                    ValueRaw = "+15551230001",
                    ValueNormalized = "+15551230001",
                    Notes = null,
                    CreatedAtUtc = now,
                    SourceType = "Manual",
                    SourceEvidenceItemId = null,
                    SourceLocator = "manual:test:target-phone",
                    IngestModuleVersion = "test"
                },
                new IdentifierRecord
                {
                    IdentifierId = otherIdentifierId,
                    CaseId = caseInfo.CaseId,
                    Type = "Phone",
                    ValueRaw = "+15559990000",
                    ValueNormalized = "+15559990000",
                    Notes = null,
                    CreatedAtUtc = now,
                    SourceType = "Manual",
                    SourceEvidenceItemId = null,
                    SourceLocator = "manual:test:other-phone",
                    IngestModuleVersion = "test"
                }
            );

            db.TargetIdentifierLinks.Add(new TargetIdentifierLinkRecord
            {
                LinkId = Guid.NewGuid(),
                CaseId = caseInfo.CaseId,
                TargetId = targetId,
                IdentifierId = targetIdentifierId,
                IsPrimary = true,
                CreatedAtUtc = now,
                SourceType = "Manual",
                SourceEvidenceItemId = null,
                SourceLocator = "manual:test:target-link",
                IngestModuleVersion = "test"
            });

            db.TargetMessagePresences.AddRange(
                new TargetMessagePresenceRecord
                {
                    PresenceId = Guid.NewGuid(),
                    CaseId = caseInfo.CaseId,
                    TargetId = targetId,
                    MessageEventId = targetKeywordMessageId,
                    MatchedIdentifierId = targetIdentifierId,
                    Role = "Sender",
                    EvidenceItemId = evidenceItemId,
                    SourceLocator = "xlsx:test#Messages:R10",
                    MessageTimestampUtc = now,
                    FirstSeenUtc = now,
                    LastSeenUtc = now
                },
                new TargetMessagePresenceRecord
                {
                    PresenceId = Guid.NewGuid(),
                    CaseId = caseInfo.CaseId,
                    TargetId = targetId,
                    MessageEventId = targetNonKeywordMessageId,
                    MatchedIdentifierId = targetIdentifierId,
                    Role = "Sender",
                    EvidenceItemId = evidenceItemId,
                    SourceLocator = "xlsx:test#Messages:R11",
                    MessageTimestampUtc = now.AddMinutes(1),
                    FirstSeenUtc = now.AddMinutes(1),
                    LastSeenUtc = now.AddMinutes(1)
                }
            );

            await db.SaveChangesAsync();
        }

        var targetOnlyHits = await search.SearchAsync(
            new MessageSearchRequest(
                caseInfo.CaseId,
                Query: null,
                PlatformFilter: null,
                SenderFilter: null,
                RecipientFilter: null,
                TargetId: targetId,
                IdentifierTypeFilter: null,
                DirectionFilter: MessageDirectionFilter.Any,
                FromUtc: null,
                ToUtc: null,
                Take: 20,
                Skip: 0
            ),
            CancellationToken.None
        );

        Assert.Equal(2, targetOnlyHits.Count);
        Assert.DoesNotContain(targetOnlyHits, hit => hit.MessageEventId == otherKeywordMessageId);

        var targetAndKeywordHits = await search.SearchAsync(
            new MessageSearchRequest(
                caseInfo.CaseId,
                Query: "checkpoint",
                PlatformFilter: null,
                SenderFilter: null,
                RecipientFilter: null,
                TargetId: targetId,
                IdentifierTypeFilter: null,
                DirectionFilter: MessageDirectionFilter.Any,
                FromUtc: null,
                ToUtc: null,
                Take: 20,
                Skip: 0
            ),
            CancellationToken.None
        );

        var single = Assert.Single(targetAndKeywordHits);
        Assert.Equal(targetKeywordMessageId, single.MessageEventId);
    }

    [Fact]
    public async Task SearchAsync_TargetFilter_RespectsDirectionFilter()
    {
        await using var fixture = await WorkspaceFixture.CreateAsync();
        var workspace = fixture.Services.GetRequiredService<ICaseWorkspaceService>();
        var search = fixture.Services.GetRequiredService<IMessageSearchService>();

        var caseInfo = await workspace.CreateCaseAsync("Target Direction Case", CancellationToken.None);
        var evidenceItemId = Guid.NewGuid();
        var threadId = Guid.NewGuid();
        var targetId = Guid.NewGuid();
        var identifierId = Guid.NewGuid();
        var incomingMessageId = Guid.NewGuid();
        var outgoingMessageId = Guid.NewGuid();
        var now = new DateTimeOffset(2026, 2, 13, 14, 0, 0, TimeSpan.Zero);

        await using (var db = await fixture.CreateDbContextAsync())
        {
            db.EvidenceItems.Add(new EvidenceItemRecord
            {
                EvidenceItemId = evidenceItemId,
                CaseId = caseInfo.CaseId,
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
                CaseId = caseInfo.CaseId,
                EvidenceItemId = evidenceItemId,
                Platform = "SMS",
                ThreadKey = "thread-direction",
                CreatedAtUtc = now,
                SourceLocator = "test:thread:direction",
                IngestModuleVersion = "test"
            });

            db.MessageEvents.AddRange(
                new MessageEventRecord
                {
                    MessageEventId = incomingMessageId,
                    ThreadId = threadId,
                    CaseId = caseInfo.CaseId,
                    EvidenceItemId = evidenceItemId,
                    Platform = "SMS",
                    TimestampUtc = now,
                    Direction = "Incoming",
                    Sender = "+15550001",
                    Recipients = "+15551230001",
                    Body = "direction check",
                    IsDeleted = false,
                    SourceLocator = "xlsx:test#Messages:R20",
                    IngestModuleVersion = "test"
                },
                new MessageEventRecord
                {
                    MessageEventId = outgoingMessageId,
                    ThreadId = threadId,
                    CaseId = caseInfo.CaseId,
                    EvidenceItemId = evidenceItemId,
                    Platform = "SMS",
                    TimestampUtc = now.AddMinutes(1),
                    Direction = "Outgoing",
                    Sender = "+15551230001",
                    Recipients = "+15550001",
                    Body = "direction check",
                    IsDeleted = false,
                    SourceLocator = "xlsx:test#Messages:R21",
                    IngestModuleVersion = "test"
                }
            );

            db.Targets.Add(new TargetRecord
            {
                TargetId = targetId,
                CaseId = caseInfo.CaseId,
                DisplayName = "Direction Target",
                PrimaryAlias = null,
                Notes = null,
                CreatedAtUtc = now,
                UpdatedAtUtc = now,
                SourceType = "Manual",
                SourceEvidenceItemId = null,
                SourceLocator = "manual:test:direction-target",
                IngestModuleVersion = "test"
            });

            db.Identifiers.Add(new IdentifierRecord
            {
                IdentifierId = identifierId,
                CaseId = caseInfo.CaseId,
                Type = "Phone",
                ValueRaw = "+15551230001",
                ValueNormalized = "+15551230001",
                Notes = null,
                CreatedAtUtc = now,
                SourceType = "Manual",
                SourceEvidenceItemId = null,
                SourceLocator = "manual:test:direction-id",
                IngestModuleVersion = "test"
            });

            db.TargetIdentifierLinks.Add(new TargetIdentifierLinkRecord
            {
                LinkId = Guid.NewGuid(),
                CaseId = caseInfo.CaseId,
                TargetId = targetId,
                IdentifierId = identifierId,
                IsPrimary = true,
                CreatedAtUtc = now,
                SourceType = "Manual",
                SourceEvidenceItemId = null,
                SourceLocator = "manual:test:direction-link",
                IngestModuleVersion = "test"
            });

            db.TargetMessagePresences.AddRange(
                new TargetMessagePresenceRecord
                {
                    PresenceId = Guid.NewGuid(),
                    CaseId = caseInfo.CaseId,
                    TargetId = targetId,
                    MessageEventId = incomingMessageId,
                    MatchedIdentifierId = identifierId,
                    Role = "Recipient",
                    EvidenceItemId = evidenceItemId,
                    SourceLocator = "xlsx:test#Messages:R20",
                    MessageTimestampUtc = now,
                    FirstSeenUtc = now,
                    LastSeenUtc = now
                },
                new TargetMessagePresenceRecord
                {
                    PresenceId = Guid.NewGuid(),
                    CaseId = caseInfo.CaseId,
                    TargetId = targetId,
                    MessageEventId = outgoingMessageId,
                    MatchedIdentifierId = identifierId,
                    Role = "Sender",
                    EvidenceItemId = evidenceItemId,
                    SourceLocator = "xlsx:test#Messages:R21",
                    MessageTimestampUtc = now.AddMinutes(1),
                    FirstSeenUtc = now.AddMinutes(1),
                    LastSeenUtc = now.AddMinutes(1)
                }
            );

            await db.SaveChangesAsync();
        }

        var incomingHits = await search.SearchAsync(
            new MessageSearchRequest(
                caseInfo.CaseId,
                Query: "direction",
                PlatformFilter: null,
                SenderFilter: null,
                RecipientFilter: null,
                TargetId: targetId,
                IdentifierTypeFilter: null,
                DirectionFilter: MessageDirectionFilter.Incoming,
                FromUtc: null,
                ToUtc: null,
                Take: 20,
                Skip: 0
            ),
            CancellationToken.None
        );

        var incoming = Assert.Single(incomingHits);
        Assert.Equal(incomingMessageId, incoming.MessageEventId);

        var outgoingHits = await search.SearchAsync(
            new MessageSearchRequest(
                caseInfo.CaseId,
                Query: "direction",
                PlatformFilter: null,
                SenderFilter: null,
                RecipientFilter: null,
                TargetId: targetId,
                IdentifierTypeFilter: null,
                DirectionFilter: MessageDirectionFilter.Outgoing,
                FromUtc: null,
                ToUtc: null,
                Take: 20,
                Skip: 0
            ),
            CancellationToken.None
        );

        var outgoing = Assert.Single(outgoingHits);
        Assert.Equal(outgoingMessageId, outgoing.MessageEventId);
    }

    [Fact]
    public async Task MessagesIngestJob_CreatesRows_AndAuditSummary()
    {
        await using var fixture = await WorkspaceFixture.CreateAsync(startRunner: false);
        var workspace = fixture.Services.GetRequiredService<ICaseWorkspaceService>();
        var vault = fixture.Services.GetRequiredService<IEvidenceVaultService>();
        var queue = fixture.Services.GetRequiredService<IJobQueueService>();
        var queueRunner = fixture.Services.GetRequiredService<JobQueueService>();

        var caseInfo = await workspace.CreateCaseAsync("Messages Job Case", CancellationToken.None);
        var sourceXlsx = fixture.CreateMessagesXlsx("messages-job.xlsx");
        var evidence = await vault.ImportEvidenceFileAsync(caseInfo, sourceXlsx, null, CancellationToken.None);

        var jobId = await queue.EnqueueAsync(
            new JobEnqueueRequest(
                JobQueueService.MessagesIngestJobType,
                caseInfo.CaseId,
                evidence.EvidenceItemId,
                JsonSerializer.Serialize(new
                {
                    SchemaVersion = 1,
                    caseInfo.CaseId,
                    evidence.EvidenceItemId
                })
            ),
            CancellationToken.None
        );

        await queueRunner.ExecuteAsync(jobId, CancellationToken.None);

        await using var db = await fixture.CreateDbContextAsync();
        var succeeded = await db.Jobs
            .AsNoTracking()
            .FirstAsync(j => j.JobId == jobId);

        Assert.Equal("Succeeded", succeeded.Status);
        Assert.Equal(1, succeeded.Progress);
        Assert.NotNull(succeeded.CompletedAtUtc);
        Assert.Equal("Succeeded: Extracted 2 message(s).", succeeded.StatusMessage);

        var events = await db.MessageEvents
            .Where(e => e.CaseId == caseInfo.CaseId && e.EvidenceItemId == evidence.EvidenceItemId)
            .ToListAsync();

        Assert.NotEmpty(events);
        Assert.All(events, e => Assert.False(string.IsNullOrWhiteSpace(e.SourceLocator)));
        Assert.All(events, e => Assert.False(string.IsNullOrWhiteSpace(e.IngestModuleVersion)));

        var summaryAudits = await db.AuditEvents
            .AsNoTracking()
            .Where(a =>
                a.CaseId == caseInfo.CaseId
                && a.EvidenceItemId == evidence.EvidenceItemId
                && a.ActionType == "MessagesIngested"
            )
            .ToListAsync();
        Assert.Single(summaryAudits);
    }

    [Fact]
    public async Task MessagesIngestJob_XlsxWithoutRecognizedSheets_ReportsGuidance()
    {
        await using var fixture = await WorkspaceFixture.CreateAsync(startRunner: true);
        var workspace = fixture.Services.GetRequiredService<ICaseWorkspaceService>();
        var vault = fixture.Services.GetRequiredService<IEvidenceVaultService>();
        var queue = fixture.Services.GetRequiredService<IJobQueueService>();

        var caseInfo = await workspace.CreateCaseAsync("No Sheet Case", CancellationToken.None);
        var sourceXlsx = fixture.CreateWorkbookWithoutMessageSheets("messages-nosheet.xlsx");
        var evidence = await vault.ImportEvidenceFileAsync(caseInfo, sourceXlsx, null, CancellationToken.None);

        var jobId = await queue.EnqueueAsync(
            new JobEnqueueRequest(
                JobQueueService.MessagesIngestJobType,
                caseInfo.CaseId,
                evidence.EvidenceItemId,
                JsonSerializer.Serialize(new
                {
                    SchemaVersion = 1,
                    caseInfo.CaseId,
                    evidence.EvidenceItemId
                })
            ),
            CancellationToken.None
        );

        var succeeded = await WaitForJobStatusAsync(
            fixture,
            jobId,
            status => status == "Succeeded",
            TimeSpan.FromSeconds(12)
        );

        Assert.Equal(1, succeeded.Progress);
        Assert.StartsWith(
            "Succeeded: No message sheets found; verify export settings.",
            succeeded.StatusMessage,
            StringComparison.Ordinal
        );
        Assert.DoesNotContain("Persisting parsed messages", succeeded.StatusMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task MessagesIngestJob_UfdrUnsupported_ReportsGuidance()
    {
        await using var fixture = await WorkspaceFixture.CreateAsync(startRunner: true);
        var workspace = fixture.Services.GetRequiredService<ICaseWorkspaceService>();
        var vault = fixture.Services.GetRequiredService<IEvidenceVaultService>();
        var queue = fixture.Services.GetRequiredService<IJobQueueService>();

        var caseInfo = await workspace.CreateCaseAsync("UFDR Unsupported Case", CancellationToken.None);
        var sourceUfdr = fixture.CreateUfdrArchive(
            "messages-unsupported.ufdr",
            ("messages/readme.txt", "unsupported format")
        );
        var evidence = await vault.ImportEvidenceFileAsync(caseInfo, sourceUfdr, null, CancellationToken.None);

        var jobId = await queue.EnqueueAsync(
            new JobEnqueueRequest(
                JobQueueService.MessagesIngestJobType,
                caseInfo.CaseId,
                evidence.EvidenceItemId,
                JsonSerializer.Serialize(new
                {
                    SchemaVersion = 1,
                    caseInfo.CaseId,
                    evidence.EvidenceItemId
                })
            ),
            CancellationToken.None
        );

        var succeeded = await WaitForJobStatusAsync(
            fixture,
            jobId,
            status => status == "Succeeded",
            TimeSpan.FromSeconds(12)
        );

        Assert.Equal(
            "Succeeded: UFDR message parsing not supported in this build. Generate a Cellebrite XLSX message export and import that.",
            succeeded.StatusMessage
        );
    }

    [Fact]
    public async Task MessagesIngestJob_CancelQueued_TransitionsToCanceled()
    {
        await using var fixture = await WorkspaceFixture.CreateAsync(startRunner: false);
        var workspace = fixture.Services.GetRequiredService<ICaseWorkspaceService>();
        var vault = fixture.Services.GetRequiredService<IEvidenceVaultService>();
        var queue = fixture.Services.GetRequiredService<IJobQueueService>();

        var caseInfo = await workspace.CreateCaseAsync("Cancel Queued Case", CancellationToken.None);
        var sourceXlsx = fixture.CreateMessagesXlsx("messages-cancel-queued.xlsx", messageCount: 400);
        var evidence = await vault.ImportEvidenceFileAsync(caseInfo, sourceXlsx, null, CancellationToken.None);

        var jobId = await queue.EnqueueAsync(
            new JobEnqueueRequest(
                JobQueueService.MessagesIngestJobType,
                caseInfo.CaseId,
                evidence.EvidenceItemId,
                JsonSerializer.Serialize(new
                {
                    SchemaVersion = 1,
                    caseInfo.CaseId,
                    evidence.EvidenceItemId
                })
            ),
            CancellationToken.None
        );

        await queue.CancelAsync(jobId, CancellationToken.None);

        await using var db = await fixture.CreateDbContextAsync();
        var record = await db.Jobs
            .AsNoTracking()
            .FirstAsync(j => j.JobId == jobId);

        Assert.Equal("Canceled", record.Status);
        Assert.Equal(1, record.Progress);
        Assert.NotNull(record.CompletedAtUtc);
        Assert.Equal("Canceled", record.StatusMessage);
    }

    [Fact]
    public async Task MessagesIngestJob_CancelRunning_TransitionsToCanceled()
    {
        await using var fixture = await WorkspaceFixture.CreateAsync(startRunner: true);
        var workspace = fixture.Services.GetRequiredService<ICaseWorkspaceService>();
        var vault = fixture.Services.GetRequiredService<IEvidenceVaultService>();
        var queue = fixture.Services.GetRequiredService<IJobQueueService>();

        var caseInfo = await workspace.CreateCaseAsync("Cancel Running Case", CancellationToken.None);
        var sourceXlsx = fixture.CreateMessagesXlsx("messages-cancel-running.xlsx", messageCount: 3000);
        var evidence = await vault.ImportEvidenceFileAsync(caseInfo, sourceXlsx, null, CancellationToken.None);

        var jobId = await queue.EnqueueAsync(
            new JobEnqueueRequest(
                JobQueueService.MessagesIngestJobType,
                caseInfo.CaseId,
                evidence.EvidenceItemId,
                JsonSerializer.Serialize(new
                {
                    SchemaVersion = 1,
                    caseInfo.CaseId,
                    evidence.EvidenceItemId
                })
            ),
            CancellationToken.None
        );

        await WaitForJobStatusAsync(
            fixture,
            jobId,
            status => status == "Running",
            TimeSpan.FromSeconds(12)
        );

        await queue.CancelAsync(jobId, CancellationToken.None);

        var canceled = await WaitForJobStatusAsync(
            fixture,
            jobId,
            status => status == "Canceled",
            TimeSpan.FromSeconds(12)
        );

        Assert.Equal("Canceled", canceled.Status);
        Assert.Equal(1, canceled.Progress);
        Assert.NotNull(canceled.CompletedAtUtc);
        Assert.Equal("Canceled", canceled.StatusMessage);
    }

    [Fact]
    public async Task MessagesIngestJob_ReportsProgressBeforeCompletion()
    {
        await using var fixture = await WorkspaceFixture.CreateAsync(startRunner: true);
        var workspace = fixture.Services.GetRequiredService<ICaseWorkspaceService>();
        var vault = fixture.Services.GetRequiredService<IEvidenceVaultService>();
        var queue = fixture.Services.GetRequiredService<IJobQueueService>();

        var caseInfo = await workspace.CreateCaseAsync("Progress Case", CancellationToken.None);
        var sourceXlsx = fixture.CreateMessagesXlsx("messages-progress.xlsx", messageCount: 900);
        var evidence = await vault.ImportEvidenceFileAsync(caseInfo, sourceXlsx, null, CancellationToken.None);

        var jobId = await queue.EnqueueAsync(
            new JobEnqueueRequest(
                JobQueueService.MessagesIngestJobType,
                caseInfo.CaseId,
                evidence.EvidenceItemId,
                JsonSerializer.Serialize(new
                {
                    SchemaVersion = 1,
                    caseInfo.CaseId,
                    evidence.EvidenceItemId
                })
            ),
            CancellationToken.None
        );

        var progressObserved = await WaitForJobRecordAsync(
            fixture,
            jobId,
            record =>
                record.Progress > 0
                && record.Progress < 1
                && record.StatusMessage.Contains("(")
                && record.StatusMessage.Contains("/"),
            TimeSpan.FromSeconds(12)
        );
        Assert.True(progressObserved.Progress > 0);
        Assert.Contains("(", progressObserved.StatusMessage);
        Assert.Contains("/", progressObserved.StatusMessage);

        var succeeded = await WaitForJobStatusAsync(
            fixture,
            jobId,
            status => status == "Succeeded",
            TimeSpan.FromSeconds(12)
        );
        Assert.Equal("Succeeded", succeeded.Status);
    }

    [Fact]
    public async Task IngestMessagesFromEvidenceAsync_IsIdempotent()
    {
        await using var fixture = await WorkspaceFixture.CreateAsync();
        var workspace = fixture.Services.GetRequiredService<ICaseWorkspaceService>();
        var vault = fixture.Services.GetRequiredService<IEvidenceVaultService>();
        var ingest = fixture.Services.GetRequiredService<IMessageIngestService>();

        var caseInfo = await workspace.CreateCaseAsync("Idempotent Case", CancellationToken.None);
        var sourceXlsx = fixture.CreateMessagesXlsx("messages-idempotent.xlsx");
        var imported = await vault.ImportEvidenceFileAsync(caseInfo, sourceXlsx, null, CancellationToken.None);

        EvidenceItemRecord evidenceRecord;
        await using (var db = await fixture.CreateDbContextAsync())
        {
            evidenceRecord = await db.EvidenceItems
                .AsNoTracking()
                .FirstAsync(e => e.EvidenceItemId == imported.EvidenceItemId);
        }

        var first = await ingest.IngestMessagesFromEvidenceAsync(
            caseInfo.CaseId,
            evidenceRecord,
            progress: null,
            CancellationToken.None
        );
        var second = await ingest.IngestMessagesFromEvidenceAsync(
            caseInfo.CaseId,
            evidenceRecord,
            progress: null,
            CancellationToken.None
        );

        Assert.True(first > 0);
        Assert.Equal(first, second);

        await using var verifyDb = await fixture.CreateDbContextAsync();
        var totalRows = await verifyDb.MessageEvents
            .CountAsync(e => e.CaseId == caseInfo.CaseId && e.EvidenceItemId == imported.EvidenceItemId);
        Assert.Equal(first, totalRows);
    }

    private static async Task<JobRecord> WaitForJobStatusAsync(
        WorkspaceFixture fixture,
        Guid jobId,
        Func<string, bool> statusPredicate,
        TimeSpan timeout
    )
    {
        var timer = Stopwatch.StartNew();
        while (timer.Elapsed < timeout)
        {
            await using var db = await fixture.CreateDbContextAsync();
            var record = await db.Jobs
                .AsNoTracking()
                .FirstOrDefaultAsync(job => job.JobId == jobId);

            if (record is not null && statusPredicate(record.Status))
            {
                return record;
            }

            await Task.Delay(50);
        }

        throw new TimeoutException($"Job {jobId:D} did not reach target status in time.");
    }

    private static async Task<JobRecord> WaitForJobRecordAsync(
        WorkspaceFixture fixture,
        Guid jobId,
        Func<JobRecord, bool> recordPredicate,
        TimeSpan timeout
    )
    {
        var timer = Stopwatch.StartNew();
        while (timer.Elapsed < timeout)
        {
            await using var db = await fixture.CreateDbContextAsync();
            var record = await db.Jobs
                .AsNoTracking()
                .FirstOrDefaultAsync(job => job.JobId == jobId);

            if (record is not null && recordPredicate(record))
            {
                return record;
            }

            await Task.Delay(50);
        }

        throw new TimeoutException($"Job {jobId:D} did not report expected progress in time.");
    }

    private static async Task<AuditEventRecord?> WaitForAuditEventAsync(
        WorkspaceFixture fixture,
        Guid caseId,
        Guid evidenceItemId,
        string actionType,
        TimeSpan timeout
    )
    {
        var timer = Stopwatch.StartNew();
        while (timer.Elapsed < timeout)
        {
            await using var db = await fixture.CreateDbContextAsync();
            var record = (await db.AuditEvents
                .AsNoTracking()
                .Where(a => a.CaseId == caseId && a.EvidenceItemId == evidenceItemId && a.ActionType == actionType)
                .ToListAsync())
                .OrderByDescending(a => a.TimestampUtc)
                .FirstOrDefault();

            if (record is not null)
            {
                return record;
            }

            await Task.Delay(50);
        }

        return null;
    }

    private sealed class WorkspaceFixture : IAsyncDisposable
    {
        private readonly ServiceProvider _provider;
        private readonly TestWorkspacePathProvider _pathProvider;
        private readonly JobRunnerHostedService? _jobRunner;

        private WorkspaceFixture(
            ServiceProvider provider,
            TestWorkspacePathProvider pathProvider,
            JobRunnerHostedService? jobRunner
        )
        {
            _provider = provider;
            _pathProvider = pathProvider;
            _jobRunner = jobRunner;
        }

        public IServiceProvider Services => _provider;

        public static async Task<WorkspaceFixture> CreateAsync(bool startRunner = false)
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
                new FixedClock(new DateTimeOffset(2026, 2, 13, 12, 0, 0, TimeSpan.Zero))
            );
            services.AddSingleton<IWorkspacePathProvider>(pathProvider);
            services.AddDbContextFactory<WorkspaceDbContext>(options =>
            {
                Directory.CreateDirectory(pathProvider.WorkspaceRoot);
                options.UseSqlite($"Data Source={pathProvider.WorkspaceDbPath}");
            });
            services.AddSingleton<WorkspaceDbRebuilder>();
            services.AddSingleton<WorkspaceDbInitializer>();
            services.AddSingleton<IWorkspaceDbInitializer>(provider => provider.GetRequiredService<WorkspaceDbInitializer>());
            services.AddSingleton<IWorkspaceDatabaseInitializer>(provider => provider.GetRequiredService<WorkspaceDbInitializer>());
            services.AddSingleton<IWorkspaceWriteGate, WorkspaceWriteGate>();
            services.AddSingleton<IAuditLogService, AuditLogService>();
            services.AddSingleton<ICaseWorkspaceService, CaseWorkspaceService>();
            services.AddSingleton<IEvidenceVaultService, EvidenceVaultService>();
            services.AddSingleton<IMessageSearchService, MessageSearchService>();
            services.AddSingleton<IMessageIngestService, MessageIngestService>();
            services.AddSingleton<ITargetMessagePresenceIndexService, TargetMessagePresenceIndexService>();
            services.AddSingleton<IJobQueryService, JobQueryService>();
            services.AddSingleton<JobQueueService>();
            services.AddSingleton<IJobQueueService>(provider => provider.GetRequiredService<JobQueueService>());

            var provider = services.BuildServiceProvider();

            var initializer = provider.GetRequiredService<IWorkspaceDatabaseInitializer>();
            await initializer.EnsureInitializedAsync(CancellationToken.None);

            JobRunnerHostedService? runner = null;
            if (startRunner)
            {
                runner = ActivatorUtilities.CreateInstance<JobRunnerHostedService>(provider);
                await runner.StartAsync(CancellationToken.None);
            }

            return new WorkspaceFixture(provider, pathProvider, runner);
        }

        public string CreateMessagesXlsx(string fileName, int messageCount = 2)
        {
            var sourceDirectory = Path.Combine(_pathProvider.WorkspaceRoot, "source");
            Directory.CreateDirectory(sourceDirectory);

            var path = Path.Combine(sourceDirectory, fileName);
            using var doc = SpreadsheetDocument.Create(path, SpreadsheetDocumentType.Workbook);
            var workbookPart = doc.AddWorkbookPart();
            workbookPart.Workbook = new Workbook();

            var worksheetPart = workbookPart.AddNewPart<WorksheetPart>();
            var sheetData = new SheetData();
            worksheetPart.Worksheet = new Worksheet(sheetData);

            sheetData.Append(new Row(
                Cell("A1", "Timestamp"),
                Cell("B1", "Direction"),
                Cell("C1", "Sender"),
                Cell("D1", "Recipients"),
                Cell("E1", "Body"),
                Cell("F1", "Deleted"),
                Cell("G1", "ThreadId"),
                Cell("H1", "Platform")
            ));
            for (var row = 0; row < messageCount; row++)
            {
                var rowNumber = row + 2;
                var incoming = row % 2 == 0;
                var sender = incoming ? "+15550001" : "+15550002";
                var recipient = incoming ? "+15550002" : "+15550001";
                var body = incoming
                    ? $"Meet me at checkpoint {row + 1}."
                    : $"Bring evidence folder {row + 1}.";

                sheetData.Append(new Row(
                    Cell($"A{rowNumber}", $"2026-02-13T12:{(row % 60):00}:00Z"),
                    Cell($"B{rowNumber}", incoming ? "Incoming" : "Outgoing"),
                    Cell($"C{rowNumber}", sender),
                    Cell($"D{rowNumber}", recipient),
                    Cell($"E{rowNumber}", body),
                    Cell($"F{rowNumber}", "false"),
                    Cell($"G{rowNumber}", "thread-alpha"),
                    Cell($"H{rowNumber}", "SMS")
                ));
            }

            var sheets = workbookPart.Workbook.AppendChild(new Sheets());
            sheets.Append(new Sheet
            {
                Id = workbookPart.GetIdOfPart(worksheetPart),
                SheetId = 1,
                Name = "Messages"
            });
            workbookPart.Workbook.Save();
            return path;
        }

        public string CreateWorkbookWithoutMessageSheets(string fileName)
        {
            var sourceDirectory = Path.Combine(_pathProvider.WorkspaceRoot, "source");
            Directory.CreateDirectory(sourceDirectory);

            var path = Path.Combine(sourceDirectory, fileName);
            using var doc = SpreadsheetDocument.Create(path, SpreadsheetDocumentType.Workbook);
            var workbookPart = doc.AddWorkbookPart();
            workbookPart.Workbook = new Workbook();

            var worksheetPart = workbookPart.AddNewPart<WorksheetPart>();
            var sheetData = new SheetData();
            worksheetPart.Worksheet = new Worksheet(sheetData);

            sheetData.Append(new Row(Cell("A1", "Irrelevant")));
            sheetData.Append(new Row(Cell("A2", "No messages here")));

            var sheets = workbookPart.Workbook.AppendChild(new Sheets());
            sheets.Append(new Sheet
            {
                Id = workbookPart.GetIdOfPart(worksheetPart),
                SheetId = 1,
                Name = "Contacts"
            });
            workbookPart.Workbook.Save();
            return path;
        }

        public string CreateUfdrArchive(string fileName, params (string entryPath, string content)[] entries)
        {
            var sourceDirectory = Path.Combine(_pathProvider.WorkspaceRoot, "source");
            Directory.CreateDirectory(sourceDirectory);

            var path = Path.Combine(sourceDirectory, fileName);
            using (var archive = ZipFile.Open(path, ZipArchiveMode.Create))
            {
                foreach (var (entryPath, content) in entries)
                {
                    var entry = archive.CreateEntry(entryPath, CompressionLevel.Optimal);
                    using var writer = new StreamWriter(entry.Open(), Encoding.UTF8);
                    writer.Write(content);
                }
            }

            return path;
        }

        private static Cell Cell(string reference, string value)
        {
            return new Cell
            {
                CellReference = reference,
                DataType = CellValues.InlineString,
                InlineString = new InlineString(new Text(value))
            };
        }

        public Task<WorkspaceDbContext> CreateDbContextAsync()
        {
            var factory = _provider.GetRequiredService<IDbContextFactory<WorkspaceDbContext>>();
            return factory.CreateDbContextAsync(CancellationToken.None);
        }

        public async ValueTask DisposeAsync()
        {
            if (_jobRunner is not null)
            {
                using var stopCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                await _jobRunner.StopAsync(stopCts.Token);
                _jobRunner.Dispose();
            }

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
