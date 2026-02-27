using TicketKit;

namespace TicketKit.Tests;

public sealed class VerifyCommandTests
{
    [Fact]
    public async Task Verify_MissingFiles_ReturnsNonZeroAndActionableMessage()
    {
        using var fixture = new TestRepositoryFixture();
        fixture.WriteFile(
            "Docs/Tickets/T0023.closeout.md",
            """
            ## What shipped
            ## Files changed (high-level)
            """
        );

        var output = new StringWriter();
        var error = new StringWriter();
        var runner = new TicketKitRunner(
            fixture.RootPath,
            output,
            error,
            new FakeGitChangedFilesProvider(GitChangedFilesResult.Success(Array.Empty<string>()))
        );

        var exitCode = await runner.RunAsync(["verify", "T0023"], CancellationToken.None);

        Assert.Equal(1, exitCode);
        Assert.Contains("Missing required file: Docs/Tickets/T0023.context.md", error.ToString());
        Assert.Contains("ticketkit init T0023 \"Title\"", error.ToString());
    }

    [Fact]
    public async Task Verify_MissingRequiredHeading_ReturnsNonZero()
    {
        using var fixture = new TestRepositoryFixture();
        SeedValidBaseFiles(fixture);
        fixture.WriteFile(
            "Docs/Tickets/T0023.context.md",
            """
            ## Purpose
            ## Files to open (whitelist)
            """
        );
        fixture.WriteFile(
            "Docs/Tickets/T0023.closeout.md",
            """
            ## What shipped
            ## Files changed (high-level)
            """
        );

        var output = new StringWriter();
        var error = new StringWriter();
        var runner = new TicketKitRunner(
            fixture.RootPath,
            output,
            error,
            new FakeGitChangedFilesProvider(GitChangedFilesResult.Success(Array.Empty<string>()))
        );

        var exitCode = await runner.RunAsync(["verify", "T0023"], CancellationToken.None);

        Assert.Equal(1, exitCode);
        Assert.Contains(
            "Missing required heading in Docs/Tickets/T0023.context.md: ## Canonical links (do not paste content).",
            error.ToString()
        );
    }

    [Fact]
    public async Task Verify_ValidFiles_ReturnsZero()
    {
        using var fixture = new TestRepositoryFixture();
        SeedValidBaseFiles(fixture);
        SeedValidTicketFiles(fixture);

        var output = new StringWriter();
        var error = new StringWriter();
        var runner = new TicketKitRunner(
            fixture.RootPath,
            output,
            error,
            new FakeGitChangedFilesProvider(GitChangedFilesResult.Success(Array.Empty<string>()))
        );

        var exitCode = await runner.RunAsync(["verify", "T0023"], CancellationToken.None);

        Assert.Equal(0, exitCode);
        Assert.Contains("verify passed (exit 0)", output.ToString());
        Assert.True(string.IsNullOrWhiteSpace(error.ToString()));
    }

    [Fact]
    public async Task Verify_BudgetWarnings_AreWarningsByDefault_AndFailInStrict()
    {
        using var fixture = new TestRepositoryFixture();
        SeedValidBaseFiles(fixture);
        SeedValidTicketFiles(fixture);

        var changedFiles = new[]
        {
            "src/CaseGraph.Core/Models/A.cs",
            "src/CaseGraph.Infrastructure/Persistence/Migrations/001_First.cs",
            "tests/CaseGraph.Core.Tests/Sample.cs",
            "Docs/TICKETS.md",
            "Tools/TicketKit/Program.cs",
            "src/CaseGraph.Infrastructure/Persistence/Migrations/002_Second.cs"
        };

        var output = new StringWriter();
        var error = new StringWriter();
        var runner = new TicketKitRunner(
            fixture.RootPath,
            output,
            error,
            new FakeGitChangedFilesProvider(GitChangedFilesResult.Success(changedFiles))
        );

        var nonStrictExitCode = await runner.RunAsync(["verify", "T0023"], CancellationToken.None);
        Assert.Equal(0, nonStrictExitCode);
        Assert.Contains("Budget warning: touched top-level folders", output.ToString());
        Assert.Contains("Budget warning: migrations touched", output.ToString());

        output.GetStringBuilder().Clear();
        error.GetStringBuilder().Clear();

        var strictExitCode = await runner.RunAsync(
            ["verify", "T0023", "--strict"],
            CancellationToken.None
        );
        Assert.Equal(1, strictExitCode);
        Assert.Contains("Strict mode: Budget warning: touched top-level folders", error.ToString());
        Assert.Contains("Strict mode: Budget warning: migrations touched", error.ToString());
    }

    [Fact]
    public async Task Verify_WhenGitUnavailable_SkipsBudgetChecksWithMessage()
    {
        using var fixture = new TestRepositoryFixture();
        SeedValidBaseFiles(fixture);
        SeedValidTicketFiles(fixture);

        var output = new StringWriter();
        var error = new StringWriter();
        var runner = new TicketKitRunner(
            fixture.RootPath,
            output,
            error,
            new FakeGitChangedFilesProvider(GitChangedFilesResult.NotAvailable())
        );

        var exitCode = await runner.RunAsync(["verify", "T0023"], CancellationToken.None);

        Assert.Equal(0, exitCode);
        Assert.Contains("Budget checks skipped (git not available).", output.ToString());
        Assert.True(string.IsNullOrWhiteSpace(error.ToString()));
    }

    private static void SeedValidBaseFiles(TestRepositoryFixture fixture)
    {
        fixture.WriteFile("Docs/INVARIANTS.md", "# Invariants");
        fixture.WriteFile("Docs/TICKETS.md", "# Tickets");
    }

    private static void SeedValidTicketFiles(TestRepositoryFixture fixture)
    {
        fixture.WriteFile(
            "Docs/Tickets/T0023.context.md",
            """
            ## Purpose
            ## Canonical links (do not paste content)
            - [Invariants](Docs/INVARIANTS.md)
            - Docs/TICKETS.md
            ## Files to open (whitelist)
            """
        );
        fixture.WriteFile(
            "Docs/Tickets/T0023.closeout.md",
            """
            ## What shipped
            ## Files changed (high-level)
            """
        );
    }
}
