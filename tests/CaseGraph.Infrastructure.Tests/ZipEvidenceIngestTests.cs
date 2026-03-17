using CaseGraph.Core.Abstractions;
using CaseGraph.Core.Models;
using CaseGraph.Infrastructure.Locations;
using CaseGraph.Infrastructure.Persistence;
using CaseGraph.Infrastructure.Persistence.Entities;
using CaseGraph.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using System.IO.Compression;
using System.Text;

namespace CaseGraph.Infrastructure.Tests;

public sealed class ZipEvidenceIngestTests
{
    [Fact]
    public async Task ImportEvidenceFileAsync_Zip_PreservesOriginalAndExtractsDerivedContents()
    {
        await using var fixture = await WorkspaceFixture.CreateAsync();
        var workspace = fixture.Services.GetRequiredService<ICaseWorkspaceService>();
        var vault = fixture.Services.GetRequiredService<IEvidenceVaultService>();

        var caseInfo = await workspace.CreateCaseAsync("ZIP Import Case", CancellationToken.None);
        var zipPath = fixture.CreateZipArchive(
            "bundle.zip",
            ("returns/messages.csv", BuildMessageCsv("msg-1001", "thread-zip-1", "Archive body")),
            ("geo/locations.json", """
            { "locations": [ { "timestamp": "2026-03-03T10:00:00Z", "latitude": 34.1001, "longitude": -118.2001 } ] }
            """)
        );

        var evidenceItem = await vault.ImportEvidenceFileAsync(caseInfo, zipPath, progress: null, CancellationToken.None);
        var storedZipPath = fixture.ResolveCasePath(caseInfo.CaseId, evidenceItem.StoredRelativePath);
        var extractedCsvPath = fixture.ResolveCasePath(
            caseInfo.CaseId,
            $"vault/{evidenceItem.EvidenceItemId:D}/derived/extracted/returns/messages.csv"
        );
        var extractedJsonPath = fixture.ResolveCasePath(
            caseInfo.CaseId,
            $"vault/{evidenceItem.EvidenceItemId:D}/derived/extracted/geo/locations.json"
        );

        Assert.True(File.Exists(storedZipPath));
        Assert.True(File.Exists(extractedCsvPath));
        Assert.True(File.Exists(extractedJsonPath));
        Assert.Equal("ZIP", evidenceItem.SourceType);

        var extraction = await vault.EnsureArchiveExtractedAsync(caseInfo.CaseId, evidenceItem, CancellationToken.None);
        Assert.NotNull(extraction);
        Assert.Equal(
            ["geo/locations.json", "returns/messages.csv"],
            extraction!.ExtractedRelativePaths.OrderBy(path => path, StringComparer.Ordinal).ToArray()
        );
        Assert.Empty(extraction.Warnings);
    }

    [Fact]
    public async Task IngestMessagesFromZip_RoutesCsvAndHtmlEntries_AndPreservesLocators()
    {
        await using var fixture = await WorkspaceFixture.CreateAsync();
        var workspace = fixture.Services.GetRequiredService<ICaseWorkspaceService>();
        var vault = fixture.Services.GetRequiredService<IEvidenceVaultService>();
        var ingest = fixture.Services.GetRequiredService<IMessageIngestService>();

        var caseInfo = await workspace.CreateCaseAsync("ZIP Messages Case", CancellationToken.None);
        var zipPath = fixture.CreateZipArchive(
            "messages-bundle.zip",
            ("returns/messages.csv", BuildMessageCsv("msg-2001", "thread-csv", "Archive CSV body")),
            ("social/thread.html", """
            <html xmlns="http://www.w3.org/1999/xhtml">
              <body>
                <div class="message" data-message-id="msg-2002" data-thread-id="thread-html" data-platform="Instagram" data-sent-utc="2026-03-03T11:00:00Z" data-direction="Incoming">
                  <span class="sender">handle_alpha</span>
                  <span class="recipients">handle_bravo</span>
                  <div class="body">Archive HTML body</div>
                </div>
              </body>
            </html>
            """)
        );

        var evidenceItem = await vault.ImportEvidenceFileAsync(caseInfo, zipPath, progress: null, CancellationToken.None);
        var evidenceRecord = await fixture.GetEvidenceRecordAsync(evidenceItem.EvidenceItemId);

        var result = await ingest.IngestMessagesDetailedFromEvidenceAsync(
            caseInfo.CaseId,
            evidenceRecord,
            progress: null,
            logContext: null,
            CancellationToken.None
        );

        Assert.Equal(2, result.MessagesExtracted);
        Assert.Contains("archive file", result.SummaryOverride ?? string.Empty, StringComparison.OrdinalIgnoreCase);

        await using var db = await fixture.CreateDbContextAsync();
        var messages = await db.MessageEvents
            .AsNoTracking()
            .Where(item => item.CaseId == caseInfo.CaseId && item.EvidenceItemId == evidenceItem.EvidenceItemId)
            .OrderBy(item => item.SourceLocator)
            .ToListAsync();

        Assert.Equal(2, messages.Count);
        Assert.Contains(messages, item => item.SourceLocator == "csv:returns/messages.csv#row:2");
        Assert.Contains(messages, item => item.SourceLocator == "html:social/thread.html#msg-2002");
        Assert.All(messages, item => Assert.Equal(evidenceItem.EvidenceItemId, item.EvidenceItemId));
    }

    [Fact]
    public async Task IngestMessagesFromZip_FailsSoft_WhenOneInnerFileIsMalformed()
    {
        await using var fixture = await WorkspaceFixture.CreateAsync();
        var workspace = fixture.Services.GetRequiredService<ICaseWorkspaceService>();
        var vault = fixture.Services.GetRequiredService<IEvidenceVaultService>();
        var ingest = fixture.Services.GetRequiredService<IMessageIngestService>();

        var caseInfo = await workspace.CreateCaseAsync("ZIP Fail Soft Case", CancellationToken.None);
        var zipPath = fixture.CreateZipArchive(
            "messages-fail-soft.zip",
            ("returns/messages.csv", BuildMessageCsv("msg-3001", "thread-csv", "Good archive CSV body")),
            ("social/bad.html", "<html><body><div class=\"message\"><span>broken")
        );

        var evidenceItem = await vault.ImportEvidenceFileAsync(caseInfo, zipPath, progress: null, CancellationToken.None);
        var evidenceRecord = await fixture.GetEvidenceRecordAsync(evidenceItem.EvidenceItemId);

        var result = await ingest.IngestMessagesDetailedFromEvidenceAsync(
            caseInfo.CaseId,
            evidenceRecord,
            progress: null,
            logContext: null,
            CancellationToken.None
        );

        Assert.Equal(1, result.MessagesExtracted);
        Assert.Contains("Failed 1 archive file", result.SummaryOverride ?? string.Empty, StringComparison.OrdinalIgnoreCase);

        await using var db = await fixture.CreateDbContextAsync();
        var messageCount = await db.MessageEvents
            .AsNoTracking()
            .CountAsync(item => item.CaseId == caseInfo.CaseId && item.EvidenceItemId == evidenceItem.EvidenceItemId);
        Assert.Equal(1, messageCount);
    }

    [Fact]
    public async Task IngestLocationsFromZip_RoutesJsonAndPlistEntries_AndPreservesProvenance()
    {
        await using var fixture = await WorkspaceFixture.CreateAsync();
        var workspace = fixture.Services.GetRequiredService<ICaseWorkspaceService>();
        var vault = fixture.Services.GetRequiredService<IEvidenceVaultService>();
        var ingest = fixture.Services.GetRequiredService<LocationsIngestJob>();

        var caseInfo = await workspace.CreateCaseAsync("ZIP Locations Case", CancellationToken.None);
        var zipPath = fixture.CreateZipArchive(
            "locations-bundle.zip",
            ("geo/locations.json", """
            {
              "locations": [
                { "timestamp": "2026-03-03T12:00:00Z", "latitude": 34.2101, "longitude": -118.3101 }
              ]
            }
            """),
            ("geo/cache.plist", """
            <?xml version="1.0" encoding="UTF-8"?>
            <!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
            <plist version="1.0">
              <dict>
                <key>locations</key>
                <array>
                  <dict>
                    <key>timestamp</key><date>2026-03-03T12:05:00Z</date>
                    <key>latitude</key><real>34.2202</real>
                    <key>longitude</key><real>-118.3202</real>
                  </dict>
                </array>
              </dict>
            </plist>
            """)
        );

        var evidenceItem = await vault.ImportEvidenceFileAsync(caseInfo, zipPath, progress: null, CancellationToken.None);
        var result = await ingest.IngestAsync(caseInfo.CaseId, evidenceItem.EvidenceItemId, progress: null, CancellationToken.None);

        Assert.Equal(2, result.InsertedCount);
        Assert.Equal(0, result.FileErrorCount);

        await using var db = await fixture.CreateDbContextAsync();
        var rows = await db.LocationObservations
            .AsNoTracking()
            .Where(item => item.CaseId == caseInfo.CaseId && item.SourceEvidenceItemId == evidenceItem.EvidenceItemId)
            .OrderBy(item => item.SourceLocator)
            .ToListAsync();

        Assert.Equal(2, rows.Count);
        Assert.Contains(rows, item => item.SourceLocator == "json:geo/locations.json#/locations/0");
        Assert.Contains(rows, item => item.SourceLocator == "plist:geo/cache.plist#Root.locations[0]");
        Assert.All(rows, item => Assert.Equal(evidenceItem.EvidenceItemId, item.SourceEvidenceItemId));
        Assert.All(rows, item => Assert.Equal(LocationsIngestJob.IngestModuleVersion, item.IngestModuleVersion));
    }

    private static string BuildMessageCsv(string messageId, string threadId, string body)
    {
        return $$"""
        MessageId,ThreadId,ThreadName,Sender,Recipients,SentUtc,Direction,Body
        {{messageId}},{{threadId}},Archive Thread,+15550001111,+15550002222,2026-03-03T10:00:00Z,Outgoing,{{body}}
        """;
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
                new FixedClock(new DateTimeOffset(2026, 3, 3, 12, 0, 0, TimeSpan.Zero))
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
            services.AddSingleton<IMessageIngestService, MessageIngestService>();
            services.AddSingleton<LocationCsvParser>();
            services.AddSingleton<LocationJsonParser>();
            services.AddSingleton<LocationPlistParser>();
            services.AddSingleton<LocationsIngestJob>();

            var provider = services.BuildServiceProvider();
            var initializer = provider.GetRequiredService<IWorkspaceDatabaseInitializer>();
            await initializer.EnsureInitializedAsync(CancellationToken.None);

            return new WorkspaceFixture(provider, pathProvider);
        }

        public string CreateZipArchive(string fileName, params (string entryPath, string content)[] entries)
        {
            var sourceDirectory = Path.Combine(_pathProvider.WorkspaceRoot, "source");
            Directory.CreateDirectory(sourceDirectory);

            var path = Path.Combine(sourceDirectory, fileName);
            using var archive = ZipFile.Open(path, ZipArchiveMode.Create);
            foreach (var (entryPath, content) in entries)
            {
                var entry = archive.CreateEntry(entryPath, CompressionLevel.Optimal);
                using var writer = new StreamWriter(entry.Open(), Encoding.UTF8);
                writer.Write(content);
            }

            return path;
        }

        public string ResolveCasePath(Guid caseId, string relativePath)
        {
            var root = Path.Combine(_pathProvider.CasesRoot, caseId.ToString("D"));
            return Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar));
        }

        public async Task<EvidenceItemRecord> GetEvidenceRecordAsync(Guid evidenceItemId)
        {
            await using var db = await CreateDbContextAsync();
            return await db.EvidenceItems
                .AsNoTracking()
                .FirstAsync(item => item.EvidenceItemId == evidenceItemId);
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
                catch (IOException) when (attempt < 5)
                {
                    await Task.Delay(50);
                }
                catch (UnauthorizedAccessException) when (attempt < 5)
                {
                    await Task.Delay(50);
                }
                catch (IOException)
                {
                    return;
                }
                catch (UnauthorizedAccessException)
                {
                    return;
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
