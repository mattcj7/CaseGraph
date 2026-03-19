using CaseGraph.Core.Models;
using CaseGraph.Infrastructure.GangDocumentation;
using CaseGraph.Infrastructure.Organizations;
using CaseGraph.Infrastructure.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using System.Text.Json;

namespace CaseGraph.Infrastructure.Tests;

public sealed class GangDocumentationPacketExportTests
{
    [Fact]
    public async Task BuildPreviewAsync_ApprovedRecord_AssemblesExpectedPacket()
    {
        await using var fixture = await GangDocumentationTestWorkspaceFixture.CreateAsync();
        var exportService = fixture.Services.GetRequiredService<IGangDocumentationPacketExportService>();
        var seeded = await SeedApprovedScenarioAsync(fixture);

        var preview = await exportService.BuildPreviewAsync(
            new GangDocumentationPacketBuildRequest(
                seeded.CaseId,
                seeded.DocumentationId,
                "packet.tester"
            ),
            CancellationToken.None
        );

        Assert.Equal(seeded.CaseId, preview.CaseId);
        Assert.Equal(seeded.DocumentationId, preview.DocumentationId);
        Assert.Equal(seeded.TargetId, preview.TargetId);
        Assert.Equal("Marcus Lane", preview.Subject.SubjectDisplayName);
        Assert.Equal("Rollin 60s", preview.Subject.OrganizationName);
        Assert.Equal("Rollin 60s West", preview.Subject.SubgroupOrganizationName);
        Assert.Equal("member", preview.Subject.AffiliationRole);
        Assert.Equal("Approved", preview.Subject.WorkflowStatusDisplay);
        Assert.Equal(2, preview.AnalystContent.Criteria.Count);
        Assert.Equal("Detective Hale", preview.Workflow.ReviewerName);
        Assert.Equal("Approved for export packet.", preview.Workflow.DecisionNote);
        Assert.Equal(2, preview.Evidence.SupportingItems.Count);
        Assert.Single(preview.Context.LinkedCases);
        Assert.Equal(seeded.LinkedCaseId, preview.Context.LinkedCases[0].CaseId);
        Assert.Equal("Analyst-entered basis and criteria", preview.AnalystContent.SectionLabel);
        Assert.Equal("Supervisor approval metadata", preview.Workflow.SectionLabel);
        Assert.Equal("Evidence-derived supporting references", preview.Evidence.SectionLabel);
    }

    [Fact]
    public async Task BuildPreviewAsync_PreservesEvidenceAndAnalystCitations()
    {
        await using var fixture = await GangDocumentationTestWorkspaceFixture.CreateAsync();
        var exportService = fixture.Services.GetRequiredService<IGangDocumentationPacketExportService>();
        var seeded = await SeedApprovedScenarioAsync(fixture);

        var preview = await exportService.BuildPreviewAsync(
            new GangDocumentationPacketBuildRequest(
                seeded.CaseId,
                seeded.DocumentationId,
                "packet.tester"
            ),
            CancellationToken.None
        );

        Assert.Contains(
            preview.AllCitations,
            citation => citation.SourceKindLabel == "Analyst-entered reference"
                && citation.SourceLocator == "FI card 12"
        );
        Assert.Contains(
            preview.AllCitations,
            citation => citation.SourceKindLabel == "Evidence-derived message reference"
                && citation.SourceLocator == "xlsx:test#Messages:R2"
                && citation.MessageEventId == seeded.SecondMessageEventId
        );
    }

    [Fact]
    public async Task BuildPreviewAsync_UnapprovedRecord_IsBlocked()
    {
        await using var fixture = await GangDocumentationTestWorkspaceFixture.CreateAsync();
        var exportService = fixture.Services.GetRequiredService<IGangDocumentationPacketExportService>();
        var documentationService = fixture.Services.GetRequiredService<IGangDocumentationService>();
        var organizationService = fixture.Services.GetRequiredService<IOrganizationService>();

        var target = await fixture.CreateTargetAsync("Draft Subject", createGlobalPerson: true);
        var organization = await organizationService.CreateOrganizationAsync(
            new CreateOrganizationRequest("Neighborhood Crips", "gang", "active", null, null),
            CancellationToken.None
        );
        var documentation = await documentationService.CreateDocumentationAsync(
            new CreateGangDocumentationRequest(
                target.CaseId,
                target.TargetId,
                organization.OrganizationId,
                null,
                "associate",
                "Draft-only documentation.",
                null
            ),
            CancellationToken.None
        );

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => exportService.BuildPreviewAsync(
            new GangDocumentationPacketBuildRequest(
                target.CaseId,
                documentation.DocumentationId,
                "packet.tester"
            ),
            CancellationToken.None
        ));

        Assert.Contains("Only Approved gang documentation records can be exported", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExportHtmlAsync_WritesHtmlAndAuditsExport()
    {
        await using var fixture = await GangDocumentationTestWorkspaceFixture.CreateAsync();
        var exportService = fixture.Services.GetRequiredService<IGangDocumentationPacketExportService>();
        var seeded = await SeedApprovedScenarioAsync(fixture);
        var preview = await exportService.BuildPreviewAsync(
            new GangDocumentationPacketBuildRequest(
                seeded.CaseId,
                seeded.DocumentationId,
                "packet.tester"
            ),
            CancellationToken.None
        );

        var exportDirectory = Path.Combine(
            Path.GetTempPath(),
            "CaseGraph.Infrastructure.Tests",
            Guid.NewGuid().ToString("N")
        );
        Directory.CreateDirectory(exportDirectory);

        try
        {
            var result = await exportService.ExportHtmlAsync(
                preview,
                Path.Combine(exportDirectory, "Gang:Packet?.html"),
                CancellationToken.None
            );

            Assert.True(File.Exists(result.OutputPath));
            Assert.EndsWith("Gang_Packet_.html", result.OutputPath, StringComparison.OrdinalIgnoreCase);

            var html = await File.ReadAllTextAsync(result.OutputPath, CancellationToken.None);
            Assert.Contains("Marcus Lane Gang Documentation Packet", html, StringComparison.Ordinal);
            Assert.Contains("Supervisor approval metadata", html, StringComparison.Ordinal);
            Assert.Contains("Detective Hale", html, StringComparison.Ordinal);
            Assert.Contains("Approved for export packet.", html, StringComparison.Ordinal);
            Assert.Contains("xlsx:test#Messages:R2", html, StringComparison.Ordinal);

            await using var db = await fixture.CreateDbContextAsync();
            var audit = (await db.AuditEvents
                .AsNoTracking()
                .Where(record => record.CaseId == seeded.CaseId && record.ActionType == "GangDocumentationPacketExported")
                .ToListAsync())
                .OrderByDescending(record => record.TimestampUtc)
                .FirstOrDefault();

            Assert.NotNull(audit);
            Assert.Contains(result.OutputPath, audit!.Summary, StringComparison.OrdinalIgnoreCase);

            using var payload = JsonDocument.Parse(audit.JsonPayload!);
            Assert.Equal(seeded.DocumentationId, payload.RootElement.GetProperty("DocumentationId").GetGuid());
            Assert.Equal(result.OutputPath, payload.RootElement.GetProperty("OutputPath").GetString());
            Assert.Equal("Detective Hale", payload.RootElement.GetProperty("ReviewerName").GetString());
        }
        finally
        {
            if (Directory.Exists(exportDirectory))
            {
                Directory.Delete(exportDirectory, recursive: true);
            }
        }
    }

    private static async Task<SeededPacketScenario> SeedApprovedScenarioAsync(
        GangDocumentationTestWorkspaceFixture fixture
    )
    {
        var documentationService = fixture.Services.GetRequiredService<IGangDocumentationService>();
        var organizationService = fixture.Services.GetRequiredService<IOrganizationService>();

        var target = await fixture.CreateTargetAsync("Marcus Lane", createGlobalPerson: true);
        var linkedTarget = await fixture.CreateTargetAsync("Marcus Lane West", createGlobalPerson: false);
        var organization = await organizationService.CreateOrganizationAsync(
            new CreateOrganizationRequest("Rollin 60s", "gang", "active", null, null),
            CancellationToken.None
        );
        var subgroup = await organizationService.CreateOrganizationAsync(
            new CreateOrganizationRequest("Rollin 60s West", "set", "active", organization.OrganizationId, null),
            CancellationToken.None
        );

        await using (var db = await fixture.CreateDbContextAsync())
        {
            var linkedTargetRecord = await db.Targets.FirstAsync(record => record.TargetId == linkedTarget.TargetId);
            linkedTargetRecord.GlobalEntityId = target.GlobalEntityId;
            await db.SaveChangesAsync();
        }

        var documentation = await documentationService.CreateDocumentationAsync(
            new CreateGangDocumentationRequest(
                target.CaseId,
                target.TargetId,
                organization.OrganizationId,
                subgroup.OrganizationId,
                "member",
                "Documented with multiple criteria and supporting evidence.",
                "Analyst entered documentation note."
            ),
            CancellationToken.None
        );

        await documentationService.SaveCriterionAsync(
            new SaveGangDocumentationCriterionRequest(
                target.CaseId,
                documentation.DocumentationId,
                null,
                "self-admission",
                true,
                "Subject admitted affiliation during interview.",
                new DateTimeOffset(2026, 3, 10, 0, 0, 0, TimeSpan.Zero),
                "FI card 12"
            ),
            CancellationToken.None
        );
        await documentationService.SaveCriterionAsync(
            new SaveGangDocumentationCriterionRequest(
                target.CaseId,
                documentation.DocumentationId,
                null,
                "social media evidence",
                true,
                "Photos showed documented association with the linked set.",
                new DateTimeOffset(2026, 3, 11, 0, 0, 0, TimeSpan.Zero),
                null
            ),
            CancellationToken.None
        );

        await documentationService.TransitionWorkflowAsync(
            new TransitionGangDocumentationWorkflowRequest(
                target.CaseId,
                documentation.DocumentationId,
                GangDocumentationCatalog.WorkflowActionSubmitForReview,
                null,
                null
            ),
            CancellationToken.None
        );
        await documentationService.TransitionWorkflowAsync(
            new TransitionGangDocumentationWorkflowRequest(
                target.CaseId,
                documentation.DocumentationId,
                GangDocumentationCatalog.WorkflowActionApprove,
                "Detective Hale",
                "Approved for export packet."
            ),
            CancellationToken.None
        );

        var seededEvidence = await SeedSupportingEvidenceAsync(
            fixture,
            target.CaseId,
            target.TargetId
        );

        return new SeededPacketScenario(
            target.CaseId,
            documentation.DocumentationId,
            target.TargetId,
            linkedTarget.CaseId,
            seededEvidence.FirstMessageEventId,
            seededEvidence.SecondMessageEventId
        );
    }

    private static async Task<SeededPacketEvidence> SeedSupportingEvidenceAsync(
        GangDocumentationTestWorkspaceFixture fixture,
        Guid caseId,
        Guid targetId
    )
    {
        var evidenceId = Guid.NewGuid();
        var threadId = Guid.NewGuid();
        var identifierId = Guid.NewGuid();
        var firstMessageEventId = Guid.NewGuid();
        var secondMessageEventId = Guid.NewGuid();
        var t1 = new DateTimeOffset(2026, 3, 12, 15, 0, 0, TimeSpan.Zero);
        var t2 = t1.AddHours(1);

        await using var db = await fixture.CreateDbContextAsync();
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
            ThreadKey = "thread-gang-docs",
            CreatedAtUtc = t1,
            SourceLocator = "test:thread:gang-docs",
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
        db.MessageEvents.AddRange(
            new MessageEventRecord
            {
                MessageEventId = firstMessageEventId,
                ThreadId = threadId,
                CaseId = caseId,
                EvidenceItemId = evidenceId,
                Platform = "SMS",
                TimestampUtc = t1,
                Direction = "Incoming",
                Sender = "+15551230001",
                Recipients = "+15550000001",
                Body = "First gang documentation support message.",
                IsDeleted = false,
                SourceLocator = "xlsx:test#Messages:R1",
                IngestModuleVersion = "test"
            },
            new MessageEventRecord
            {
                MessageEventId = secondMessageEventId,
                ThreadId = threadId,
                CaseId = caseId,
                EvidenceItemId = evidenceId,
                Platform = "SMS",
                TimestampUtc = t2,
                Direction = "Outgoing",
                Sender = "+15551230001",
                Recipients = "+15550000002",
                Body = "Second support message with stronger association detail.",
                IsDeleted = false,
                SourceLocator = "xlsx:test#Messages:R2",
                IngestModuleVersion = "test"
            }
        );
        db.TargetMessagePresences.AddRange(
            new TargetMessagePresenceRecord
            {
                PresenceId = Guid.NewGuid(),
                CaseId = caseId,
                TargetId = targetId,
                MessageEventId = firstMessageEventId,
                MatchedIdentifierId = identifierId,
                Role = "Sender",
                EvidenceItemId = evidenceId,
                SourceLocator = "xlsx:test#Messages:R1",
                MessageTimestampUtc = t1,
                FirstSeenUtc = t1,
                LastSeenUtc = t1
            },
            new TargetMessagePresenceRecord
            {
                PresenceId = Guid.NewGuid(),
                CaseId = caseId,
                TargetId = targetId,
                MessageEventId = secondMessageEventId,
                MatchedIdentifierId = identifierId,
                Role = "Sender",
                EvidenceItemId = evidenceId,
                SourceLocator = "xlsx:test#Messages:R2",
                MessageTimestampUtc = t2,
                FirstSeenUtc = t2,
                LastSeenUtc = t2
            }
        );
        await db.SaveChangesAsync();

        return new SeededPacketEvidence(firstMessageEventId, secondMessageEventId);
    }

    private sealed record SeededPacketScenario(
        Guid CaseId,
        Guid DocumentationId,
        Guid TargetId,
        Guid LinkedCaseId,
        Guid FirstMessageEventId,
        Guid SecondMessageEventId
    );

    private sealed record SeededPacketEvidence(
        Guid FirstMessageEventId,
        Guid SecondMessageEventId
    );
}
