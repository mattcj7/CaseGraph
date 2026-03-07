using CaseGraph.Core.Abstractions;
using CaseGraph.Infrastructure.IncidentWindow;
using CaseGraph.Infrastructure.Persistence;
using CaseGraph.Infrastructure.Persistence.Entities;
using CaseGraph.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using System.Text.Json;

namespace CaseGraph.Infrastructure.Tests;

public sealed class IncidentWindowQueryServiceTests
{
    [Fact]
    public async Task ExecuteAsync_FiltersByWindowAndRadius_AndBuildsCoLocationCandidates()
    {
        await using var fixture = await WorkspaceFixture.CreateAsync();
        var service = fixture.Services.GetRequiredService<IncidentWindowQueryService>();
        var seeded = await fixture.SeedCaseWithEvidenceAsync("Incident Window Filters");

        var alphaTargetId = Guid.NewGuid();
        var bravoTargetId = Guid.NewGuid();
        var windowStart = new DateTimeOffset(2026, 3, 4, 9, 30, 0, TimeSpan.Zero);
        var windowEnd = new DateTimeOffset(2026, 3, 4, 11, 0, 0, TimeSpan.Zero);

        await using (var db = await fixture.CreateDbContextAsync())
        {
            db.Targets.AddRange(
                CreateTarget(seeded.CaseId, alphaTargetId, "Alpha", windowStart),
                CreateTarget(seeded.CaseId, bravoTargetId, "Bravo", windowStart)
            );

            db.MessageEvents.AddRange(
                CreateMessage(seeded.CaseId, seeded.EvidenceItemId, seeded.ThreadId, new DateTimeOffset(2026, 3, 4, 9, 0, 0, TimeSpan.Zero), "msg:before"),
                CreateMessage(seeded.CaseId, seeded.EvidenceItemId, seeded.ThreadId, new DateTimeOffset(2026, 3, 4, 10, 0, 0, TimeSpan.Zero), "msg:inside"),
                CreateMessage(seeded.CaseId, seeded.EvidenceItemId, seeded.ThreadId, new DateTimeOffset(2026, 3, 4, 12, 0, 0, TimeSpan.Zero), "msg:after")
            );

            db.LocationObservations.AddRange(
                CreateObservation(seeded.CaseId, seeded.EvidenceItemId, new DateTimeOffset(2026, 3, 4, 10, 5, 0, TimeSpan.Zero), 34.00040, -118.00000, "row-alpha", "Target", alphaTargetId),
                CreateObservation(seeded.CaseId, seeded.EvidenceItemId, new DateTimeOffset(2026, 3, 4, 10, 9, 0, TimeSpan.Zero), 34.00055, -118.00010, "row-bravo", "Target", bravoTargetId),
                CreateObservation(seeded.CaseId, seeded.EvidenceItemId, new DateTimeOffset(2026, 3, 4, 10, 15, 0, TimeSpan.Zero), 34.00200, -118.00000, "row-far", "Target", Guid.NewGuid()),
                CreateObservation(seeded.CaseId, seeded.EvidenceItemId, new DateTimeOffset(2026, 3, 4, 12, 15, 0, TimeSpan.Zero), 34.00030, -118.00000, "row-late", "Target", Guid.NewGuid())
            );

            await db.SaveChangesAsync();
        }

        var result = await service.ExecuteAsync(
            new IncidentWindowQueryRequest(
                CaseId: seeded.CaseId,
                StartUtc: windowStart,
                EndUtc: windowEnd,
                RadiusEnabled: true,
                CenterLatitude: 34.0000,
                CenterLongitude: -118.0000,
                RadiusMeters: 100d,
                SubjectType: null,
                SubjectId: null,
                IncludeCoLocationCandidates: true,
                CommsTake: 50,
                CommsSkip: 0,
                GeoTake: 50,
                GeoSkip: 0,
                CoLocationTake: 50,
                CoLocationSkip: 0,
                CoLocationDistanceMeters: 100d,
                CoLocationTimeWindowMinutes: 10,
                CorrelationId: "incident-window-filters",
                WriteAuditEvent: false
            ),
            CancellationToken.None
        );

        var comms = Assert.Single(result.Comms.Rows);
        Assert.Equal("msg:inside", comms.SourceLocator);

        Assert.Equal(2, result.Geo.TotalCount);
        Assert.All(result.Geo.Rows, row => Assert.InRange(row.DistanceFromCenterMeters ?? double.MaxValue, 0d, 100d));

        var candidate = Assert.Single(result.CoLocation.Rows);
        Assert.Contains("row-alpha", candidate.Citation, StringComparison.Ordinal);
        Assert.Contains("row-bravo", candidate.Citation, StringComparison.Ordinal);
        Assert.Contains("Alpha", candidate.Why, StringComparison.Ordinal);
        Assert.Contains("Bravo", candidate.Why, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExecuteAsync_WritesIncidentWindowAuditEvent()
    {
        await using var fixture = await WorkspaceFixture.CreateAsync();
        var service = fixture.Services.GetRequiredService<IncidentWindowQueryService>();
        var seeded = await fixture.SeedCaseWithEvidenceAsync("Incident Window Audit");

        await using (var db = await fixture.CreateDbContextAsync())
        {
            db.MessageEvents.Add(
                CreateMessage(
                    seeded.CaseId,
                    seeded.EvidenceItemId,
                    seeded.ThreadId,
                    new DateTimeOffset(2026, 3, 5, 14, 0, 0, TimeSpan.Zero),
                    "msg:audit"
                )
            );
            db.LocationObservations.Add(
                CreateObservation(
                    seeded.CaseId,
                    seeded.EvidenceItemId,
                    new DateTimeOffset(2026, 3, 5, 14, 2, 0, TimeSpan.Zero),
                    34.00020,
                    -118.00010,
                    "row-audit"
                )
            );
            await db.SaveChangesAsync();
        }

        var correlationId = "incident-window-audit";
        var result = await service.ExecuteAsync(
            new IncidentWindowQueryRequest(
                CaseId: seeded.CaseId,
                StartUtc: new DateTimeOffset(2026, 3, 5, 13, 0, 0, TimeSpan.Zero),
                EndUtc: new DateTimeOffset(2026, 3, 5, 15, 0, 0, TimeSpan.Zero),
                RadiusEnabled: true,
                CenterLatitude: 34.0000,
                CenterLongitude: -118.0000,
                RadiusMeters: 250d,
                SubjectType: null,
                SubjectId: null,
                IncludeCoLocationCandidates: false,
                CommsTake: 50,
                CommsSkip: 0,
                GeoTake: 50,
                GeoSkip: 0,
                CoLocationTake: 50,
                CoLocationSkip: 0,
                CoLocationDistanceMeters: 100d,
                CoLocationTimeWindowMinutes: 10,
                CorrelationId: correlationId,
                WriteAuditEvent: true
            ),
            CancellationToken.None
        );

        Assert.Equal(1, result.Comms.TotalCount);
        Assert.Equal(1, result.Geo.TotalCount);

        await using var verifyDb = await fixture.CreateDbContextAsync();
        var audit = (await verifyDb.AuditEvents
            .AsNoTracking()
            .Where(item => item.ActionType == "IncidentWindowExecuted")
            .ToListAsync())
            .OrderByDescending(item => item.TimestampUtc)
            .FirstOrDefault();

        Assert.NotNull(audit);
        Assert.Equal(seeded.CaseId, audit.CaseId);
        Assert.Contains("comms hit", audit.Summary, StringComparison.OrdinalIgnoreCase);
        Assert.False(string.IsNullOrWhiteSpace(audit.JsonPayload));

        using var payload = JsonDocument.Parse(audit.JsonPayload!);
        Assert.Equal(correlationId, payload.RootElement.GetProperty("CorrelationId").GetString());
        Assert.True(payload.RootElement.GetProperty("RadiusEnabled").GetBoolean());
        Assert.Equal(1, payload.RootElement.GetProperty("CommsResultCount").GetInt32());
        Assert.Equal(1, payload.RootElement.GetProperty("GeoResultCount").GetInt32());
    }

    private static TargetRecord CreateTarget(Guid caseId, Guid targetId, string displayName, DateTimeOffset createdUtc)
    {
        return new TargetRecord
        {
            TargetId = targetId,
            CaseId = caseId,
            DisplayName = displayName,
            CreatedAtUtc = createdUtc,
            UpdatedAtUtc = createdUtc,
            SourceType = "Manual",
            SourceEvidenceItemId = null,
            SourceLocator = $"manual:{displayName.ToLowerInvariant()}",
            IngestModuleVersion = "test"
        };
    }

    private static MessageEventRecord CreateMessage(
        Guid caseId,
        Guid evidenceItemId,
        Guid threadId,
        DateTimeOffset timestampUtc,
        string sourceLocator
    )
    {
        return new MessageEventRecord
        {
            MessageEventId = Guid.NewGuid(),
            ThreadId = threadId,
            CaseId = caseId,
            EvidenceItemId = evidenceItemId,
            Platform = "SMS",
            TimestampUtc = timestampUtc,
            Direction = "Incoming",
            Sender = "+15550001",
            Recipients = "+15550002",
            Body = "window evidence",
            IsDeleted = false,
            SourceLocator = sourceLocator,
            IngestModuleVersion = "test"
        };
    }

    private static LocationObservationRecord CreateObservation(
        Guid caseId,
        Guid evidenceItemId,
        DateTimeOffset observedUtc,
        double latitude,
        double longitude,
        string sourceLabel,
        string? subjectType = null,
        Guid? subjectId = null
    )
    {
        return new LocationObservationRecord
        {
            LocationObservationId = Guid.NewGuid(),
            CaseId = caseId,
            ObservedUtc = observedUtc,
            Latitude = latitude,
            Longitude = longitude,
            AccuracyMeters = 5d,
            SourceType = "CSV",
            SourceLabel = sourceLabel,
            SubjectType = subjectType,
            SubjectId = subjectId,
            SourceEvidenceItemId = evidenceItemId,
            SourceLocator = $"csv://test#{sourceLabel}",
            IngestModuleVersion = "test",
            CreatedUtc = observedUtc
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
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(workspaceRoot);

            var pathProvider = new TestWorkspacePathProvider(workspaceRoot);
            var services = new ServiceCollection();
            services.AddSingleton<IClock>(new FixedClock(new DateTimeOffset(2026, 3, 5, 18, 0, 0, TimeSpan.Zero)));
            services.AddSingleton<IWorkspacePathProvider>(pathProvider);
            services.AddDbContextFactory<WorkspaceDbContext>(options => options.UseSqlite($"Data Source={pathProvider.WorkspaceDbPath}"));
            services.AddSingleton<WorkspaceDbRebuilder>();
            services.AddSingleton<WorkspaceDbInitializer>();
            services.AddSingleton<IWorkspaceDbInitializer>(provider => provider.GetRequiredService<WorkspaceDbInitializer>());
            services.AddSingleton<IWorkspaceDatabaseInitializer>(provider => provider.GetRequiredService<WorkspaceDbInitializer>());
            services.AddSingleton<IWorkspaceWriteGate, WorkspaceWriteGate>();
            services.AddSingleton<IAuditLogService, AuditLogService>();
            services.AddSingleton<IncidentWindowQueryService>();

            var provider = services.BuildServiceProvider();
            await provider.GetRequiredService<IWorkspaceDatabaseInitializer>().EnsureInitializedAsync(CancellationToken.None);
            return new WorkspaceFixture(provider, pathProvider);
        }

        public async Task<(Guid CaseId, Guid EvidenceItemId, Guid ThreadId)> SeedCaseWithEvidenceAsync(string caseName)
        {
            var caseId = Guid.NewGuid();
            var evidenceItemId = Guid.NewGuid();
            var threadId = Guid.NewGuid();
            var createdAt = new DateTimeOffset(2026, 3, 4, 8, 0, 0, TimeSpan.Zero);

            await using var db = await CreateDbContextAsync();
            db.Cases.Add(new CaseRecord { CaseId = caseId, Name = caseName, CreatedAtUtc = createdAt, LastOpenedAtUtc = createdAt });
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
                SourceLocator = "thread:test",
                IngestModuleVersion = "test"
            });
            await db.SaveChangesAsync();
            return (caseId, evidenceItemId, threadId);
        }

        public Task<WorkspaceDbContext> CreateDbContextAsync()
        {
            return _provider.GetRequiredService<IDbContextFactory<WorkspaceDbContext>>()
                .CreateDbContextAsync(CancellationToken.None);
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
        public FixedClock(DateTimeOffset utcNow) => UtcNow = utcNow;
        public DateTimeOffset UtcNow { get; }
    }
}
