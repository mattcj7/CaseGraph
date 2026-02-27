using System.Text.RegularExpressions;

namespace TicketKit;

public sealed class VerificationResult
{
    public List<string> Infos { get; } = [];

    public List<string> Warnings { get; } = [];

    public List<string> Errors { get; } = [];

    public bool Succeeded => Errors.Count == 0;
}

public sealed partial class TicketVerifier
{
    private static readonly HeadingRule[] ContextHeadingRules =
    [
        new HeadingRule("## Purpose", static heading => heading.Equals("## Purpose", StringComparison.OrdinalIgnoreCase)),
        new HeadingRule(
            "## Canonical links (do not paste content)",
            static heading => heading.StartsWith("## Canonical links", StringComparison.OrdinalIgnoreCase)
        ),
        new HeadingRule(
            "## Files to open (whitelist)",
            static heading =>
                heading.Equals("## Files to open (whitelist)", StringComparison.OrdinalIgnoreCase)
        )
    ];

    private static readonly HeadingRule[] CloseoutHeadingRules =
    [
        new HeadingRule(
            "## What shipped",
            static heading => heading.Equals("## What shipped", StringComparison.OrdinalIgnoreCase)
        ),
        new HeadingRule(
            "## Files changed (high-level)",
            static heading =>
                heading.Equals("## Files changed (high-level)", StringComparison.OrdinalIgnoreCase)
        )
    ];

    private readonly string _repoRoot;
    private readonly string _normalizedRepoRoot;
    private readonly IGitChangedFilesProvider _gitChangedFilesProvider;

    public TicketVerifier(string repoRoot, IGitChangedFilesProvider gitChangedFilesProvider)
    {
        _repoRoot = repoRoot;
        _normalizedRepoRoot = NormalizeRepoRoot(repoRoot);
        _gitChangedFilesProvider = gitChangedFilesProvider;
    }

    public async Task<VerificationResult> VerifyAsync(
        string ticketId,
        bool strict,
        CancellationToken cancellationToken
    )
    {
        var result = new VerificationResult();

        var contextPath = Path.Combine(_repoRoot, "Docs", "Tickets", $"{ticketId}.context.md");
        var closeoutPath = Path.Combine(_repoRoot, "Docs", "Tickets", $"{ticketId}.closeout.md");

        var contextScan = await ScanAndValidateDocumentAsync(
            ticketId,
            contextPath,
            ContextHeadingRules,
            result,
            cancellationToken
        );
        var closeoutScan = await ScanAndValidateDocumentAsync(
            ticketId,
            closeoutPath,
            CloseoutHeadingRules,
            result,
            cancellationToken
        );

        if (contextScan is not null)
        {
            ValidateReferencedPaths(contextScan, result);
        }

        if (closeoutScan is not null)
        {
            ValidateReferencedPaths(closeoutScan, result);
        }

        await ApplyBudgetChecksAsync(result, strict, cancellationToken);

        return result;
    }

    private async Task<DocumentScanResult?> ScanAndValidateDocumentAsync(
        string ticketId,
        string filePath,
        IReadOnlyList<HeadingRule> headingRules,
        VerificationResult result,
        CancellationToken cancellationToken
    )
    {
        if (!File.Exists(filePath))
        {
            result.Errors.Add(
                $"Missing required file: {ToRelative(filePath)}. "
                    + $"Run `ticketkit init {ticketId} \"Title\"` to create it."
            );
            return null;
        }

        var scan = await ScanDocumentAsync(filePath, cancellationToken);
        foreach (var headingRule in headingRules)
        {
            var hasHeading = scan.Headings.Any(headingRule.Matches);
            if (!hasHeading)
            {
                result.Errors.Add(
                    $"Missing required heading in {ToRelative(filePath)}: {headingRule.DisplayText}."
                );
            }
        }

        return scan;
    }

    private static async Task<DocumentScanResult> ScanDocumentAsync(
        string path,
        CancellationToken cancellationToken
    )
    {
        var headings = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var referencedDocsPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 4096,
            useAsync: true
        );
        using var reader = new StreamReader(stream);

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var line = await reader.ReadLineAsync(cancellationToken);
            if (line is null)
            {
                break;
            }

            var trimmed = line.Trim();
            if (trimmed.StartsWith("## ", StringComparison.OrdinalIgnoreCase))
            {
                headings.Add(NormalizeHeading(trimmed));
            }

            foreach (var docsPath in ExtractDocsPaths(line))
            {
                referencedDocsPaths.Add(docsPath);
            }
        }

        return new DocumentScanResult(path, headings, referencedDocsPaths);
    }

    private void ValidateReferencedPaths(DocumentScanResult scan, VerificationResult result)
    {
        foreach (var docsPath in scan.ReferencedDocsPaths)
        {
            var fullPath = Path.GetFullPath(
                Path.Combine(_repoRoot, docsPath.Replace('/', Path.DirectorySeparatorChar))
            );

            if (!fullPath.StartsWith(_normalizedRepoRoot, StringComparison.OrdinalIgnoreCase))
            {
                result.Errors.Add(
                    $"Referenced path escapes repository root in {ToRelative(scan.FilePath)}: {docsPath}."
                );
                continue;
            }

            if (!File.Exists(fullPath) && !Directory.Exists(fullPath))
            {
                result.Errors.Add(
                    $"Referenced path not found in {ToRelative(scan.FilePath)}: {docsPath}. "
                        + "Fix the link or create the file."
                );
            }
        }
    }

    private async Task ApplyBudgetChecksAsync(
        VerificationResult result,
        bool strict,
        CancellationToken cancellationToken
    )
    {
        var gitResult = await _gitChangedFilesProvider.TryGetChangedFilesAsync(
            _repoRoot,
            cancellationToken
        );
        if (gitResult.Status == GitChangedFilesStatus.NotAvailable)
        {
            result.Infos.Add(gitResult.Message);
            return;
        }

        if (gitResult.Status == GitChangedFilesStatus.Failed)
        {
            result.Infos.Add(gitResult.Message);
            return;
        }

        var topLevelFolders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var migrationsTouched = 0;
        foreach (var changedFile in gitResult.Files)
        {
            if (string.IsNullOrWhiteSpace(changedFile))
            {
                continue;
            }

            var normalizedPath = changedFile.Replace('\\', '/').Trim();
            var pathParts = normalizedPath.Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (pathParts.Length > 0)
            {
                topLevelFolders.Add(pathParts[0]);
            }

            if (normalizedPath.Contains("/Migrations/", StringComparison.OrdinalIgnoreCase))
            {
                migrationsTouched++;
            }
        }

        AddBudgetFinding(
            result,
            strict,
            topLevelFolders.Count > 3,
            $"Budget warning: touched top-level folders = {topLevelFolders.Count} (> 3)."
        );
        AddBudgetFinding(
            result,
            strict,
            migrationsTouched > 1,
            $"Budget warning: migrations touched = {migrationsTouched} (> 1)."
        );
    }

    private static void AddBudgetFinding(
        VerificationResult result,
        bool strict,
        bool condition,
        string message
    )
    {
        if (!condition)
        {
            return;
        }

        if (strict)
        {
            result.Errors.Add($"Strict mode: {message}");
            return;
        }

        result.Warnings.Add(message);
    }

    private string ToRelative(string path)
    {
        return Path.GetRelativePath(_repoRoot, path).Replace('\\', '/');
    }

    private static string NormalizeHeading(string heading)
    {
        return WhitespaceRegex().Replace(heading.Trim(), " ");
    }

    private static IEnumerable<string> ExtractDocsPaths(string line)
    {
        foreach (Match markdownLink in MarkdownLinkRegex().Matches(line))
        {
            var path = NormalizeDocsPath(markdownLink.Groups["path"].Value);
            if (path is not null)
            {
                yield return path;
            }
        }

        foreach (Match barePath in BareDocsPathRegex().Matches(line))
        {
            var path = NormalizeDocsPath(barePath.Groups["path"].Value);
            if (path is not null)
            {
                yield return path;
            }
        }
    }

    private static string? NormalizeDocsPath(string rawPath)
    {
        if (string.IsNullOrWhiteSpace(rawPath))
        {
            return null;
        }

        var candidate = rawPath.Trim().Trim('<', '>', '"', '\'', '`');
        if (candidate.StartsWith("#", StringComparison.Ordinal))
        {
            return null;
        }

        if (
            candidate.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            || candidate.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
            || candidate.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase)
        )
        {
            return null;
        }

        var firstSpace = candidate.IndexOf(' ');
        if (firstSpace >= 0)
        {
            candidate = candidate[..firstSpace];
        }

        var hashIndex = candidate.IndexOf('#');
        if (hashIndex >= 0)
        {
            candidate = candidate[..hashIndex];
        }

        var queryIndex = candidate.IndexOf('?');
        if (queryIndex >= 0)
        {
            candidate = candidate[..queryIndex];
        }

        candidate = candidate.Replace('\\', '/');
        if (candidate.StartsWith("./", StringComparison.Ordinal))
        {
            candidate = candidate[2..];
        }

        candidate = candidate.TrimStart('/');

        var docsIndex = candidate.IndexOf("Docs/", StringComparison.OrdinalIgnoreCase);
        if (docsIndex < 0)
        {
            return null;
        }

        var relativePath = candidate[docsIndex..];
        if (!relativePath.StartsWith("Docs/", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return relativePath.TrimEnd('.', ',', ';', ')', ']');
    }

    private static string NormalizeRepoRoot(string repoRoot)
    {
        var normalized = Path.GetFullPath(repoRoot).TrimEnd(
            Path.DirectorySeparatorChar,
            Path.AltDirectorySeparatorChar
        );
        return normalized + Path.DirectorySeparatorChar;
    }

    private sealed record HeadingRule(string DisplayText, Func<string, bool> Matches);

    private sealed record DocumentScanResult(
        string FilePath,
        IReadOnlyCollection<string> Headings,
        IReadOnlyCollection<string> ReferencedDocsPaths
    );

    [GeneratedRegex(@"\[[^\]]+\]\((?<path>[^)]+)\)", RegexOptions.CultureInvariant)]
    private static partial Regex MarkdownLinkRegex();

    [GeneratedRegex(@"(?<path>Docs[\\/][A-Za-z0-9._\-/\\]+)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex BareDocsPathRegex();

    [GeneratedRegex(@"\s+", RegexOptions.CultureInvariant)]
    private static partial Regex WhitespaceRegex();
}
