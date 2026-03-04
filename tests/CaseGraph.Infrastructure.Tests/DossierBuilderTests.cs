using CaseGraph.Core.Abstractions;
using CaseGraph.Core.Models;
using CaseGraph.Infrastructure.Persistence;
using CaseGraph.Infrastructure.Persistence.Entities;
using CaseGraph.Infrastructure.Reports;
using CaseGraph.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using System.Text.Json;

namespace CaseGraph.Infrastructure.Tests;

public sealed class DossierBuilderTests
{
    [Fact]
    public async Task BuildAsync_AppliesSectionTogglesAndDateRange()
    {
        await using var fixture = await ReportFixture.CreateAsync();
        var seeded = await fixture.SeedTargetScenarioAsync();
        var builder = fixture.Services.GetRequiredService<DossierBuilder>();

        var report = await builder.BuildAsync(
            new DossierBuildRequest(
                seeded.CaseId,
                DossierSubjectTypes.Target,
                seeded.TargetId,
                seeded.SecondTimestampUtc,
                seeded.ThirdTimestampUtc,
                new DossierSectionSelection(
                    IncludeSubjectIdentifiers: false,
                    IncludeWhereSeenSummary: true,
                    IncludeTimelineExcerpt: true,
                    IncludeNotableMessageExcerpts: false,
                    IncludeAppendix: true
                ),
                Operator: "tester"
            ),
            CancellationToken.None
        );

        Assert.Null(report.SubjectIdentifiers);
        Assert.NotNull(report.WhereSeenSummary);
        Assert.Equal(2, report.WhereSeenSummary!.TotalMessages);
        Assert.Equal(seeded.SecondTimestampUtc, report.WhereSeenSummary.FirstSeenUtc);
        Assert.Equal(seeded.ThirdTimestampUtc, report.WhereSeenSummary.LastSeenUtc);

        Assert.NotNull(report.TimelineExcerpt);
        Assert.Equal(2, report.TimelineExcerpt!.Entries.Count);
        Assert.All(
            report.TimelineExcerpt.Entries,
            entry => Assert.InRange(entry.TimestampUtc!.Value, seeded.SecondTimestampUtc, seeded.ThirdTimestampUtc)
        );

        Assert.Null(report.NotableExcerpts);
        Assert.NotEmpty(report.AppendixCitations);
    }

    [Fact]
    public async Task BuildAsync_IncludesIdentifierDecisionIdsAndExcerptCitations()
    {
        await using var fixture = await ReportFixture.CreateAsync();
        var seeded = await fixture.SeedTargetScenarioAsync();
        var builder = fixture.Services.GetRequiredService<DossierBuilder>();

        var report = await builder.BuildAsync(
            new DossierBuildRequest(
                seeded.CaseId,
                DossierSubjectTypes.Target,
                seeded.TargetId,
                FromUtc: null,
                ToUtc: null,
                Sections: new DossierSectionSelection(),
                Operator: "tester"
            ),
            CancellationToken.None
        );

        var identifierEntry = Assert.Single(report.SubjectIdentifiers!.Entries);
        Assert.Contains(seeded.IdentifierLinkAuditId.ToString("D"), identifierEntry.DecisionIds);
        Assert.NotEmpty(identifierEntry.Citations);

        Assert.NotNull(report.NotableExcerpts);
        Assert.NotEmpty(report.NotableExcerpts!.Entries);
        Assert.All(report.NotableExcerpts.Entries, entry => Assert.NotEmpty(entry.Citations));
        Assert.Contains(
            report.AppendixCitations,
            citation => citation.MessageEventId == seeded.SecondMessageEventId
                && citation.SourceLocator == "xlsx:test#Messages:R2"
        );
    }

    [Fact]
    public async Task ReportExportJob_CreatesHtmlAndWritesReportExportedAudit()
    {
        await using var fixture = await ReportFixture.CreateAsync();
        var seeded = await fixture.SeedTargetScenarioAsync();
        var queue = fixture.Services.GetRequiredService<IJobQueueService>();
        var runner = fixture.Services.GetRequiredService<JobQueueService>();

        var requestedOutputPath = Path.Combine(
            fixture.PathProvider.WorkspaceRoot,
            "exports",
            "Alpha:Target?.html"
        );

        var payload = new DossierExportJobPayload(
            SchemaVersion: 1,
            CaseId: seeded.CaseId,
            SubjectType: DossierSubjectTypes.Target,
            SubjectId: seeded.TargetId,
            FromUtc: null,
            ToUtc: null,
            Sections: new DossierSectionSelection(),
            OutputPath: requestedOutputPath,
            RequestedBy: "tester"
        );

        var jobId = await queue.EnqueueAsync(
            new JobEnqueueRequest(
                JobQueueService.ReportExportJobType,
                seeded.CaseId,
                EvidenceItemId: null,
                JsonPayload: JsonSerializer.Serialize(payload)
            ),
            CancellationToken.None
        );

        await runner.ExecuteAsync(jobId, CancellationToken.None);

        var expectedOutputPath = Path.Combine(
            fixture.PathProvider.WorkspaceRoot,
            "exports",
            "Alpha_Target_.html"
        );

        Assert.True(File.Exists(expectedOutputPath));

        var html = await File.ReadAllTextAsync(expectedOutputPath, CancellationToken.None);
        Assert.Contains("Alpha Target Dossier", html, StringComparison.Ordinal);
        Assert.Contains("xlsx:test#Messages:R2", html, StringComparison.Ordinal);
        Assert.Contains(seeded.SecondMessageEventId.ToString("D"), html, StringComparison.Ordinal);

        await using var db = await fixture.CreateDbContextAsync();
        var job = await db.Jobs
            .AsNoTracking()
            .FirstAsync(record => record.JobId == jobId);
        Assert.Equal(nameof(JobStatus.Succeeded), job.Status);

        var audit = (await db.AuditEvents
            .AsNoTracking()
            .Where(record => record.CaseId == seeded.CaseId && record.ActionType == "ReportExported")
            .ToListAsync())
            .OrderByDescending(record => record.TimestampUtc)
            .FirstOrDefault();
        Assert.NotNull(audit);
        Assert.Contains(expectedOutputPath, audit!.Summary, StringComparison.OrdinalIgnoreCase);

        using var payloadDocument = JsonDocument.Parse(audit.JsonPayload!);
        Assert.Equal(expectedOutputPath, payloadDocument.RootElement.GetProperty("OutputPath").GetString());
        Assert.Equal("Target", payloadDocument.RootElement.GetProperty("SubjectType").GetString());
        Assert.Equal(seeded.TargetId.ToString("D"), payloadDocument.RootElement.GetProperty("SubjectId").GetGuid().ToString("D"));
    }

    private sealed class ReportFixture : IAsyncDisposable
    {
        private readonly ServiceProvider _provider;

        private ReportFixture(ServiceProvider provider, TestWorkspacePathProvider pathProvider)
        {
            _provider = provider;
            PathProvider = pathProvider;
        }

        public IServiceProvider Services => _provider;

        public TestWorkspacePathProvider PathProvider { get; }

        public static async Task<ReportFixture> CreateAsync()
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
                new FixedClock(new DateTimeOffset(2026, 3, 1, 12, 0, 0, TimeSpan.Zero))
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
            services.AddSingleton<IJobQueryService, JobQueryService>();
            services.AddSingleton<DossierBuilder>();
            services.AddSingleton<ReportExportService>();
            services.AddSingleton<ICaseWorkspaceService, NoOpCaseWorkspaceService>();
            services.AddSingleton<IEvidenceVaultService, NoOpEvidenceVaultService>();
            services.AddSingleton<IMessageIngestService, NoOpMessageIngestService>();
            services.AddSingleton<JobQueueService>();
            services.AddSingleton<IJobQueueService>(provider => provider.GetRequiredService<JobQueueService>());

            var provider = services.BuildServiceProvider();
            var initializer = provider.GetRequiredService<IWorkspaceDatabaseInitializer>();
            await initializer.EnsureInitializedAsync(CancellationToken.None);

            return new ReportFixture(provider, pathProvider);
        }

        public async Task<SeededTargetScenario> SeedTargetScenarioAsync()
        {
            var caseId = Guid.NewGuid();
            var evidenceId = Guid.NewGuid();
            var threadId = Guid.NewGuid();
            var targetId = Guid.NewGuid();
            var identifierId = Guid.NewGuid();
            var auditId = Guid.NewGuid();
            var firstMessageId = Guid.NewGuid();
            var secondMessageId = Guid.NewGuid();
            var thirdMessageId = Guid.NewGuid();
            var t1 = new DateTimeOffset(2026, 2, 25, 9, 0, 0, TimeSpan.Zero);
            var t2 = t1.AddHours(1);
            var t3 = t2.AddHours(1);

            await using var db = await CreateDbContextAsync();
            db.Cases.Add(new CaseRecord
            {
                CaseId = caseId,
                Name = "Report Case",
                CreatedAtUtc = t1,
                LastOpenedAtUtc = t1
            });

            db.EvidenceItems.Add(new EvidenceItemRecord
            {
                EvidenceItemId = evidenceId,
                CaseId = caseId,
                DisplayName = "Synthetic Messages",
                OriginalPath = "synthetic",
                OriginalFileName = "synthetic.xlsx",
                AddedAtUtc = t1,
                SizeBytes = 1024,
                Sha256Hex = "ab",
                FileExtension = ".xlsx",
                SourceType = "XLSX",
                ManifestRelativePath = "manifest.json",
                StoredRelativePath = "vault/synthetic.xlsx"
            });

            db.MessageThreads.Add(new MessageThreadRecord
            {
                ThreadId = threadId,
                CaseId = caseId,
                EvidenceItemId = evidenceId,
                Platform = "SMS",
                ThreadKey = "thread-alpha",
                CreatedAtUtc = t1,
                SourceLocator = "test:thread:alpha",
                IngestModuleVersion = "test"
            });

            db.Targets.Add(new TargetRecord
            {
                TargetId = targetId,
                CaseId = caseId,
                DisplayName = "Alpha Target",
                PrimaryAlias = "Alpha",
                Notes = null,
                CreatedAtUtc = t1,
                UpdatedAtUtc = t1,
                SourceType = "Manual",
                SourceEvidenceItemId = null,
                SourceLocator = "manual:test:target",
                IngestModuleVersion = "test"
            });

            db.Identifiers.Add(new IdentifierRecord
            {
                IdentifierId = identifierId,
                CaseId = caseId,
                Type = nameof(TargetIdentifierType.Phone),
                ValueRaw = "+15551230001",
                ValueNormalized = "+15551230001",
                Notes = "primary phone",
                CreatedAtUtc = t1,
                SourceType = "Derived",
                SourceEvidenceItemId = evidenceId,
                SourceLocator = "xlsx:test#Messages:R1:sender",
                IngestModuleVersion = "test"
            });

            db.TargetIdentifierLinks.Add(new TargetIdentifierLinkRecord
            {
                LinkId = Guid.NewGuid(),
                CaseId = caseId,
                TargetId = targetId,
                IdentifierId = identifierId,
                IsPrimary = true,
                CreatedAtUtc = t1,
                SourceType = "Derived",
                SourceEvidenceItemId = evidenceId,
                SourceLocator = "xlsx:test#Messages:R1:sender",
                IngestModuleVersion = "test"
            });

            db.MessageEvents.AddRange(
                CreateMessage(caseId, evidenceId, threadId, firstMessageId, t1, "xlsx:test#Messages:R1", "alpha one"),
                CreateMessage(caseId, evidenceId, threadId, secondMessageId, t2, "xlsx:test#Messages:R2", "alpha two"),
                CreateMessage(caseId, evidenceId, threadId, thirdMessageId, t3, "xlsx:test#Messages:R3", "alpha three")
            );

            db.TargetMessagePresences.AddRange(
                CreatePresence(caseId, targetId, identifierId, evidenceId, firstMessageId, t1, "xlsx:test#Messages:R1"),
                CreatePresence(caseId, targetId, identifierId, evidenceId, secondMessageId, t2, "xlsx:test#Messages:R2"),
                CreatePresence(caseId, targetId, identifierId, evidenceId, thirdMessageId, t3, "xlsx:test#Messages:R3")
            );

            db.AuditEvents.Add(new AuditEventRecord
            {
                AuditEventId = auditId,
                TimestampUtc = t1.AddMinutes(5),
                Operator = "tester",
                ActionType = "IdentifierLinkedToTarget",
                CaseId = caseId,
                EvidenceItemId = evidenceId,
                Summary = "Identifier linked to target.",
                JsonPayload = JsonSerializer.Serialize(new
                {
                    TargetId = targetId,
                    IdentifierId = identifierId
                })
            });

            await db.SaveChangesAsync();

            return new SeededTargetScenario(
                caseId,
                targetId,
                identifierId,
                evidenceId,
                firstMessageId,
                secondMessageId,
                thirdMessageId,
                auditId,
                t1,
                t2,
                t3
            );
        }

        public Task<WorkspaceDbContext> CreateDbContextAsync()
        {
            var factory = _provider.GetRequiredService<IDbContextFactory<WorkspaceDbContext>>();
            return factory.CreateDbContextAsync(CancellationToken.None);
        }

        public async ValueTask DisposeAsync()
        {
            await _provider.DisposeAsync();

            if (!Directory.Exists(PathProvider.WorkspaceRoot))
            {
                return;
            }

            for (var attempt = 1; attempt <= 5; attempt++)
            {
                try
                {
                    Directory.Delete(PathProvider.WorkspaceRoot, recursive: true);
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

        private static MessageEventRecord CreateMessage(
            Guid caseId,
            Guid evidenceId,
            Guid threadId,
            Guid messageEventId,
            DateTimeOffset timestampUtc,
            string sourceLocator,
            string body
        )
        {
            return new MessageEventRecord
            {
                MessageEventId = messageEventId,
                ThreadId = threadId,
                CaseId = caseId,
                EvidenceItemId = evidenceId,
                Platform = "SMS",
                TimestampUtc = timestampUtc,
                Direction = "Incoming",
                Sender = "+15551230001",
                Recipients = "+15550000001",
                Body = body,
                IsDeleted = false,
                SourceLocator = sourceLocator,
                IngestModuleVersion = "test"
            };
        }

        private static TargetMessagePresenceRecord CreatePresence(
            Guid caseId,
            Guid targetId,
            Guid identifierId,
            Guid evidenceId,
            Guid messageEventId,
            DateTimeOffset timestampUtc,
            string sourceLocator
        )
        {
            return new TargetMessagePresenceRecord
            {
                PresenceId = Guid.NewGuid(),
                CaseId = caseId,
                TargetId = targetId,
                MessageEventId = messageEventId,
                MatchedIdentifierId = identifierId,
                Role = "Sender",
                EvidenceItemId = evidenceId,
                SourceLocator = sourceLocator,
                MessageTimestampUtc = timestampUtc,
                FirstSeenUtc = timestampUtc,
                LastSeenUtc = timestampUtc
            };
        }
    }

    private sealed record SeededTargetScenario(
        Guid CaseId,
        Guid TargetId,
        Guid IdentifierId,
        Guid EvidenceId,
        Guid FirstMessageEventId,
        Guid SecondMessageEventId,
        Guid ThirdMessageEventId,
        Guid IdentifierLinkAuditId,
        DateTimeOffset FirstTimestampUtc,
        DateTimeOffset SecondTimestampUtc,
        DateTimeOffset ThirdTimestampUtc
    );

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

    private sealed class NoOpCaseWorkspaceService : ICaseWorkspaceService
    {
        public Task<IReadOnlyList<CaseInfo>> ListCasesAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<CaseInfo>>(Array.Empty<CaseInfo>());

        public Task<CaseInfo> CreateCaseAsync(string name, CancellationToken ct) => throw new NotSupportedException();

        public Task<CaseInfo> OpenCaseAsync(Guid caseId, CancellationToken ct) => throw new NotSupportedException();

        public Task SaveCaseAsync(CaseInfo caseInfo, IReadOnlyList<EvidenceItem> evidence, CancellationToken ct) => throw new NotSupportedException();

        public Task<(CaseInfo caseInfo, List<EvidenceItem> evidence)> LoadCaseAsync(Guid caseId, CancellationToken ct) => throw new NotSupportedException();
    }

    private sealed class NoOpEvidenceVaultService : IEvidenceVaultService
    {
        public Task<EvidenceItem> ImportEvidenceFileAsync(CaseInfo caseInfo, string filePath, IProgress<double>? progress, CancellationToken ct) => throw new NotSupportedException();

        public Task<(bool ok, string message)> VerifyEvidenceAsync(CaseInfo caseInfo, EvidenceItem item, IProgress<double>? progress, CancellationToken ct) => throw new NotSupportedException();
    }

    private sealed class NoOpMessageIngestService : IMessageIngestService
    {
        public Task<int> IngestMessagesFromEvidenceAsync(Guid caseId, EvidenceItemRecord evidence, IProgress<double>? progress, CancellationToken ct) => throw new NotSupportedException();

        public Task<MessageIngestResult> IngestMessagesDetailedFromEvidenceAsync(Guid caseId, EvidenceItemRecord evidence, IProgress<MessageIngestProgress>? progress, string? logContext, CancellationToken ct) => throw new NotSupportedException();
    }
}
