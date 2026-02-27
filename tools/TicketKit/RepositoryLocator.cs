namespace TicketKit;

public static class RepositoryLocator
{
    public static string Resolve(string currentDirectory)
    {
        var overridePath = Environment.GetEnvironmentVariable("TICKETKIT_REPO_ROOT");
        if (!string.IsNullOrWhiteSpace(overridePath))
        {
            return Path.GetFullPath(overridePath);
        }

        var directory = new DirectoryInfo(currentDirectory);
        while (directory is not null)
        {
            if (Directory.Exists(Path.Combine(directory.FullName, "Docs")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        return currentDirectory;
    }
}
