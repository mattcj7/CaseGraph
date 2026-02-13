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

        var hits = await search.SearchAsync(caseInfo.CaseId, "burner", take: 20, skip: 0, CancellationToken.None);
        Assert.NotEmpty(hits);
        Assert.Contains(hits, h => (h.Snippet ?? string.Empty).Contains("burner", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task MessagesIngestJob_CreatesRows_AndAuditSummary()
    {
        await using var fixture = await WorkspaceFixture.CreateAsync(startRunner: true);
        var workspace = fixture.Services.GetRequiredService<ICaseWorkspaceService>();
        var vault = fixture.Services.GetRequiredService<IEvidenceVaultService>();
        var queue = fixture.Services.GetRequiredService<IJobQueueService>();

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

        var succeeded = await WaitForJobStatusAsync(
            fixture,
            jobId,
            status => status == "Succeeded",
            TimeSpan.FromSeconds(12)
        );
        Assert.Equal("Succeeded", succeeded.Status);

        await using var db = await fixture.CreateDbContextAsync();
        var events = await db.MessageEvents
            .Where(e => e.CaseId == caseInfo.CaseId && e.EvidenceItemId == evidence.EvidenceItemId)
            .ToListAsync();

        Assert.NotEmpty(events);
        Assert.All(events, e => Assert.False(string.IsNullOrWhiteSpace(e.SourceLocator)));
        Assert.All(events, e => Assert.False(string.IsNullOrWhiteSpace(e.IngestModuleVersion)));

        var summaryAudit = await WaitForAuditEventAsync(
            fixture,
            caseInfo.CaseId,
            evidence.EvidenceItemId,
            "MessagesIngested",
            TimeSpan.FromSeconds(5)
        );
        Assert.NotNull(summaryAudit);
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
            services.AddSingleton<IAuditLogService, AuditLogService>();
            services.AddSingleton<ICaseWorkspaceService, CaseWorkspaceService>();
            services.AddSingleton<IEvidenceVaultService, EvidenceVaultService>();
            services.AddSingleton<IMessageSearchService, MessageSearchService>();
            services.AddSingleton<IMessageIngestService, MessageIngestService>();
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

        public string CreateMessagesXlsx(string fileName)
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
            sheetData.Append(new Row(
                Cell("A2", "2026-02-13T12:00:00Z"),
                Cell("B2", "Incoming"),
                Cell("C2", "+15550001"),
                Cell("D2", "+15550002"),
                Cell("E2", "Meet me at the checkpoint."),
                Cell("F2", "false"),
                Cell("G2", "thread-alpha"),
                Cell("H2", "SMS")
            ));
            sheetData.Append(new Row(
                Cell("A3", "2026-02-13T12:05:00Z"),
                Cell("B3", "Outgoing"),
                Cell("C3", "+15550002"),
                Cell("D3", "+15550001"),
                Cell("E3", "Bring the evidence folder."),
                Cell("F3", "false"),
                Cell("G3", "thread-alpha"),
                Cell("H3", "SMS")
            ));

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
