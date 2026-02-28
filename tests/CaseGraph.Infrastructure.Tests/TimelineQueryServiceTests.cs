using CaseGraph.Core.Abstractions;
using CaseGraph.Core.Models;
using CaseGraph.Infrastructure.Persistence;
using CaseGraph.Infrastructure.Persistence.Entities;
using CaseGraph.Infrastructure.Services;
using CaseGraph.Infrastructure.Timeline;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using System.Text.Json;

namespace CaseGraph.Infrastructure.Tests;

public sealed class TimelineQueryServiceTests
{
    [Fact]
    public async Task SearchAsync_OrdersByTimestampDescending_WithNullsLast()
    {
        await using var fixture = await WorkspaceFixture.CreateAsync();
        var service = fixture.Services.GetRequiredService<TimelineQueryService>();

        var seeded = await fixture.SeedCaseWithEvidenceAsync("Timeline Order Case");
        var t1 = new DateTimeOffset(2026, 2, 27, 10, 0, 0, TimeSpan.Zero);
        var t2 = t1.AddMinutes(3);

        await fixture.SeedMessagesAsync(
            seeded.CaseId,
            seeded.EvidenceItemId,
            seeded.ThreadId,
            [
                CreateMessage(seeded.CaseId, seeded.EvidenceItemId, seeded.ThreadId, t1, "xlsx:test#Messages:R1"),
                CreateMessage(seeded.CaseId, seeded.EvidenceItemId, seeded.ThreadId, t2, "xlsx:test#Messages:R2"),
                CreateMessage(seeded.CaseId, seeded.EvidenceItemId, seeded.ThreadId, null, "xlsx:test#Messages:R3")
            ]
        );

        var page = await service.SearchAsync(
            new TimelineQueryRequest(
                seeded.CaseId,
                QueryText: null,
                TargetId: null,
                GlobalEntityId: null,
                Direction: null,
                FromUtc: null,
                ToUtc: null,
                Take: 20,
                Skip: 0,
                CorrelationId: "order-test"
            ),
            CancellationToken.None
        );

        Assert.Equal(3, page.TotalCount);
        Assert.Equal("xlsx:test#Messages:R2", page.Rows[0].SourceLocator);
        Assert.Equal("xlsx:test#Messages:R1", page.Rows[1].SourceLocator);
        Assert.Equal("xlsx:test#Messages:R3", page.Rows[2].SourceLocator);
    }

    [Fact]
    public async Task SearchAsync_DateRange_IsInclusive()
    {
        await using var fixture = await WorkspaceFixture.CreateAsync();
        var service = fixture.Services.GetRequiredService<TimelineQueryService>();

        var seeded = await fixture.SeedCaseWithEvidenceAsync("Timeline Date Case");
        var t1 = new DateTimeOffset(2026, 2, 27, 10, 0, 0, TimeSpan.Zero);
        var t2 = t1.AddHours(1);
        var t3 = t2.AddHours(1);

        await fixture.SeedMessagesAsync(
            seeded.CaseId,
            seeded.EvidenceItemId,
            seeded.ThreadId,
            [
                CreateMessage(seeded.CaseId, seeded.EvidenceItemId, seeded.ThreadId, t1, "xlsx:test#Messages:R10"),
                CreateMessage(seeded.CaseId, seeded.EvidenceItemId, seeded.ThreadId, t2, "xlsx:test#Messages:R11"),
                CreateMessage(seeded.CaseId, seeded.EvidenceItemId, seeded.ThreadId, t3, "xlsx:test#Messages:R12")
            ]
        );

        var page = await service.SearchAsync(
            new TimelineQueryRequest(
                seeded.CaseId,
                QueryText: null,
                TargetId: null,
                GlobalEntityId: null,
                Direction: null,
                FromUtc: t1,
                ToUtc: t2,
                Take: 20,
                Skip: 0,
                CorrelationId: "date-test"
            ),
            CancellationToken.None
        );

        Assert.Equal(2, page.TotalCount);
        Assert.Contains(page.Rows, row => row.SourceLocator == "xlsx:test#Messages:R10");
        Assert.Contains(page.Rows, row => row.SourceLocator == "xlsx:test#Messages:R11");
        Assert.DoesNotContain(page.Rows, row => row.SourceLocator == "xlsx:test#Messages:R12");
    }

    [Fact]
    public async Task SearchAsync_TargetAndGlobalPersonFilters_ResolveParticipantDisplay()
    {
        await using var fixture = await WorkspaceFixture.CreateAsync();
        var service = fixture.Services.GetRequiredService<TimelineQueryService>();

        var seeded = await fixture.SeedCaseWithEvidenceAsync("Timeline Filter Case");
        var globalEntityId = Guid.NewGuid();
        var alphaTargetId = Guid.NewGuid();
        var bravoTargetId = Guid.NewGuid();
        var alphaMessageId = Guid.NewGuid();
        var bravoMessageId = Guid.NewGuid();
        var alphaTime = new DateTimeOffset(2026, 2, 27, 12, 0, 0, TimeSpan.Zero);
        var bravoTime = alphaTime.AddMinutes(5);

        await using (var db = await fixture.CreateDbContextAsync())
        {
            var alphaIdentifierId = Guid.NewGuid();
            var bravoIdentifierId = Guid.NewGuid();

            db.PersonEntities.Add(new PersonEntityRecord
            {
                GlobalEntityId = globalEntityId,
                DisplayName = "Atlas Group"
            });

            db.Targets.AddRange(
                new TargetRecord
                {
                    TargetId = alphaTargetId,
                    CaseId = seeded.CaseId,
                    GlobalEntityId = globalEntityId,
                    DisplayName = "Alpha Target",
                    PrimaryAlias = null,
                    Notes = null,
                    CreatedAtUtc = alphaTime,
                    UpdatedAtUtc = alphaTime,
                    SourceType = "Manual",
                    SourceEvidenceItemId = null,
                    SourceLocator = "manual:test:alpha",
                    IngestModuleVersion = "test"
                },
                new TargetRecord
                {
                    TargetId = bravoTargetId,
                    CaseId = seeded.CaseId,
                    GlobalEntityId = null,
                    DisplayName = "Bravo Target",
                    PrimaryAlias = null,
                    Notes = null,
                    CreatedAtUtc = alphaTime,
                    UpdatedAtUtc = alphaTime,
                    SourceType = "Manual",
                    SourceEvidenceItemId = null,
                    SourceLocator = "manual:test:bravo",
                    IngestModuleVersion = "test"
                }
            );

            db.Identifiers.AddRange(
                new IdentifierRecord
                {
                    IdentifierId = alphaIdentifierId,
                    CaseId = seeded.CaseId,
                    Type = "Phone",
                    ValueRaw = "+15550001",
                    ValueNormalized = "+15550001",
                    Notes = null,
                    CreatedAtUtc = alphaTime,
                    SourceType = "Manual",
                    SourceEvidenceItemId = null,
                    SourceLocator = "manual:test:id-alpha",
                    IngestModuleVersion = "test"
                },
                new IdentifierRecord
                {
                    IdentifierId = bravoIdentifierId,
                    CaseId = seeded.CaseId,
                    Type = "Phone",
                    ValueRaw = "+15550003",
                    ValueNormalized = "+15550003",
                    Notes = null,
                    CreatedAtUtc = bravoTime,
                    SourceType = "Manual",
                    SourceEvidenceItemId = null,
                    SourceLocator = "manual:test:id-bravo",
                    IngestModuleVersion = "test"
                }
            );

            db.MessageEvents.AddRange(
                CreateMessage(
                    seeded.CaseId,
                    seeded.EvidenceItemId,
                    seeded.ThreadId,
                    alphaTime,
                    "xlsx:test#Messages:R20",
                    messageEventId: alphaMessageId,
                    sender: "+15550001",
                    recipients: "+15550002"
                ),
                CreateMessage(
                    seeded.CaseId,
                    seeded.EvidenceItemId,
                    seeded.ThreadId,
                    bravoTime,
                    "xlsx:test#Messages:R21",
                    messageEventId: bravoMessageId,
                    sender: "+15550003",
                    recipients: "+15550004"
                )
            );

            db.TargetMessagePresences.AddRange(
                new TargetMessagePresenceRecord
                {
                    PresenceId = Guid.NewGuid(),
                    CaseId = seeded.CaseId,
                    TargetId = alphaTargetId,
                    MessageEventId = alphaMessageId,
                    MatchedIdentifierId = alphaIdentifierId,
                    Role = "Sender",
                    EvidenceItemId = seeded.EvidenceItemId,
                    SourceLocator = "xlsx:test#Messages:R20",
                    MessageTimestampUtc = alphaTime,
                    FirstSeenUtc = alphaTime,
                    LastSeenUtc = alphaTime
                },
                new TargetMessagePresenceRecord
                {
                    PresenceId = Guid.NewGuid(),
                    CaseId = seeded.CaseId,
                    TargetId = bravoTargetId,
                    MessageEventId = bravoMessageId,
                    MatchedIdentifierId = bravoIdentifierId,
                    Role = "Sender",
                    EvidenceItemId = seeded.EvidenceItemId,
                    SourceLocator = "xlsx:test#Messages:R21",
                    MessageTimestampUtc = bravoTime,
                    FirstSeenUtc = bravoTime,
                    LastSeenUtc = bravoTime
                }
            );

            await db.SaveChangesAsync();
        }

        var targetFiltered = await service.SearchAsync(
            new TimelineQueryRequest(
                seeded.CaseId,
                QueryText: null,
                TargetId: alphaTargetId,
                GlobalEntityId: null,
                Direction: null,
                FromUtc: null,
                ToUtc: null,
                Take: 20,
                Skip: 0,
                CorrelationId: "target-filter"
            ),
            CancellationToken.None
        );

        var targetRow = Assert.Single(targetFiltered.Rows);
        Assert.Equal(alphaMessageId, targetRow.MessageEventId);
        Assert.Contains("Atlas Group", targetRow.ParticipantsSummary);

        var globalFiltered = await service.SearchAsync(
            new TimelineQueryRequest(
                seeded.CaseId,
                QueryText: null,
                TargetId: null,
                GlobalEntityId: globalEntityId,
                Direction: null,
                FromUtc: null,
                ToUtc: null,
                Take: 20,
                Skip: 0,
                CorrelationId: "global-filter"
            ),
            CancellationToken.None
        );

        var globalRow = Assert.Single(globalFiltered.Rows);
        Assert.Equal(alphaMessageId, globalRow.MessageEventId);
        Assert.Equal("Atlas Group", globalRow.SenderDisplay);
    }

    [Fact]
    public async Task SearchAsync_WritesAuditEventWithFiltersAndCorrelationId()
    {
        await using var fixture = await WorkspaceFixture.CreateAsync();
        var service = fixture.Services.GetRequiredService<TimelineQueryService>();

        var seeded = await fixture.SeedCaseWithEvidenceAsync("Timeline Audit Case");
        var timestamp = new DateTimeOffset(2026, 2, 27, 18, 0, 0, TimeSpan.Zero);
        await fixture.SeedMessagesAsync(
            seeded.CaseId,
            seeded.EvidenceItemId,
            seeded.ThreadId,
            [CreateMessage(seeded.CaseId, seeded.EvidenceItemId, seeded.ThreadId, timestamp, "xlsx:test#Messages:R30")]
        );

        var correlationId = "timeline-audit-correlation";
        var page = await service.SearchAsync(
            new TimelineQueryRequest(
                seeded.CaseId,
                QueryText: "checkpoint",
                TargetId: null,
                GlobalEntityId: null,
                Direction: "Incoming",
                FromUtc: new DateTimeOffset(timestamp.UtcDateTime.Date, TimeSpan.Zero),
                ToUtc: new DateTimeOffset(
                    timestamp.UtcDateTime.Date.AddDays(1).AddTicks(-1),
                    TimeSpan.Zero
                ),
                Take: 20,
                Skip: 0,
                CorrelationId: correlationId
            ),
            CancellationToken.None
        );

        Assert.Equal(1, page.TotalCount);

        await using var db = await fixture.CreateDbContextAsync();
        var audit = (await db.AuditEvents
            .AsNoTracking()
            .Where(item => item.ActionType == "TimelineSearchExecuted")
            .ToListAsync())
            .OrderByDescending(item => item.TimestampUtc)
            .FirstOrDefault();

        Assert.NotNull(audit);
        Assert.Equal(seeded.CaseId, audit.CaseId);
        Assert.Contains("returned 1 row", audit.Summary, StringComparison.OrdinalIgnoreCase);

        Assert.False(string.IsNullOrWhiteSpace(audit.JsonPayload));
        using var payload = JsonDocument.Parse(audit.JsonPayload);
        Assert.Equal(correlationId, payload.RootElement.GetProperty("CorrelationId").GetString());
        Assert.Equal("checkpoint", payload.RootElement.GetProperty("QueryText").GetString());
        Assert.Equal(1, payload.RootElement.GetProperty("ResultCount").GetInt32());
    }

    private static MessageEventRecord CreateMessage(
        Guid caseId,
        Guid evidenceItemId,
        Guid threadId,
        DateTimeOffset? timestampUtc,
        string sourceLocator,
        Guid? messageEventId = null,
        string sender = "+15550000",
        string recipients = "+15559999"
    )
    {
        return new MessageEventRecord
        {
            MessageEventId = messageEventId ?? Guid.NewGuid(),
            ThreadId = threadId,
            CaseId = caseId,
            EvidenceItemId = evidenceItemId,
            Platform = "SMS",
            TimestampUtc = timestampUtc,
            Direction = "Incoming",
            Sender = sender,
            Recipients = recipients,
            Body = "checkpoint update",
            IsDeleted = false,
            SourceLocator = sourceLocator,
            IngestModuleVersion = "test"
        };
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
                new FixedClock(new DateTimeOffset(2026, 2, 27, 12, 0, 0, TimeSpan.Zero))
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
            services.AddSingleton<TimelineQueryService>();

            var provider = services.BuildServiceProvider();

            var initializer = provider.GetRequiredService<IWorkspaceDatabaseInitializer>();
            await initializer.EnsureInitializedAsync(CancellationToken.None);

            return new WorkspaceFixture(provider, pathProvider);
        }

        public async Task<(Guid CaseId, Guid EvidenceItemId, Guid ThreadId)> SeedCaseWithEvidenceAsync(
            string caseName
        )
        {
            var caseId = Guid.NewGuid();
            var evidenceItemId = Guid.NewGuid();
            var threadId = Guid.NewGuid();
            var createdAt = new DateTimeOffset(2026, 2, 27, 9, 0, 0, TimeSpan.Zero);

            await using var db = await CreateDbContextAsync();
            db.Cases.Add(new CaseRecord
            {
                CaseId = caseId,
                Name = caseName,
                CreatedAtUtc = createdAt,
                LastOpenedAtUtc = createdAt
            });

            db.EvidenceItems.Add(new EvidenceItemRecord
            {
                EvidenceItemId = evidenceItemId,
                CaseId = caseId,
                DisplayName = "Synthetic",
                OriginalPath = "synthetic",
                OriginalFileName = "synthetic.txt",
                AddedAtUtc = createdAt,
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
                ThreadKey = "thread-alpha",
                CreatedAtUtc = createdAt,
                SourceLocator = "test:thread:alpha",
                IngestModuleVersion = "test"
            });

            await db.SaveChangesAsync();
            return (caseId, evidenceItemId, threadId);
        }

        public async Task SeedMessagesAsync(
            Guid caseId,
            Guid evidenceItemId,
            Guid threadId,
            IReadOnlyList<MessageEventRecord> messages
        )
        {
            await using var db = await CreateDbContextAsync();
            foreach (var message in messages)
            {
                db.MessageEvents.Add(message);
            }

            await db.SaveChangesAsync();
        }

        public Task<WorkspaceDbContext> CreateDbContextAsync()
        {
            var factory = _provider.GetRequiredService<IDbContextFactory<WorkspaceDbContext>>();
            return factory.CreateDbContextAsync(CancellationToken.None);
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
