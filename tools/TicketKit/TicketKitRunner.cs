using System.Globalization;
using System.Text;

namespace TicketKit;

public sealed class TicketKitRunner
{
    private const int SuccessExitCode = 0;
    private const int ValidationFailedExitCode = 1;
    private const int UsageExitCode = 2;
    private const int UnexpectedExitCode = 3;

    private readonly string _repoRoot;
    private readonly TextWriter _output;
    private readonly TextWriter _error;
    private readonly IGitChangedFilesProvider _gitChangedFilesProvider;

    public TicketKitRunner(
        string repoRoot,
        TextWriter output,
        TextWriter error,
        IGitChangedFilesProvider gitChangedFilesProvider
    )
    {
        _repoRoot = repoRoot;
        _output = output;
        _error = error;
        _gitChangedFilesProvider = gitChangedFilesProvider;
    }

    public async Task<int> RunAsync(string[] args, CancellationToken cancellationToken)
    {
        if (args.Length == 0)
        {
            WriteHelp();
            return UsageExitCode;
        }

        var command = args[0].Trim();
        if (IsHelpCommand(command))
        {
            WriteHelp();
            return SuccessExitCode;
        }

        try
        {
            if (command.Equals("init", StringComparison.OrdinalIgnoreCase))
            {
                return await RunInitAsync(args.Skip(1).ToArray(), cancellationToken);
            }

            if (command.Equals("verify", StringComparison.OrdinalIgnoreCase))
            {
                return await RunVerifyAsync(args.Skip(1).ToArray(), cancellationToken);
            }

            _error.WriteLine($"Unknown command '{command}'.");
            WriteHelp();
            return UsageExitCode;
        }
        catch (OperationCanceledException)
        {
            _error.WriteLine("Operation canceled.");
            return UnexpectedExitCode;
        }
        catch (Exception ex)
        {
            _error.WriteLine($"Unexpected error: {ex.Message}");
            return UnexpectedExitCode;
        }
    }

    private async Task<int> RunInitAsync(string[] args, CancellationToken cancellationToken)
    {
        if (!TryParseInitOptions(args, out var options, out var errorMessage))
        {
            _error.WriteLine(errorMessage);
            WriteInitUsage();
            return UsageExitCode;
        }

        var contextTemplatePath = Path.Combine(
            _repoRoot,
            "Docs",
            "Tickets",
            "_TEMPLATES",
            "TXXXX.context.md"
        );
        var closeoutTemplatePath = Path.Combine(
            _repoRoot,
            "Docs",
            "Tickets",
            "_TEMPLATES",
            "TXXXX.closeout.md"
        );
        var adrTemplatePath = Path.Combine(_repoRoot, "Docs", "DECISIONS", "ADR-TEMPLATE.md");

        if (
            !EnsureTemplateExists(contextTemplatePath)
            || !EnsureTemplateExists(closeoutTemplatePath)
            || (options.AdrTitle is not null && !EnsureTemplateExists(adrTemplatePath))
        )
        {
            return ValidationFailedExitCode;
        }

        var initFailures = 0;

        var commonReplacements = CreateTokenMap(
            options.TicketId,
            options.Title,
            ticketTitle: options.Title
        );
        initFailures += await CreateFromTemplateAsync(
            contextTemplatePath,
            Path.Combine(_repoRoot, "Docs", "Tickets", $"{options.TicketId}.context.md"),
            options.Force,
            commonReplacements,
            cancellationToken
        );
        initFailures += await CreateFromTemplateAsync(
            closeoutTemplatePath,
            Path.Combine(_repoRoot, "Docs", "Tickets", $"{options.TicketId}.closeout.md"),
            options.Force,
            commonReplacements,
            cancellationToken
        );

        if (options.AdrTitle is not null)
        {
            var adrFileName = BuildAdrFileName(options.TicketId, options.AdrTitle);
            var adrReplacements = CreateTokenMap(
                options.TicketId,
                options.AdrTitle,
                ticketTitle: options.Title
            );
            initFailures += await CreateFromTemplateAsync(
                adrTemplatePath,
                Path.Combine(_repoRoot, "Docs", "DECISIONS", adrFileName),
                options.Force,
                adrReplacements,
                cancellationToken
            );
        }

        if (initFailures > 0)
        {
            _error.WriteLine($"init failed (exit {ValidationFailedExitCode}).");
            return ValidationFailedExitCode;
        }

        _output.WriteLine("init completed.");
        return SuccessExitCode;
    }

    private async Task<int> RunVerifyAsync(string[] args, CancellationToken cancellationToken)
    {
        if (!TryParseVerifyOptions(args, out var options, out var errorMessage))
        {
            _error.WriteLine(errorMessage);
            WriteVerifyUsage();
            return UsageExitCode;
        }

        var verifier = new TicketVerifier(_repoRoot, _gitChangedFilesProvider);
        var verificationResult = await verifier.VerifyAsync(
            options.TicketId,
            options.Strict,
            cancellationToken
        );

        foreach (var info in verificationResult.Infos)
        {
            _output.WriteLine($"[info] {info}");
        }

        foreach (var warning in verificationResult.Warnings)
        {
            _output.WriteLine($"[warning] {warning}");
        }

        foreach (var error in verificationResult.Errors)
        {
            _error.WriteLine($"[error] {error}");
        }

        if (!verificationResult.Succeeded)
        {
            _error.WriteLine(
                $"verify failed (exit {ValidationFailedExitCode}): "
                    + $"{verificationResult.Errors.Count} error(s), "
                    + $"{verificationResult.Warnings.Count} warning(s)."
            );
            return ValidationFailedExitCode;
        }

        _output.WriteLine(
            $"verify passed (exit {SuccessExitCode}): "
                + $"{verificationResult.Warnings.Count} warning(s)."
        );
        return SuccessExitCode;
    }

    private async Task<int> CreateFromTemplateAsync(
        string templatePath,
        string destinationPath,
        bool overwrite,
        IReadOnlyDictionary<string, string> replacements,
        CancellationToken cancellationToken
    )
    {
        try
        {
            var outcome = await SafeFileWriter.WriteTemplateAsync(
                templatePath,
                destinationPath,
                replacements,
                overwrite,
                cancellationToken
            );

            var relativePath = ToRelativePath(destinationPath);
            switch (outcome)
            {
                case SafeWriteOutcome.Created:
                    _output.WriteLine($"[created] {relativePath}");
                    return 0;
                case SafeWriteOutcome.Overwritten:
                    _output.WriteLine($"[overwritten] {relativePath}");
                    return 0;
                case SafeWriteOutcome.Skipped:
                    _output.WriteLine(
                        $"[skipped] {relativePath} already exists. Re-run with --force to overwrite."
                    );
                    return 0;
                default:
                    _error.WriteLine($"[error] Unknown write outcome for {relativePath}.");
                    return 1;
            }
        }
        catch (Exception ex)
        {
            _error.WriteLine(
                $"[error] Failed to write {ToRelativePath(destinationPath)}: {ex.Message}"
            );
            return 1;
        }
    }

    private bool EnsureTemplateExists(string templatePath)
    {
        if (File.Exists(templatePath))
        {
            return true;
        }

        _error.WriteLine(
            $"Missing template: {ToRelativePath(templatePath)}. "
                + "Restore the template file and retry."
        );
        return false;
    }

    private static Dictionary<string, string> CreateTokenMap(
        string ticketId,
        string title,
        string ticketTitle
    )
    {
        return new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["TXXXX"] = ticketId,
            ["<Title>"] = title,
            ["<Date>"] = DateTimeOffset.UtcNow.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            ["<TicketTitle>"] = ticketTitle
        };
    }

    private string ToRelativePath(string fullPath)
    {
        return Path.GetRelativePath(_repoRoot, fullPath).Replace('\\', '/');
    }

    private static bool TryParseInitOptions(
        IReadOnlyList<string> args,
        out InitOptions options,
        out string errorMessage
    )
    {
        options = default;
        errorMessage = string.Empty;

        if (args.Count < 2)
        {
            errorMessage = "init requires: TXXXX \"Title\"";
            return false;
        }

        var ticketId = args[0].Trim();
        var title = args[1].Trim();
        if (!TicketIdValidator.IsValid(ticketId))
        {
            errorMessage = $"Invalid ticket id '{ticketId}'. Expected format: TXXXX.";
            return false;
        }

        if (string.IsNullOrWhiteSpace(title))
        {
            errorMessage = "Title cannot be empty.";
            return false;
        }

        var force = false;
        string? adrTitle = null;
        for (var index = 2; index < args.Count; index++)
        {
            var arg = args[index];
            if (arg.Equals("--force", StringComparison.OrdinalIgnoreCase))
            {
                force = true;
                continue;
            }

            if (arg.Equals("--adr", StringComparison.OrdinalIgnoreCase))
            {
                if (index + 1 >= args.Count)
                {
                    errorMessage = "--adr requires a value.";
                    return false;
                }

                index++;
                adrTitle = args[index].Trim();
                if (string.IsNullOrWhiteSpace(adrTitle))
                {
                    errorMessage = "--adr value cannot be empty.";
                    return false;
                }

                continue;
            }

            errorMessage = $"Unknown option for init: {arg}";
            return false;
        }

        options = new InitOptions(ticketId, title, force, adrTitle);
        return true;
    }

    private static bool TryParseVerifyOptions(
        IReadOnlyList<string> args,
        out VerifyOptions options,
        out string errorMessage
    )
    {
        options = default;
        errorMessage = string.Empty;

        if (args.Count < 1)
        {
            errorMessage = "verify requires: TXXXX";
            return false;
        }

        var ticketId = args[0].Trim();
        if (!TicketIdValidator.IsValid(ticketId))
        {
            errorMessage = $"Invalid ticket id '{ticketId}'. Expected format: TXXXX.";
            return false;
        }

        var strict = false;
        for (var index = 1; index < args.Count; index++)
        {
            var arg = args[index];
            if (arg.Equals("--strict", StringComparison.OrdinalIgnoreCase))
            {
                strict = true;
                continue;
            }

            errorMessage = $"Unknown option for verify: {arg}";
            return false;
        }

        options = new VerifyOptions(ticketId, strict);
        return true;
    }

    private static string BuildAdrFileName(string ticketId, string adrTitle)
    {
        var slugBuilder = new StringBuilder();
        var previousWasSeparator = false;
        foreach (var character in adrTitle.Trim().ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(character))
            {
                slugBuilder.Append(character);
                previousWasSeparator = false;
                continue;
            }

            if (!previousWasSeparator)
            {
                slugBuilder.Append('-');
                previousWasSeparator = true;
            }
        }

        var slug = slugBuilder.ToString().Trim('-');
        if (string.IsNullOrWhiteSpace(slug))
        {
            slug = "decision";
        }

        return $"ADR-{ticketId}-{slug}.md";
    }

    private void WriteHelp()
    {
        _output.WriteLine("TicketKit commands:");
        _output.WriteLine("  ticketkit help");
        _output.WriteLine("  ticketkit init TXXXX \"Title\" [--force] [--adr \"ADR Title\"]");
        _output.WriteLine("  ticketkit verify TXXXX [--strict]");
        _output.WriteLine();
        _output.WriteLine("Examples:");
        _output.WriteLine("  dotnet run --project Tools/TicketKit -- init T0023 \"Ticket Context Packs\"");
        _output.WriteLine("  dotnet run --project Tools/TicketKit -- verify T0023");
        _output.WriteLine("  dotnet run --project Tools/TicketKit -- verify T0023 --strict");
    }

    private void WriteInitUsage()
    {
        _output.WriteLine("Usage: ticketkit init TXXXX \"Title\" [--force] [--adr \"ADR Title\"]");
    }

    private void WriteVerifyUsage()
    {
        _output.WriteLine("Usage: ticketkit verify TXXXX [--strict]");
    }

    private static bool IsHelpCommand(string command)
    {
        return command.Equals("help", StringComparison.OrdinalIgnoreCase)
            || command.Equals("--help", StringComparison.OrdinalIgnoreCase)
            || command.Equals("-h", StringComparison.OrdinalIgnoreCase);
    }

    private readonly record struct InitOptions(
        string TicketId,
        string Title,
        bool Force,
        string? AdrTitle
    );

    private readonly record struct VerifyOptions(string TicketId, bool Strict);
}
