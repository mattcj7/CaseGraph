using CaseGraph.Core.Abstractions;
using CaseGraph.Core.Models;
using CaseGraph.Infrastructure.Persistence;
using CaseGraph.Infrastructure.Persistence.Entities;
using CaseGraph.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace CaseGraph.Infrastructure.Tests;

public sealed class AssociationGraphQueryServiceTests
{
    [Fact]
    public async Task BuildAsync_IncludesExpectedNodesEdgesAndWeights()
    {
        await using var fixture = await WorkspaceFixture.CreateAsync();
        var service = fixture.Services.GetRequiredService<IAssociationGraphQueryService>();

        var caseId = Guid.NewGuid();
        var evidenceItemId = Guid.NewGuid();
        var targetAlpha = Guid.NewGuid();
        var targetBravo = Guid.NewGuid();
        var identifierAlpha = Guid.NewGuid();
        var identifierBravo = Guid.NewGuid();
        var threadOne = Guid.NewGuid();
        var threadTwo = Guid.NewGuid();
        var messageOne = Guid.NewGuid();
        var messageTwo = Guid.NewGuid();
        var messageThree = Guid.NewGuid();
        var t1 = new DateTimeOffset(2026, 2, 25, 10, 0, 0, TimeSpan.Zero);
        var t2 = t1.AddMinutes(3);
        var t3 = t1.AddMinutes(7);

        await using (var db = await fixture.CreateDbContextAsync())
        {
            db.Cases.Add(new CaseRecord
            {
                CaseId = caseId,
                Name = "Association Graph Base",
                CreatedAtUtc = t1,
                LastOpenedAtUtc = t1
            });

            db.EvidenceItems.Add(new EvidenceItemRecord
            {
                EvidenceItemId = evidenceItemId,
                CaseId = caseId,
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

            db.MessageThreads.AddRange(
                new MessageThreadRecord
                {
                    ThreadId = threadOne,
                    CaseId = caseId,
                    EvidenceItemId = evidenceItemId,
                    Platform = "SMS",
                    ThreadKey = "thread-one",
                    CreatedAtUtc = t1,
                    SourceLocator = "test:thread:1",
                    IngestModuleVersion = "test"
                },
                new MessageThreadRecord
                {
                    ThreadId = threadTwo,
                    CaseId = caseId,
                    EvidenceItemId = evidenceItemId,
                    Platform = "SMS",
                    ThreadKey = "thread-two",
                    CreatedAtUtc = t1,
                    SourceLocator = "test:thread:2",
                    IngestModuleVersion = "test"
                }
            );

            db.MessageEvents.AddRange(
                new MessageEventRecord
                {
                    MessageEventId = messageOne,
                    ThreadId = threadOne,
                    CaseId = caseId,
                    EvidenceItemId = evidenceItemId,
                    Platform = "SMS",
                    TimestampUtc = t1,
                    Direction = "Incoming",
                    Sender = "+1555000001",
                    Recipients = "+1555000002",
                    Body = "M1",
                    IsDeleted = false,
                    SourceLocator = "xlsx:test#Messages:R1",
                    IngestModuleVersion = "test"
                },
                new MessageEventRecord
                {
                    MessageEventId = messageTwo,
                    ThreadId = threadOne,
                    CaseId = caseId,
                    EvidenceItemId = evidenceItemId,
                    Platform = "SMS",
                    TimestampUtc = t2,
                    Direction = "Incoming",
                    Sender = "+1555000001",
                    Recipients = "+1555000002",
                    Body = "M2",
                    IsDeleted = false,
                    SourceLocator = "xlsx:test#Messages:R2",
                    IngestModuleVersion = "test"
                },
                new MessageEventRecord
                {
                    MessageEventId = messageThree,
                    ThreadId = threadTwo,
                    CaseId = caseId,
                    EvidenceItemId = evidenceItemId,
                    Platform = "SMS",
                    TimestampUtc = t3,
                    Direction = "Incoming",
                    Sender = "+1555000001",
                    Recipients = "+1555000002",
                    Body = "M3",
                    IsDeleted = false,
                    SourceLocator = "xlsx:test#Messages:R3",
                    IngestModuleVersion = "test"
                }
            );

            db.Targets.AddRange(
                new TargetRecord
                {
                    TargetId = targetAlpha,
                    CaseId = caseId,
                    DisplayName = "Alpha",
                    PrimaryAlias = null,
                    Notes = null,
                    CreatedAtUtc = t1,
                    UpdatedAtUtc = t1,
                    SourceType = "Manual",
                    SourceEvidenceItemId = null,
                    SourceLocator = "manual:test:target-alpha",
                    IngestModuleVersion = "test"
                },
                new TargetRecord
                {
                    TargetId = targetBravo,
                    CaseId = caseId,
                    DisplayName = "Bravo",
                    PrimaryAlias = null,
                    Notes = null,
                    CreatedAtUtc = t1,
                    UpdatedAtUtc = t1,
                    SourceType = "Manual",
                    SourceEvidenceItemId = null,
                    SourceLocator = "manual:test:target-bravo",
                    IngestModuleVersion = "test"
                }
            );

            db.Identifiers.AddRange(
                new IdentifierRecord
                {
                    IdentifierId = identifierAlpha,
                    CaseId = caseId,
                    Type = "Phone",
                    ValueRaw = "+1555000001",
                    ValueNormalized = "+1555000001",
                    Notes = null,
                    CreatedAtUtc = t1,
                    SourceType = "Manual",
                    SourceEvidenceItemId = null,
                    SourceLocator = "manual:test:id-alpha",
                    IngestModuleVersion = "test"
                },
                new IdentifierRecord
                {
                    IdentifierId = identifierBravo,
                    CaseId = caseId,
                    Type = "Phone",
                    ValueRaw = "+1555000002",
                    ValueNormalized = "+1555000002",
                    Notes = null,
                    CreatedAtUtc = t1,
                    SourceType = "Manual",
                    SourceEvidenceItemId = null,
                    SourceLocator = "manual:test:id-bravo",
                    IngestModuleVersion = "test"
                }
            );

            db.TargetIdentifierLinks.AddRange(
                new TargetIdentifierLinkRecord
                {
                    LinkId = Guid.NewGuid(),
                    CaseId = caseId,
                    TargetId = targetAlpha,
                    IdentifierId = identifierAlpha,
                    IsPrimary = true,
                    CreatedAtUtc = t1,
                    SourceType = "Manual",
                    SourceEvidenceItemId = null,
                    SourceLocator = "manual:test:link-alpha",
                    IngestModuleVersion = "test"
                },
                new TargetIdentifierLinkRecord
                {
                    LinkId = Guid.NewGuid(),
                    CaseId = caseId,
                    TargetId = targetBravo,
                    IdentifierId = identifierBravo,
                    IsPrimary = true,
                    CreatedAtUtc = t1,
                    SourceType = "Manual",
                    SourceEvidenceItemId = null,
                    SourceLocator = "manual:test:link-bravo",
                    IngestModuleVersion = "test"
                }
            );

            var presences = new[]
            {
                (MessageEventId: messageOne, TargetId: targetAlpha, IdentifierId: identifierAlpha, SeenUtc: t1),
                (MessageEventId: messageOne, TargetId: targetBravo, IdentifierId: identifierBravo, SeenUtc: t1),
                (MessageEventId: messageTwo, TargetId: targetAlpha, IdentifierId: identifierAlpha, SeenUtc: t2),
                (MessageEventId: messageTwo, TargetId: targetBravo, IdentifierId: identifierBravo, SeenUtc: t2),
                (MessageEventId: messageThree, TargetId: targetAlpha, IdentifierId: identifierAlpha, SeenUtc: t3),
                (MessageEventId: messageThree, TargetId: targetBravo, IdentifierId: identifierBravo, SeenUtc: t3)
            };
            foreach (var presence in presences)
            {
                db.TargetMessagePresences.Add(new TargetMessagePresenceRecord
                {
                    PresenceId = Guid.NewGuid(),
                    CaseId = caseId,
                    TargetId = presence.TargetId,
                    MessageEventId = presence.MessageEventId,
                    MatchedIdentifierId = presence.IdentifierId,
                    Role = "Sender",
                    EvidenceItemId = evidenceItemId,
                    SourceLocator = $"test:presence:{presence.MessageEventId:D}:{presence.TargetId:D}",
                    MessageTimestampUtc = presence.SeenUtc,
                    FirstSeenUtc = presence.SeenUtc,
                    LastSeenUtc = presence.SeenUtc
                });
            }

            await db.SaveChangesAsync();
        }

        var graph = await service.BuildAsync(
            caseId,
            new AssociationGraphBuildOptions(
                IncludeIdentifiers: true,
                GroupByGlobalPerson: false,
                MinEdgeWeight: 2
            ),
            CancellationToken.None
        );

        Assert.Equal(4, graph.Nodes.Count);
        Assert.Equal(3, graph.Edges.Count);
        Assert.Equal(2, graph.Nodes.Count(node => node.Kind == AssociationGraphNodeKind.Target));
        Assert.Equal(2, graph.Nodes.Count(node => node.Kind == AssociationGraphNodeKind.Identifier));

        var alphaNodeId = $"target:{targetAlpha:D}";
        var bravoNodeId = $"target:{targetBravo:D}";
        var alphaIdentifierNodeId = $"identifier:{identifierAlpha:D}";
        var bravoIdentifierNodeId = $"identifier:{identifierBravo:D}";

        Assert.Contains(graph.Nodes, node => node.NodeId == alphaNodeId);
        Assert.Contains(graph.Nodes, node => node.NodeId == bravoNodeId);
        Assert.Contains(graph.Nodes, node => node.NodeId == alphaIdentifierNodeId);
        Assert.Contains(graph.Nodes, node => node.NodeId == bravoIdentifierNodeId);

        var targetEdge = Assert.Single(graph.Edges.Where(edge => edge.Kind == AssociationGraphEdgeKind.TargetTarget));
        Assert.Equal(2, targetEdge.Weight);
        Assert.Equal(2, targetEdge.DistinctThreadCount);
        Assert.Equal(3, targetEdge.DistinctEventCount);
        Assert.True(
            (targetEdge.SourceNodeId == alphaNodeId && targetEdge.TargetNodeId == bravoNodeId)
            || (targetEdge.SourceNodeId == bravoNodeId && targetEdge.TargetNodeId == alphaNodeId)
        );

        var identifierEdges = graph.Edges
            .Where(edge => edge.Kind == AssociationGraphEdgeKind.TargetIdentifier)
            .ToList();
        Assert.Equal(2, identifierEdges.Count);
        Assert.All(identifierEdges, edge => Assert.Equal(3, edge.Weight));
    }

    [Fact]
    public async Task BuildAsync_GroupByGlobalPerson_CollapsesNodesAndMergesEdges()
    {
        await using var fixture = await WorkspaceFixture.CreateAsync();
        var service = fixture.Services.GetRequiredService<IAssociationGraphQueryService>();

        var caseId = Guid.NewGuid();
        var evidenceItemId = Guid.NewGuid();
        var personOne = Guid.NewGuid();
        var personTwo = Guid.NewGuid();
        var targetOne = Guid.NewGuid();
        var targetTwo = Guid.NewGuid();
        var targetThree = Guid.NewGuid();
        var identifierOne = Guid.NewGuid();
        var identifierTwo = Guid.NewGuid();
        var identifierThree = Guid.NewGuid();
        var threadOne = Guid.NewGuid();
        var threadTwo = Guid.NewGuid();
        var messageOne = Guid.NewGuid();
        var messageTwo = Guid.NewGuid();
        var t1 = new DateTimeOffset(2026, 2, 25, 12, 0, 0, TimeSpan.Zero);
        var t2 = t1.AddMinutes(10);

        await using (var db = await fixture.CreateDbContextAsync())
        {
            db.Cases.Add(new CaseRecord
            {
                CaseId = caseId,
                Name = "Association Global Group",
                CreatedAtUtc = t1,
                LastOpenedAtUtc = t1
            });

            db.PersonEntities.AddRange(
                new PersonEntityRecord
                {
                    GlobalEntityId = personOne,
                    DisplayName = "Person One",
                    CreatedAtUtc = t1,
                    UpdatedAtUtc = t1
                },
                new PersonEntityRecord
                {
                    GlobalEntityId = personTwo,
                    DisplayName = "Person Two",
                    CreatedAtUtc = t1,
                    UpdatedAtUtc = t1
                }
            );

            db.EvidenceItems.Add(new EvidenceItemRecord
            {
                EvidenceItemId = evidenceItemId,
                CaseId = caseId,
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

            db.MessageThreads.AddRange(
                new MessageThreadRecord
                {
                    ThreadId = threadOne,
                    CaseId = caseId,
                    EvidenceItemId = evidenceItemId,
                    Platform = "SMS",
                    ThreadKey = "thread-one",
                    CreatedAtUtc = t1,
                    SourceLocator = "test:thread:1",
                    IngestModuleVersion = "test"
                },
                new MessageThreadRecord
                {
                    ThreadId = threadTwo,
                    CaseId = caseId,
                    EvidenceItemId = evidenceItemId,
                    Platform = "SMS",
                    ThreadKey = "thread-two",
                    CreatedAtUtc = t1,
                    SourceLocator = "test:thread:2",
                    IngestModuleVersion = "test"
                }
            );

            db.MessageEvents.AddRange(
                new MessageEventRecord
                {
                    MessageEventId = messageOne,
                    ThreadId = threadOne,
                    CaseId = caseId,
                    EvidenceItemId = evidenceItemId,
                    Platform = "SMS",
                    TimestampUtc = t1,
                    Direction = "Incoming",
                    Sender = "p1",
                    Recipients = "p2",
                    Body = "M1",
                    IsDeleted = false,
                    SourceLocator = "xlsx:test#Messages:R10",
                    IngestModuleVersion = "test"
                },
                new MessageEventRecord
                {
                    MessageEventId = messageTwo,
                    ThreadId = threadTwo,
                    CaseId = caseId,
                    EvidenceItemId = evidenceItemId,
                    Platform = "SMS",
                    TimestampUtc = t2,
                    Direction = "Incoming",
                    Sender = "p1",
                    Recipients = "p2",
                    Body = "M2",
                    IsDeleted = false,
                    SourceLocator = "xlsx:test#Messages:R11",
                    IngestModuleVersion = "test"
                }
            );

            db.Targets.AddRange(
                new TargetRecord
                {
                    TargetId = targetOne,
                    CaseId = caseId,
                    GlobalEntityId = personOne,
                    DisplayName = "Target One",
                    PrimaryAlias = null,
                    Notes = null,
                    CreatedAtUtc = t1,
                    UpdatedAtUtc = t1,
                    SourceType = "Manual",
                    SourceEvidenceItemId = null,
                    SourceLocator = "manual:test:target-one",
                    IngestModuleVersion = "test"
                },
                new TargetRecord
                {
                    TargetId = targetTwo,
                    CaseId = caseId,
                    GlobalEntityId = personOne,
                    DisplayName = "Target Two",
                    PrimaryAlias = null,
                    Notes = null,
                    CreatedAtUtc = t1,
                    UpdatedAtUtc = t1,
                    SourceType = "Manual",
                    SourceEvidenceItemId = null,
                    SourceLocator = "manual:test:target-two",
                    IngestModuleVersion = "test"
                },
                new TargetRecord
                {
                    TargetId = targetThree,
                    CaseId = caseId,
                    GlobalEntityId = personTwo,
                    DisplayName = "Target Three",
                    PrimaryAlias = null,
                    Notes = null,
                    CreatedAtUtc = t1,
                    UpdatedAtUtc = t1,
                    SourceType = "Manual",
                    SourceEvidenceItemId = null,
                    SourceLocator = "manual:test:target-three",
                    IngestModuleVersion = "test"
                }
            );

            db.Identifiers.AddRange(
                new IdentifierRecord
                {
                    IdentifierId = identifierOne,
                    CaseId = caseId,
                    Type = "Phone",
                    ValueRaw = "+1555000001",
                    ValueNormalized = "+1555000001",
                    Notes = null,
                    CreatedAtUtc = t1,
                    SourceType = "Manual",
                    SourceEvidenceItemId = null,
                    SourceLocator = "manual:test:id-one",
                    IngestModuleVersion = "test"
                },
                new IdentifierRecord
                {
                    IdentifierId = identifierTwo,
                    CaseId = caseId,
                    Type = "Phone",
                    ValueRaw = "+1555000002",
                    ValueNormalized = "+1555000002",
                    Notes = null,
                    CreatedAtUtc = t1,
                    SourceType = "Manual",
                    SourceEvidenceItemId = null,
                    SourceLocator = "manual:test:id-two",
                    IngestModuleVersion = "test"
                },
                new IdentifierRecord
                {
                    IdentifierId = identifierThree,
                    CaseId = caseId,
                    Type = "Phone",
                    ValueRaw = "+1555000003",
                    ValueNormalized = "+1555000003",
                    Notes = null,
                    CreatedAtUtc = t1,
                    SourceType = "Manual",
                    SourceEvidenceItemId = null,
                    SourceLocator = "manual:test:id-three",
                    IngestModuleVersion = "test"
                }
            );

            db.TargetIdentifierLinks.AddRange(
                new TargetIdentifierLinkRecord
                {
                    LinkId = Guid.NewGuid(),
                    CaseId = caseId,
                    TargetId = targetOne,
                    IdentifierId = identifierOne,
                    IsPrimary = true,
                    CreatedAtUtc = t1,
                    SourceType = "Manual",
                    SourceEvidenceItemId = null,
                    SourceLocator = "manual:test:link-one",
                    IngestModuleVersion = "test"
                },
                new TargetIdentifierLinkRecord
                {
                    LinkId = Guid.NewGuid(),
                    CaseId = caseId,
                    TargetId = targetTwo,
                    IdentifierId = identifierTwo,
                    IsPrimary = true,
                    CreatedAtUtc = t1,
                    SourceType = "Manual",
                    SourceEvidenceItemId = null,
                    SourceLocator = "manual:test:link-two",
                    IngestModuleVersion = "test"
                },
                new TargetIdentifierLinkRecord
                {
                    LinkId = Guid.NewGuid(),
                    CaseId = caseId,
                    TargetId = targetThree,
                    IdentifierId = identifierThree,
                    IsPrimary = true,
                    CreatedAtUtc = t1,
                    SourceType = "Manual",
                    SourceEvidenceItemId = null,
                    SourceLocator = "manual:test:link-three",
                    IngestModuleVersion = "test"
                }
            );

            db.TargetMessagePresences.AddRange(
                new TargetMessagePresenceRecord
                {
                    PresenceId = Guid.NewGuid(),
                    CaseId = caseId,
                    TargetId = targetOne,
                    MessageEventId = messageOne,
                    MatchedIdentifierId = identifierOne,
                    Role = "Sender",
                    EvidenceItemId = evidenceItemId,
                    SourceLocator = "test:presence:1:1",
                    MessageTimestampUtc = t1,
                    FirstSeenUtc = t1,
                    LastSeenUtc = t1
                },
                new TargetMessagePresenceRecord
                {
                    PresenceId = Guid.NewGuid(),
                    CaseId = caseId,
                    TargetId = targetThree,
                    MessageEventId = messageOne,
                    MatchedIdentifierId = identifierThree,
                    Role = "Recipient",
                    EvidenceItemId = evidenceItemId,
                    SourceLocator = "test:presence:1:3",
                    MessageTimestampUtc = t1,
                    FirstSeenUtc = t1,
                    LastSeenUtc = t1
                },
                new TargetMessagePresenceRecord
                {
                    PresenceId = Guid.NewGuid(),
                    CaseId = caseId,
                    TargetId = targetTwo,
                    MessageEventId = messageTwo,
                    MatchedIdentifierId = identifierTwo,
                    Role = "Sender",
                    EvidenceItemId = evidenceItemId,
                    SourceLocator = "test:presence:2:2",
                    MessageTimestampUtc = t2,
                    FirstSeenUtc = t2,
                    LastSeenUtc = t2
                },
                new TargetMessagePresenceRecord
                {
                    PresenceId = Guid.NewGuid(),
                    CaseId = caseId,
                    TargetId = targetThree,
                    MessageEventId = messageTwo,
                    MatchedIdentifierId = identifierThree,
                    Role = "Recipient",
                    EvidenceItemId = evidenceItemId,
                    SourceLocator = "test:presence:2:3",
                    MessageTimestampUtc = t2,
                    FirstSeenUtc = t2,
                    LastSeenUtc = t2
                }
            );

            await db.SaveChangesAsync();
        }

        var graph = await service.BuildAsync(
            caseId,
            new AssociationGraphBuildOptions(
                IncludeIdentifiers: false,
                GroupByGlobalPerson: true,
                MinEdgeWeight: 1
            ),
            CancellationToken.None
        );

        Assert.Equal(2, graph.Nodes.Count);
        Assert.All(graph.Nodes, node => Assert.Equal(AssociationGraphNodeKind.GlobalPerson, node.Kind));
        Assert.Single(graph.Edges);

        var edge = graph.Edges[0];
        Assert.Equal(AssociationGraphEdgeKind.TargetTarget, edge.Kind);
        Assert.Equal(2, edge.Weight);
        Assert.Equal(2, edge.DistinctThreadCount);
        Assert.Equal(2, edge.DistinctEventCount);
    }

    [Fact]
    public void ExportPathBuilder_BuildPath_CreatesExportDirectoryAndExpectedName()
    {
        var workspaceRoot = Path.Combine(
            Path.GetTempPath(),
            "CaseGraph.Infrastructure.Tests",
            Guid.NewGuid().ToString("N")
        );

        try
        {
            var pathProvider = new TestWorkspacePathProvider(workspaceRoot);
            var fixedClock = new FixedClock(new DateTimeOffset(2026, 2, 25, 15, 30, 45, TimeSpan.Zero));
            var builder = new AssociationGraphExportPathBuilder(pathProvider, fixedClock);
            var caseId = Guid.NewGuid();

            var outputPath = builder.BuildPath(caseId);

            var expectedDirectory = Path.Combine(workspaceRoot, "session", "exports");
            Assert.True(Directory.Exists(expectedDirectory));
            Assert.StartsWith(expectedDirectory, outputPath, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(
                $"graph-{caseId:D}-20260225-153045.png",
                Path.GetFileName(outputPath)
            );
        }
        finally
        {
            if (Directory.Exists(workspaceRoot))
            {
                Directory.Delete(workspaceRoot, recursive: true);
            }
        }
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
                new FixedClock(new DateTimeOffset(2026, 2, 25, 10, 0, 0, TimeSpan.Zero))
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
            services.AddSingleton<IAssociationGraphQueryService, AssociationGraphQueryService>();

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
