using TicketKit;

namespace TicketKit.Tests;

public sealed class InitCommandTests
{
    [Fact]
    public async Task Init_DefaultDoesNotOverwrite_ForceOverwrites()
    {
        using var fixture = new TestRepositoryFixture();
        SeedTemplates(fixture, "template-v1");

        var output = new StringWriter();
        var error = new StringWriter();
        var runner = CreateRunner(fixture.RootPath, output, error);

        var firstExitCode = await runner.RunAsync(
            ["init", "T0023", "Ticket Context Packs"],
            CancellationToken.None
        );
        Assert.Equal(0, firstExitCode);

        var contextPath = fixture.GetPath("Docs/Tickets/T0023.context.md");
        var originalContext = await File.ReadAllTextAsync(contextPath);
        Assert.Contains("template-v1", originalContext);
        Assert.Contains("T0023", originalContext);
        Assert.Contains("Ticket Context Packs", originalContext);

        SeedTemplates(fixture, "template-v2");

        var secondExitCode = await runner.RunAsync(
            ["init", "T0023", "Changed Title"],
            CancellationToken.None
        );
        Assert.Equal(0, secondExitCode);
        var unchangedContext = await File.ReadAllTextAsync(contextPath);
        Assert.Equal(originalContext, unchangedContext);

        var thirdExitCode = await runner.RunAsync(
            ["init", "T0023", "Changed Title", "--force"],
            CancellationToken.None
        );
        Assert.Equal(0, thirdExitCode);
        var overwrittenContext = await File.ReadAllTextAsync(contextPath);
        Assert.Contains("template-v2", overwrittenContext);
        Assert.Contains("Changed Title", overwrittenContext);
        Assert.DoesNotContain("template-v1", overwrittenContext);
    }

    [Fact]
    public async Task Init_WithAdrFlag_CreatesAdrFromTemplate()
    {
        using var fixture = new TestRepositoryFixture();
        SeedTemplates(fixture, "template-adr");

        var output = new StringWriter();
        var error = new StringWriter();
        var runner = CreateRunner(fixture.RootPath, output, error);

        var exitCode = await runner.RunAsync(
            ["init", "T0024", "Ticket One", "--adr", "Cache Decision"],
            CancellationToken.None
        );
        Assert.Equal(0, exitCode);

        var adrPath = fixture.GetPath("Docs/DECISIONS/ADR-T0024-cache-decision.md");
        Assert.True(File.Exists(adrPath));

        var adrContent = await File.ReadAllTextAsync(adrPath);
        Assert.Contains("ADR: Cache Decision", adrContent);
        Assert.Contains("Ticket: T0024", adrContent);
    }

    private static TicketKitRunner CreateRunner(
        string repoRoot,
        TextWriter output,
        TextWriter error
    )
    {
        return new TicketKitRunner(
            repoRoot,
            output,
            error,
            new FakeGitChangedFilesProvider(GitChangedFilesResult.Success(Array.Empty<string>()))
        );
    }

    private static void SeedTemplates(TestRepositoryFixture fixture, string marker)
    {
        fixture.WriteFile(
            "Docs/Tickets/_TEMPLATES/TXXXX.context.md",
            $"""
            # {marker} context TXXXX - <Title>
            ## Purpose
            ## Canonical links (do not paste content)
            - [Invariants](Docs/INVARIANTS.md)
            ## Files to open (whitelist)
            """
        );
        fixture.WriteFile(
            "Docs/Tickets/_TEMPLATES/TXXXX.closeout.md",
            """
            # closeout TXXXX - <Title>
            ## What shipped
            ## Files changed (high-level)
            """
        );
        fixture.WriteFile(
            "Docs/DECISIONS/ADR-TEMPLATE.md",
            """
            # ADR: <Title>
            - Ticket: TXXXX
            - Date: <Date>
            """
        );
        fixture.WriteFile("Docs/INVARIANTS.md", "# Invariants");
    }
}
