using TicketKit;

namespace TicketKit.Tests;

public sealed class TemplateRendererTests
{
    [Fact]
    public void RenderTokens_ReplacesTicketAndTitleTokens()
    {
        var replacements = new Dictionary<string, string>
        {
            ["TXXXX"] = "T0023",
            ["<Title>"] = "Ticket Context Packs"
        };

        var rendered = TemplateRenderer.RenderTokens("Ticket TXXXX - <Title>", replacements);

        Assert.Equal("Ticket T0023 - Ticket Context Packs", rendered);
    }
}
