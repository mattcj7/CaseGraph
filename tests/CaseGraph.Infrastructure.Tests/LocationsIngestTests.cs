using CaseGraph.Core.Abstractions;
using CaseGraph.Core.Models;
using CaseGraph.Infrastructure.Locations;
using CaseGraph.Infrastructure.Persistence;
using CaseGraph.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace CaseGraph.Infrastructure.Tests;

public sealed class LocationsIngestTests
{
    [Fact]
    public async Task CsvParser_ParsesRows_SkipsInvalidRows_AndBuildsLocators()
    {
        var parser = new LocationCsvParser();
        var sourcePath = Path.GetTempFileName();
        try
        {
            await File.WriteAllTextAsync(
                sourcePath,
                """
                timestamp,latitude,longitude,accuracy,IgnoredField
                2026-03-01T10:00:00Z,34.1201,-118.2202,12.5,foo
                2026-03-01T11:00:00Z,999,-118.2202,15,bar
                2026-03-01T12:00:00Z,34.1202,-118.2203,7.1,baz
                """
            );

            var observations = new List<LocationParsedObservation>();
            var result = await parser.ParseAsync(
                sourcePath,
                "sample.csv",
                (observation, _) =>
                {
                    observations.Add(observation);
                    return ValueTask.CompletedTask;
                },
                onProgress: null,
                CancellationToken.None
            );

            Assert.Equal(3, result.ProcessedCount);
            Assert.Equal(2, result.AcceptedCount);
            Assert.Equal(1, result.SkippedCount);
            Assert.Contains("ignoredfield", result.UnknownFieldNames);
            Assert.Equal("csv://sample.csv#row=2", observations[0].SourceLocator);
            Assert.Equal("csv://sample.csv#row=4", observations[1].SourceLocator);
        }
        finally
        {
            File.Delete(sourcePath);
        }
    }

    [Fact]
    public async Task JsonParser_SupportsArrayAndNestedObjects()
    {
        var parser = new LocationJsonParser();
        var sourcePath = Path.GetTempFileName();
        try
        {
            await File.WriteAllTextAsync(
                sourcePath,
                """
                {
                  "locations": [
                    { "time": "2026-03-01T10:00:00Z", "lat": 34.01, "lon": -118.21 },
                    { "time": "2026-03-01T10:05:00Z", "lat": 999, "lon": -118.21 },
                    { "nested": { "timestamp": "2026-03-01T11:00:00Z", "latitude": 34.02, "longitude": -118.22 } }
                  ]
                }
                """
            );

            var observations = new List<LocationParsedObservation>();
            var result = await parser.ParseAsync(
                sourcePath,
                "sample.json",
                (observation, _) =>
                {
                    observations.Add(observation);
                    return ValueTask.CompletedTask;
                },
                onProgress: null,
                CancellationToken.None
            );

            Assert.True(result.AcceptedCount >= 2);
            Assert.True(result.SkippedCount >= 1);
            Assert.Contains(observations, item => item.SourceLocator == "json://sample.json#ptr=/locations/0");
            Assert.Contains(observations, item => item.SourceLocator == "json://sample.json#ptr=/locations/2/nested");
        }
        finally
        {
            File.Delete(sourcePath);
        }
    }

    [Fact]
    public async Task PlistParser_ExtractsLocations_WithKeyPathLocators()
    {
        var parser = new LocationPlistParser();
        var sourcePath = Path.GetTempFileName();
        try
        {
            await File.WriteAllTextAsync(
                sourcePath,
                """
                <?xml version="1.0" encoding="UTF-8"?>
                <!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
                <plist version="1.0">
                  <dict>
                    <key>locations</key>
                    <array>
                      <dict>
                        <key>timestamp</key><date>2026-03-01T14:00:00Z</date>
                        <key>latitude</key><real>34.3001</real>
                        <key>longitude</key><real>-118.4301</real>
                        <key>accuracy</key><real>6.2</real>
                      </dict>
                      <dict>
                        <key>timestamp</key><date>2026-03-01T15:00:00Z</date>
                        <key>latitude</key><real>34.3002</real>
                      </dict>
                    </array>
                  </dict>
                </plist>
                """
            );

            var observations = new List<LocationParsedObservation>();
            var result = await parser.ParseAsync(
                sourcePath,
                "sample.plist",
                (observation, _) =>
                {
                    observations.Add(observation);
                    return ValueTask.CompletedTask;
                },
                onProgress: null,
                CancellationToken.None
            );

            Assert.Equal(1, result.AcceptedCount);
            Assert.True(result.SkippedCount >= 1);
            Assert.Equal("plist://sample.plist#keyPath=root.locations%5B0%5D", observations[0].SourceLocator);
        }
        finally
        {
            File.Delete(sourcePath);
        }
    }

    [Fact]
    public async Task LocationsIngestJob_PersistsProvenance_AndReplacesPriorRows()
    {
        await using var fixture = await WorkspaceFixture.CreateAsync();
        var workspace = fixture.Services.GetRequiredService<ICaseWorkspaceService>();
        var vault = fixture.Services.GetRequiredService<IEvidenceVaultService>();
        var ingest = fixture.Services.GetRequiredService<LocationsIngestJob>();

        var caseInfo = await workspace.CreateCaseAsync("Locations Ingest Case", CancellationToken.None);
        var sourcePath = fixture.CreateSourceFile(
            "locations.csv",
            """
            timestamp,lat,lon,accuracy
            2026-03-02T10:00:00Z,34.01,-118.11,5
            2026-03-02T10:01:00Z,999,-118.11,9
            2026-03-02T10:02:00Z,34.02,-118.12,7
            """
        );

        var evidence = await vault.ImportEvidenceFileAsync(caseInfo, sourcePath, progress: null, CancellationToken.None);
        var first = await ingest.IngestAsync(caseInfo.CaseId, evidence.EvidenceItemId, progress: null, CancellationToken.None);
        var second = await ingest.IngestAsync(caseInfo.CaseId, evidence.EvidenceItemId, progress: null, CancellationToken.None);

        Assert.Equal(2, first.InsertedCount);
        Assert.True(first.SkippedCount >= 1);
        Assert.Equal(first.InsertedCount, second.ReplacedCount);
        Assert.Equal(2, second.InsertedCount);

        await using var db = await fixture.CreateDbContextAsync();
        var rows = await db.LocationObservations
            .AsNoTracking()
            .Where(item => item.CaseId == caseInfo.CaseId && item.SourceEvidenceItemId == evidence.EvidenceItemId)
            .ToListAsync();

        Assert.Equal(2, rows.Count);
        Assert.All(rows, row => Assert.Equal(evidence.EvidenceItemId, row.SourceEvidenceItemId));
        Assert.All(rows, row => Assert.StartsWith("csv://", row.SourceLocator, StringComparison.Ordinal));
        Assert.All(rows, row => Assert.Equal(LocationsIngestJob.IngestModuleVersion, row.IngestModuleVersion));
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
                new FixedClock(new DateTimeOffset(2026, 3, 2, 12, 0, 0, TimeSpan.Zero))
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
            services.AddSingleton<IEvidenceVaultService, EvidenceVaultService>();
            services.AddSingleton<LocationCsvParser>();
            services.AddSingleton<LocationJsonParser>();
            services.AddSingleton<LocationPlistParser>();
            services.AddSingleton<LocationsIngestJob>();

            var provider = services.BuildServiceProvider();
            var initializer = provider.GetRequiredService<IWorkspaceDatabaseInitializer>();
            await initializer.EnsureInitializedAsync(CancellationToken.None);

            return new WorkspaceFixture(provider, pathProvider);
        }

        public string CreateSourceFile(string fileName, string content)
        {
            var sourceDirectory = Path.Combine(_pathProvider.WorkspaceRoot, "source");
            Directory.CreateDirectory(sourceDirectory);
            var filePath = Path.Combine(sourceDirectory, fileName);
            File.WriteAllText(filePath, content);
            return filePath;
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
