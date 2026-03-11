using CaseGraph.Infrastructure.Import;

namespace CaseGraph.Infrastructure.Tests;

public sealed class MessageCanonicalMappingTests
{
    [Fact]
    public void Create_MapsAliasHeaders_AndTracksUnmappedColumns()
    {
        var map = MessageCanonicalFieldMap.Create(
            new[] { "Sent Time", "From", "To", "Message Content", "Chat ID", "Mystery" }
        );

        Assert.True(map.LooksLikeMessageExport);
        Assert.Contains(CanonicalMessageField.SentUtc, map.MatchedFields);
        Assert.Contains(CanonicalMessageField.SenderDisplay, map.MatchedFields);
        Assert.Contains(CanonicalMessageField.RecipientDisplays, map.MatchedFields);
        Assert.Contains(CanonicalMessageField.Body, map.MatchedFields);
        Assert.Contains(CanonicalMessageField.ThreadExternalId, map.MatchedFields);
        Assert.Single(map.UnmappedHeaders);
        Assert.Equal("Mystery", map.UnmappedHeaders[0]);

        var values = map.ReadRow(
            new[] { "2026-03-10T10:00:00Z", "Alpha", "Bravo", "Checkpoint move", "thread-1", "ignored" }
        );

        Assert.Equal("Checkpoint move", values[CanonicalMessageField.Body]);
        Assert.Equal("Alpha", values[CanonicalMessageField.SenderDisplay]);
        Assert.Equal("Bravo", values[CanonicalMessageField.RecipientDisplays]);
    }

    [Fact]
    public void TryCreate_NormalizesCanonicalRecord_AndPreservesProvenance()
    {
        var sourceEvidenceItemId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
        var parseResult = CanonicalMessageRecord.TryCreate(
            new Dictionary<CanonicalMessageField, string?>
            {
                [CanonicalMessageField.MessageExternalId] = "msg-001",
                [CanonicalMessageField.ThreadExternalId] = "thread-001",
                [CanonicalMessageField.ThreadTitle] = "Ops",
                [CanonicalMessageField.Platform] = "signal",
                [CanonicalMessageField.SentUtc] = "2026-03-10T10:15:00-05:00",
                [CanonicalMessageField.Direction] = "Outgoing",
                [CanonicalMessageField.SenderIdentifier] = "+1 (555) 123-0001",
                [CanonicalMessageField.RecipientIdentifiers] = "+1 (555) 123-0002; +1 (555) 123-0003",
                [CanonicalMessageField.Body] = "Proceed now.",
                [CanonicalMessageField.AttachmentCount] = "2",
                [CanonicalMessageField.DeletedFlag] = "yes"
            },
            new CanonicalMessageContext(
                sourceEvidenceItemId,
                "csv:messages.csv#row=2",
                "CSV",
                "CaseGraph.MessagesIngest/v2",
                PlatformHint: null
            )
        );

        var record = Assert.IsType<CanonicalMessageRecord>(parseResult.Record);
        Assert.True(parseResult.Success);
        Assert.Null(parseResult.SkipReason);
        Assert.Equal("msg-001", record.MessageExternalId);
        Assert.Equal("thread-001", record.ThreadExternalId);
        Assert.Equal("Signal", record.Platform);
        Assert.Equal(new DateTimeOffset(2026, 3, 10, 15, 15, 0, TimeSpan.Zero), record.SentUtc);
        Assert.Equal("Outgoing", record.Direction);
        Assert.Equal(2, record.AttachmentCount);
        Assert.True(record.HasAttachments);
        Assert.True(record.DeletedFlag);
        Assert.Equal(sourceEvidenceItemId, record.SourceEvidenceItemId);
        Assert.Equal("csv:messages.csv#row=2", record.SourceLocator);
        Assert.Equal("CSV", record.ParserFamily);
        Assert.Equal("CaseGraph.MessagesIngest/v2", record.ParserVersion);
        Assert.Empty(record.ParseWarnings);
    }

    [Fact]
    public void TryCreate_PartialFields_ReturnsWarnings_ButStillSucceeds()
    {
        var parseResult = CanonicalMessageRecord.TryCreate(
            new Dictionary<CanonicalMessageField, string?>
            {
                [CanonicalMessageField.SenderDisplay] = "Synthetic Alpha",
                [CanonicalMessageField.RecipientDisplays] = "Synthetic Bravo",
                [CanonicalMessageField.ThreadTitle] = "Loose Chat"
            },
            new CanonicalMessageContext(
                Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb"),
                "xlsx:messages.xlsx#Loose:R2",
                "XLSX",
                "CaseGraph.MessagesIngest/v2",
                PlatformHint: null
            )
        );

        var record = Assert.IsType<CanonicalMessageRecord>(parseResult.Record);
        Assert.True(parseResult.Success);
        Assert.Equal("OTHER", record.Platform);
        Assert.Contains("Timestamp missing or invalid.", record.ParseWarnings);
        Assert.Contains("Platform missing; defaulted to OTHER.", record.ParseWarnings);
        Assert.Contains("Body missing.", record.ParseWarnings);
        Assert.Contains("Thread identifier missing; ingest will derive a fallback thread key.", record.ParseWarnings);
    }
}
