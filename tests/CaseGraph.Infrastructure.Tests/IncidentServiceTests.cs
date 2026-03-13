using CaseGraph.Core.Abstractions;
using CaseGraph.Infrastructure.Incidents;
using CaseGraph.Infrastructure.Persistence;
using CaseGraph.Infrastructure.Persistence.Entities;
using CaseGraph.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace CaseGraph.Infrastructure.Tests;

public sealed class IncidentServiceTests
{
    [Fact]
    public async Task SaveIncidentAsync_PersistsIncidentAndSceneLocations()
    {
        await using var fixture = await WorkspaceFixture.CreateAsync();
        var service = fixture.Services.GetRequiredService<IIncidentService>();
        var seeded = await fixture.SeedCaseWithEvidenceAsync("Incident Persistence");

        var saved = await service.SaveIncidentAsync(
            CreateIncident(
                seeded.CaseId,
                title: "Drive-by near park",
                type: "Drive-By Shooting",
                status: "Open",
                startUtc: new DateTimeOffset(2026, 3, 7, 2, 0, 0, TimeSpan.Zero),
                endUtc: new DateTimeOffset(2026, 3, 7, 1, 0, 0, TimeSpan.Zero),
                primaryOccurrenceUtc: new DateTimeOffset(2026, 3, 7, 1, 30, 0, TimeSpan.Zero),
                locations:
                [
                    new IncidentLocation(Guid.Empty, 0, "Park", 34.0000, -118.0000, 150d, "Shell casings"),
                    new IncidentLocation(Guid.Empty, 1, "Alley", 34.0015, -118.0010, 90d, "Camera facing south")
                ]),
            "incident-persist",
            CancellationToken.None
        );

        var reloaded = await service.GetIncidentAsync(seeded.CaseId, saved.IncidentId, CancellationToken.None);

        Assert.NotNull(reloaded);
        Assert.Equal("Drive-by near park", reloaded.Title);
        Assert.Equal("Drive-By Shooting", reloaded.IncidentType);
        Assert.Equal("Open", reloaded.Status);
        Assert.Equal(new DateTimeOffset(2026, 3, 7, 1, 0, 0, TimeSpan.Zero), reloaded.OffenseWindowStartUtc);
        Assert.Equal(new DateTimeOffset(2026, 3, 7, 2, 0, 0, TimeSpan.Zero), reloaded.OffenseWindowEndUtc);
        Assert.Equal(2, reloaded.Locations.Count);
        Assert.Contains(reloaded.Locations, item => item.Label == "Park" && item.RadiusMeters == 150d);
        Assert.Contains(reloaded.Locations, item => item.Label == "Alley" && item.RadiusMeters == 90d);
    }

    [Fact]
    public async Task GetIncidentsAsync_UsesSqliteSafeOrdering_AndReturnsExpectedOrder()
    {
        await using var fixture = await WorkspaceFixture.CreateAsync();
        var service = fixture.Services.GetRequiredService<IIncidentService>();
        var seeded = await fixture.SeedCaseWithEvidenceAsync("Incident Ordering");

        fixture.SetUtcNow(new DateTimeOffset(2026, 3, 11, 8, 0, 0, TimeSpan.Zero));
        await service.SaveIncidentAsync(
            CreateIncident(
                seeded.CaseId,
                title: "Zulu older",
                type: "Shots Fired",
                status: "Open",
                startUtc: new DateTimeOffset(2026, 3, 11, 7, 0, 0, TimeSpan.Zero),
                endUtc: new DateTimeOffset(2026, 3, 11, 8, 0, 0, TimeSpan.Zero),
                primaryOccurrenceUtc: null,
                locations:
                [
                    new IncidentLocation(Guid.Empty, 0, "Scene Z", 34.0000, -118.0000, 125d, string.Empty)
                ]),
            "incident-ordering-zulu",
            CancellationToken.None
        );

        fixture.SetUtcNow(new DateTimeOffset(2026, 3, 11, 9, 0, 0, TimeSpan.Zero));
        await service.SaveIncidentAsync(
            CreateIncident(
                seeded.CaseId,
                title: "Bravo latest",
                type: "Shots Fired",
                status: "Open",
                startUtc: new DateTimeOffset(2026, 3, 11, 8, 0, 0, TimeSpan.Zero),
                endUtc: new DateTimeOffset(2026, 3, 11, 9, 0, 0, TimeSpan.Zero),
                primaryOccurrenceUtc: null,
                locations:
                [
                    new IncidentLocation(Guid.Empty, 0, "Scene B", 34.0001, -118.0001, 125d, string.Empty)
                ]),
            "incident-ordering-bravo",
            CancellationToken.None
        );

        await service.SaveIncidentAsync(
            CreateIncident(
                seeded.CaseId,
                title: "Alpha latest",
                type: "Shots Fired",
                status: "Open",
                startUtc: new DateTimeOffset(2026, 3, 11, 8, 30, 0, TimeSpan.Zero),
                endUtc: new DateTimeOffset(2026, 3, 11, 9, 30, 0, TimeSpan.Zero),
                primaryOccurrenceUtc: null,
                locations:
                [
                    new IncidentLocation(Guid.Empty, 0, "Scene A", 34.0002, -118.0002, 125d, string.Empty)
                ]),
            "incident-ordering-alpha",
            CancellationToken.None
        );

        var exception = await Record.ExceptionAsync(() => service.GetIncidentsAsync(seeded.CaseId, CancellationToken.None));
        Assert.Null(exception);

        var incidents = await service.GetIncidentsAsync(seeded.CaseId, CancellationToken.None);

        Assert.Collection(
            incidents,
            item =>
            {
                Assert.Equal("Alpha latest", item.Title);
                Assert.Equal(new DateTimeOffset(2026, 3, 11, 9, 0, 0, TimeSpan.Zero), item.UpdatedUtc);
            },
            item =>
            {
                Assert.Equal("Bravo latest", item.Title);
                Assert.Equal(new DateTimeOffset(2026, 3, 11, 9, 0, 0, TimeSpan.Zero), item.UpdatedUtc);
            },
            item =>
            {
                Assert.Equal("Zulu older", item.Title);
                Assert.Equal(new DateTimeOffset(2026, 3, 11, 8, 0, 0, TimeSpan.Zero), item.UpdatedUtc);
            }
        );
    }

    [Fact]
    public async Task RunCrossReferenceAsync_FiltersMessagesByOffenseWindow()
    {
        await using var fixture = await WorkspaceFixture.CreateAsync();
        var service = fixture.Services.GetRequiredService<IIncidentService>();
        var seeded = await fixture.SeedCaseWithEvidenceAsync("Incident Message Filter");

        await fixture.SeedMessagesAsync(
            seeded.CaseId,
            seeded.EvidenceItemId,
            seeded.ThreadId,
            [
                CreateMessage(seeded.CaseId, seeded.EvidenceItemId, seeded.ThreadId, new DateTimeOffset(2026, 3, 8, 0, 45, 0, TimeSpan.Zero), "msg:before"),
                CreateMessage(seeded.CaseId, seeded.EvidenceItemId, seeded.ThreadId, new DateTimeOffset(2026, 3, 8, 1, 15, 0, TimeSpan.Zero), "msg:inside"),
                CreateMessage(seeded.CaseId, seeded.EvidenceItemId, seeded.ThreadId, new DateTimeOffset(2026, 3, 8, 2, 10, 0, TimeSpan.Zero), "msg:after")
            ]
        );

        var incident = await service.SaveIncidentAsync(
            CreateIncident(
                seeded.CaseId,
                title: "Shots fired",
                type: "Shots Fired",
                status: "Open",
                startUtc: new DateTimeOffset(2026, 3, 8, 1, 0, 0, TimeSpan.Zero),
                endUtc: new DateTimeOffset(2026, 3, 8, 2, 0, 0, TimeSpan.Zero),
                primaryOccurrenceUtc: null,
                locations:
                [
                    new IncidentLocation(Guid.Empty, 0, "Primary scene", 34.0000, -118.0000, 250d, string.Empty)
                ]),
            "incident-time-window",
            CancellationToken.None
        );

        var result = await service.RunCrossReferenceAsync(
            seeded.CaseId,
            incident.IncidentId,
            "incident-time-window-run",
            CancellationToken.None
        );

        var message = Assert.Single(result.MessageResults);
        Assert.Equal("msg:inside", message.SourceLocator);
        Assert.DoesNotContain(result.MessageResults, item => item.SourceLocator == "msg:before");
        Assert.DoesNotContain(result.MessageResults, item => item.SourceLocator == "msg:after");
    }

    [Fact]
    public async Task RunCrossReferenceAsync_FiltersLocationsByRadiusAcrossScenes()
    {
        await using var fixture = await WorkspaceFixture.CreateAsync();
        var service = fixture.Services.GetRequiredService<IIncidentService>();
        var seeded = await fixture.SeedCaseWithEvidenceAsync("Incident Radius Filter");

        await fixture.SeedLocationsAsync(
            seeded.CaseId,
            seeded.EvidenceItemId,
            [
                CreateObservation(seeded.CaseId, seeded.EvidenceItemId, new DateTimeOffset(2026, 3, 9, 4, 10, 0, TimeSpan.Zero), 34.0004, -118.0000, "row-scene-1"),
                CreateObservation(seeded.CaseId, seeded.EvidenceItemId, new DateTimeOffset(2026, 3, 9, 4, 18, 0, TimeSpan.Zero), 34.0103, -118.0100, "row-scene-2"),
                CreateObservation(seeded.CaseId, seeded.EvidenceItemId, new DateTimeOffset(2026, 3, 9, 4, 25, 0, TimeSpan.Zero), 34.0200, -118.0200, "row-far"),
                CreateObservation(seeded.CaseId, seeded.EvidenceItemId, new DateTimeOffset(2026, 3, 9, 5, 15, 0, TimeSpan.Zero), 34.0002, -118.0001, "row-late")
            ]
        );

        var incident = await service.SaveIncidentAsync(
            CreateIncident(
                seeded.CaseId,
                title: "Homicide follow-up",
                type: "Homicide",
                status: "Open",
                startUtc: new DateTimeOffset(2026, 3, 9, 4, 0, 0, TimeSpan.Zero),
                endUtc: new DateTimeOffset(2026, 3, 9, 5, 0, 0, TimeSpan.Zero),
                primaryOccurrenceUtc: new DateTimeOffset(2026, 3, 9, 4, 20, 0, TimeSpan.Zero),
                locations:
                [
                    new IncidentLocation(Guid.Empty, 0, "Front yard", 34.0000, -118.0000, 75d, string.Empty),
                    new IncidentLocation(Guid.Empty, 1, "Rear lot", 34.0100, -118.0100, 60d, string.Empty)
                ]),
            "incident-radius-filter",
            CancellationToken.None
        );

        var result = await service.RunCrossReferenceAsync(
            seeded.CaseId,
            incident.IncidentId,
            "incident-radius-filter-run",
            CancellationToken.None
        );

        Assert.Equal(2, result.LocationResults.Count);
        Assert.Contains(result.LocationResults, item => item.SceneLabel == "Front yard" && item.Location.SourceLabel == "row-scene-1");
        Assert.Contains(result.LocationResults, item => item.SceneLabel == "Rear lot" && item.Location.SourceLabel == "row-scene-2");
        Assert.DoesNotContain(result.LocationResults, item => item.Location.SourceLabel == "row-far");
        Assert.DoesNotContain(result.LocationResults, item => item.Location.SourceLabel == "row-late");
        Assert.All(result.LocationResults, item => Assert.InRange(item.DistanceMeters, 0d, 75d));
    }

    [Fact]
    public async Task RunCrossReferenceAsync_BuildsTimelineAnchor_AndPinsRelevantResults()
    {
        await using var fixture = await WorkspaceFixture.CreateAsync();
        var service = fixture.Services.GetRequiredService<IIncidentService>();
        var seeded = await fixture.SeedCaseWithEvidenceAsync("Incident Timeline + Pins");

        await fixture.SeedMessagesAsync(
            seeded.CaseId,
            seeded.EvidenceItemId,
            seeded.ThreadId,
            [
                CreateMessage(seeded.CaseId, seeded.EvidenceItemId, seeded.ThreadId, new DateTimeOffset(2026, 3, 10, 2, 5, 0, TimeSpan.Zero), "msg:timeline")
            ]
        );
        await fixture.SeedLocationsAsync(
            seeded.CaseId,
            seeded.EvidenceItemId,
            [
                CreateObservation(seeded.CaseId, seeded.EvidenceItemId, new DateTimeOffset(2026, 3, 10, 2, 7, 0, TimeSpan.Zero), 34.1003, -118.2000, "row:timeline")
            ]
        );

        var incident = await service.SaveIncidentAsync(
            CreateIncident(
                seeded.CaseId,
                title: "Retaliatory assault",
                type: "Assault",
                status: "Open",
                startUtc: new DateTimeOffset(2026, 3, 10, 2, 0, 0, TimeSpan.Zero),
                endUtc: new DateTimeOffset(2026, 3, 10, 2, 30, 0, TimeSpan.Zero),
                primaryOccurrenceUtc: new DateTimeOffset(2026, 3, 10, 2, 10, 0, TimeSpan.Zero),
                locations:
                [
                    new IncidentLocation(Guid.Empty, 0, "Apartment walkway", 34.1000, -118.2000, 80d, string.Empty)
                ]),
            "incident-pins",
            CancellationToken.None
        );

        var result = await service.RunCrossReferenceAsync(
            seeded.CaseId,
            incident.IncidentId,
            "incident-pins-run",
            CancellationToken.None
        );

        Assert.Contains(result.TimelineItems, item => item.IsAnchor && item.MarkerType == "Incident" && item.Title == "Retaliatory assault");
        Assert.Contains(result.TimelineItems, item => item.MarkerType == "Message" && item.SourceLocator == "msg:timeline");
        Assert.Contains(result.TimelineItems, item => item.MarkerType == "Location" && item.SourceLocator == "csv://test#row:timeline");

        var message = Assert.Single(result.MessageResults);
        var location = Assert.Single(result.LocationResults);
        await service.PinMessageAsync(seeded.CaseId, incident.IncidentId, message, "pin-message", CancellationToken.None);
        await service.PinLocationAsync(seeded.CaseId, incident.IncidentId, location, "pin-location", CancellationToken.None);

        var reloaded = await service.GetIncidentAsync(seeded.CaseId, incident.IncidentId, CancellationToken.None);
        Assert.NotNull(reloaded);
        Assert.Equal(2, reloaded.PinnedResults.Count);
        Assert.Contains(reloaded.PinnedResults, item => item.ResultType == "Message" && item.SourceLocator == "msg:timeline");
        Assert.Contains(reloaded.PinnedResults, item => item.ResultType == "Location" && item.SourceLocator == "csv://test#row:timeline");
    }

    private static IncidentRecord CreateIncident(
        Guid caseId,
        string title,
        string type,
        string status,
        DateTimeOffset startUtc,
        DateTimeOffset endUtc,
        DateTimeOffset? primaryOccurrenceUtc,
        IReadOnlyList<IncidentLocation> locations
    )
    {
        return new IncidentRecord(
            IncidentId: Guid.Empty,
            CaseId: caseId,
            Title: title,
            IncidentType: type,
            Status: status,
            SummaryNotes: "Synthetic incident",
            PrimaryOccurrenceUtc: primaryOccurrenceUtc,
            OffenseWindowStartUtc: startUtc,
            OffenseWindowEndUtc: endUtc,
            CreatedUtc: DateTimeOffset.UtcNow,
            UpdatedUtc: DateTimeOffset.UtcNow,
            Locations: locations,
            PinnedResults: Array.Empty<IncidentPinnedResult>()
        );
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
            Body = "synthetic incident evidence",
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
        string sourceLabel
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
            AltitudeMeters = null,
            SpeedMps = null,
            HeadingDegrees = null,
            SourceType = "CSV",
            SourceLabel = sourceLabel,
            SubjectType = null,
            SubjectId = null,
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
        private readonly TestClock _clock;

        private WorkspaceFixture(ServiceProvider provider, TestWorkspacePathProvider pathProvider, TestClock clock)
        {
            _provider = provider;
            _pathProvider = pathProvider;
            _clock = clock;
        }

        public IServiceProvider Services => _provider;

        public void SetUtcNow(DateTimeOffset utcNow)
        {
            _clock.UtcNow = utcNow.ToUniversalTime();
        }

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
            var clock = new TestClock(new DateTimeOffset(2026, 3, 10, 12, 0, 0, TimeSpan.Zero));
            services.AddSingleton<IClock>(clock);
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
            services.AddSingleton<IIncidentService, IncidentService>();

            var provider = services.BuildServiceProvider();
            await provider.GetRequiredService<IWorkspaceDatabaseInitializer>().EnsureInitializedAsync(CancellationToken.None);
            return new WorkspaceFixture(provider, pathProvider, clock);
        }

        public async Task<(Guid CaseId, Guid EvidenceItemId, Guid ThreadId)> SeedCaseWithEvidenceAsync(string caseName)
        {
            var caseId = Guid.NewGuid();
            var evidenceItemId = Guid.NewGuid();
            var threadId = Guid.NewGuid();
            var createdAt = new DateTimeOffset(2026, 3, 7, 0, 0, 0, TimeSpan.Zero);

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
                SourceLocator = "thread:test",
                IngestModuleVersion = "test"
            });
            await db.SaveChangesAsync();
            return (caseId, evidenceItemId, threadId);
        }

        public async Task SeedMessagesAsync(Guid caseId, Guid evidenceItemId, Guid threadId, IReadOnlyList<MessageEventRecord> messages)
        {
            await using var db = await CreateDbContextAsync();
            db.MessageEvents.AddRange(messages);
            await db.SaveChangesAsync();
        }

        public async Task SeedLocationsAsync(Guid caseId, Guid evidenceItemId, IReadOnlyList<LocationObservationRecord> observations)
        {
            await using var db = await CreateDbContextAsync();
            db.LocationObservations.AddRange(observations);
            await db.SaveChangesAsync();
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

    private sealed class TestClock : IClock
    {
        public TestClock(DateTimeOffset utcNow)
        {
            UtcNow = utcNow.ToUniversalTime();
        }

        public DateTimeOffset UtcNow { get; set; }
    }
}
