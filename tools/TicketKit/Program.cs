namespace TicketKit;

public static class Program
{
    public static Task<int> Main(string[] args)
    {
        var repoRoot = RepositoryLocator.Resolve(Directory.GetCurrentDirectory());
        var runner = new TicketKitRunner(
            repoRoot,
            Console.Out,
            Console.Error,
            new GitChangedFilesProvider()
        );
        return runner.RunAsync(args, CancellationToken.None);
    }
}
