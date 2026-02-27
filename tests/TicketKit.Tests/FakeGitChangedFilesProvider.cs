using TicketKit;

namespace TicketKit.Tests;

internal sealed class FakeGitChangedFilesProvider : IGitChangedFilesProvider
{
    private readonly GitChangedFilesResult _result;

    public FakeGitChangedFilesProvider(GitChangedFilesResult result)
    {
        _result = result;
    }

    public Task<GitChangedFilesResult> TryGetChangedFilesAsync(
        string repoRoot,
        CancellationToken cancellationToken
    )
    {
        return Task.FromResult(_result);
    }
}
