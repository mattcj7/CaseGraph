using System.ComponentModel;
using System.Diagnostics;

namespace TicketKit;

public enum GitChangedFilesStatus
{
    Success,
    NotAvailable,
    Failed
}

public sealed record GitChangedFilesResult(
    GitChangedFilesStatus Status,
    IReadOnlyList<string> Files,
    string Message
)
{
    public static GitChangedFilesResult Success(IReadOnlyList<string> files)
    {
        return new GitChangedFilesResult(GitChangedFilesStatus.Success, files, string.Empty);
    }

    public static GitChangedFilesResult NotAvailable(
        string message = "Budget checks skipped (git not available)."
    )
    {
        return new GitChangedFilesResult(GitChangedFilesStatus.NotAvailable, Array.Empty<string>(), message);
    }

    public static GitChangedFilesResult Failed(string message)
    {
        return new GitChangedFilesResult(GitChangedFilesStatus.Failed, Array.Empty<string>(), message);
    }
}

public interface IGitChangedFilesProvider
{
    Task<GitChangedFilesResult> TryGetChangedFilesAsync(
        string repoRoot,
        CancellationToken cancellationToken
    );
}

public sealed class GitChangedFilesProvider : IGitChangedFilesProvider
{
    public async Task<GitChangedFilesResult> TryGetChangedFilesAsync(
        string repoRoot,
        CancellationToken cancellationToken
    )
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "git",
            WorkingDirectory = repoRoot,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        startInfo.ArgumentList.Add("diff");
        startInfo.ArgumentList.Add("--name-only");
        startInfo.ArgumentList.Add("HEAD");

        try
        {
            using var process = new Process { StartInfo = startInfo };
            process.Start();

            var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);

            await process.WaitForExitAsync(cancellationToken);
            var stdout = await stdoutTask;
            var stderr = await stderrTask;

            if (process.ExitCode != 0)
            {
                var trimmedStderr = string.IsNullOrWhiteSpace(stderr) ? "no error text" : stderr.Trim();
                return GitChangedFilesResult.Failed(
                    $"Budget checks skipped (git diff failed: {trimmedStderr})."
                );
            }

            var changedFiles = stdout
                .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
                .Select(line => line.Trim().Replace('\\', '/'))
                .Where(line => !string.IsNullOrWhiteSpace(line))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();

            return GitChangedFilesResult.Success(changedFiles);
        }
        catch (Win32Exception)
        {
            return GitChangedFilesResult.NotAvailable();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return GitChangedFilesResult.Failed(
                $"Budget checks skipped (unable to run git diff: {ex.Message})."
            );
        }
    }
}
