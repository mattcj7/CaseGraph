using CaseGraph.Core.Abstractions;
using CaseGraph.Infrastructure.Locations;
using CaseGraph.Infrastructure.Persistence;
using CaseGraph.Infrastructure.Persistence.Entities;
using CaseGraph.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using System.Text.Json;

namespace CaseGraph.Infrastructure.Tests;

public sealed class LocationQueryServiceTests
{
    [Fact]
    public async Task SearchAsync_OrdersDescending_AndAppliesFilters()
    {
        await using var fixture = await WorkspaceFixture.CreateAsync();
        var service = fixture.Services.GetRequiredService<LocationQueryService>();
        var seeded = await fixture.SeedCaseWithEvidenceAsync("Location Query Case");

        var targetId = Guid.NewGuid();
        var t1 = new DateTimeOffset(2026, 3, 1, 10, 0, 0, TimeSpan.Zero);
        var t2 = t1.AddMinutes(5);
        var t3 = t2.AddMinutes(5);

        await using (var db = await fixture.CreateDbContextAsync())
        {
            db.Targets.Add(new TargetRecord
            {
                TargetId = targetId,
                CaseId = seeded.CaseId,
                DisplayName = "Alpha",
                PrimaryAlias = null,
                Notes = null,
                CreatedAtUtc = t1,
                UpdatedAtUtc = t1,
                SourceType = "Manual",
                SourceEvidenceItemId = null,
                SourceLocator = "manual:target",
                IngestModuleVersion = "test"
            });

            db.LocationObservations.AddRange(
                CreateObservation(seeded.CaseId, seeded.EvidenceItemId, t1, 34.01, -118.11, "CSV", "row1"),
                CreateObservation(seeded.CaseId, seeded.EvidenceItemId, t2, 34.02, -118.12, "JSON", "row2", accuracy: 12),
                CreateObservation(
                    seeded.CaseId,
                    seeded.EvidenceItemId,
                    t3,
                    34.03,
                    -118.13,
                    "CSV",
                    "row3",
                    accuracy: 5,
                    subjectType: "Target",
                    subjectId: targetId
                )
            );

            await db.SaveChangesAsync();
        }

        var page = await service.SearchAsync(
            new LocationQueryRequest(
                CaseId: seeded.CaseId,
                FromUtc: null,
                ToUtc: null,
                MinAccuracyMeters: null,
                MaxAccuracyMeters: null,
                SourceType: null,
                SubjectType: null,
                SubjectId: null,
                Take: 20,
                Skip: 0,
                CorrelationId: "locations-order"
            ),
            CancellationToken.None
        );

        Assert.Equal(3, page.TotalCount);
        Assert.Equal("row3", page.Rows[0].SourceLabel);
        Assert.Equal("row2", page.Rows[1].SourceLabel);
        Assert.Equal("row1", page.Rows[2].SourceLabel);

        var filtered = await service.SearchAsync(
            new LocationQueryRequest(
                CaseId: seeded.CaseId,
                FromUtc: null,
                ToUtc: null,
                MinAccuracyMeters: 0,
                MaxAccuracyMeters: 6,
                SourceType: "CSV",
                SubjectType: "Target",
                SubjectId: targetId,
                Take: 20,
                Skip: 0,
                CorrelationId: "locations-filtered"
            ),
            CancellationToken.None
        );

        var row = Assert.Single(filtered.Rows);
        Assert.Equal("row3", row.SourceLabel);
        Assert.Equal("Alpha", row.SubjectDisplayName);
    }

    [Fact]
    public async Task SearchAsync_WritesAuditEventWithCorrelationAndResultCount()
    {
        await using var fixture = await WorkspaceFixture.CreateAsync();
        var service = fixture.Services.GetRequiredService<LocationQueryService>();
        var seeded = await fixture.SeedCaseWithEvidenceAsync("Location Audit Case");
        var timestamp = new DateTimeOffset(2026, 3, 1, 12, 0, 0, TimeSpan.Zero);

        await using (var db = await fixture.CreateDbContextAsync())
        {
            db.LocationObservations.Add(
                CreateObservation(
                    seeded.CaseId,
                    seeded.EvidenceItemId,
                    timestamp,
                    34.01,
                    -118.11,
                    "CSV",
                    "audit-row"
                )
            );
            await db.SaveChangesAsync();
        }

        var correlationId = "locations-audit-correlation";
        var page = await service.SearchAsync(
            new LocationQueryRequest(
                CaseId: seeded.CaseId,
                FromUtc: null,
                ToUtc: null,
                MinAccuracyMeters: null,
                MaxAccuracyMeters: null,
                SourceType: null,
                SubjectType: null,
                SubjectId: null,
                Take: 20,
                Skip: 0,
                CorrelationId: correlationId
            ),
            CancellationToken.None
        );

        Assert.Equal(1, page.TotalCount);

        await using var verifyDb = await fixture.CreateDbContextAsync();
        var audit = (await verifyDb.AuditEvents
            .AsNoTracking()
            .Where(item => item.ActionType == "LocationsSearchExecuted")
            .ToListAsync())
            .OrderByDescending(item => item.TimestampUtc)
            .FirstOrDefault();

        Assert.NotNull(audit);
        Assert.Equal(seeded.CaseId, audit.CaseId);
        Assert.Contains("returned 1 row", audit.Summary, StringComparison.OrdinalIgnoreCase);

        Assert.False(string.IsNullOrWhiteSpace(audit.JsonPayload));
        using var payload = JsonDocument.Parse(audit.JsonPayload);
        Assert.Equal(correlationId, payload.RootElement.GetProperty("CorrelationId").GetString());
        Assert.Equal(1, payload.RootElement.GetProperty("ResultCount").GetInt32());
    }

    private static LocationObservationRecord CreateObservation(
        Guid caseId,
        Guid evidenceItemId,
        DateTimeOffset observedUtc,
        double latitude,
        double longitude,
        string sourceType,
        string sourceLabel,
        double? accuracy = null,
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
            AccuracyMeters = accuracy,
            AltitudeMeters = null,
            SpeedMps = null,
            HeadingDegrees = null,
            SourceType = sourceType,
            SourceLabel = sourceLabel,
            SubjectType = subjectType,
            SubjectId = subjectId,
            SourceEvidenceItemId = evidenceItemId,
            SourceLocator = $"{sourceType.ToLowerInvariant()}://test#row={sourceLabel}",
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
                Guid.NewGuid().ToString("N")
            );
            Directory.CreateDirectory(workspaceRoot);

            var pathProvider = new TestWorkspacePathProvider(workspaceRoot);
            var services = new ServiceCollection();
            services.AddSingleton<IClock>(
                new FixedClock(new DateTimeOffset(2026, 3, 1, 9, 0, 0, TimeSpan.Zero))
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
            services.AddSingleton<LocationQueryService>();

            var provider = services.BuildServiceProvider();
            var initializer = provider.GetRequiredService<IWorkspaceDatabaseInitializer>();
            await initializer.EnsureInitializedAsync(CancellationToken.None);

            return new WorkspaceFixture(provider, pathProvider);
        }

        public async Task<(Guid CaseId, Guid EvidenceItemId)> SeedCaseWithEvidenceAsync(string caseName)
        {
            var caseId = Guid.NewGuid();
            var evidenceItemId = Guid.NewGuid();
            var createdAt = new DateTimeOffset(2026, 3, 1, 9, 0, 0, TimeSpan.Zero);

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
                OriginalFileName = "synthetic.csv",
                AddedAtUtc = createdAt,
                SizeBytes = 1,
                Sha256Hex = "ab",
                FileExtension = ".csv",
                SourceType = "OTHER",
                ManifestRelativePath = "manifest.json",
                StoredRelativePath = "stored.dat"
            });

            await db.SaveChangesAsync();
            return (caseId, evidenceItemId);
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
