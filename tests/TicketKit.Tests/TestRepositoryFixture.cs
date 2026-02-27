namespace TicketKit.Tests;

internal sealed class TestRepositoryFixture : IDisposable
{
    public TestRepositoryFixture()
    {
        RootPath = Path.Combine(
            Path.GetTempPath(),
            "TicketKit.Tests",
            Guid.NewGuid().ToString("N")
        );
        Directory.CreateDirectory(RootPath);
    }

    public string RootPath { get; }

    public string GetPath(string relativePath)
    {
        return Path.Combine(RootPath, relativePath.Replace('/', Path.DirectorySeparatorChar));
    }

    public void WriteFile(string relativePath, string content)
    {
        var fullPath = GetPath(relativePath);
        var directoryPath = Path.GetDirectoryName(fullPath);
        if (string.IsNullOrWhiteSpace(directoryPath))
        {
            throw new InvalidOperationException($"Unable to resolve directory for '{relativePath}'.");
        }

        Directory.CreateDirectory(directoryPath);
        File.WriteAllText(fullPath, content.Replace("\r\n", "\n"));
    }

    public void Dispose()
    {
        if (!Directory.Exists(RootPath))
        {
            return;
        }

        for (var attempt = 1; attempt <= 5; attempt++)
        {
            try
            {
                Directory.Delete(RootPath, recursive: true);
                return;
            }
            catch (IOException) when (attempt < 5)
            {
                Thread.Sleep(50);
            }
            catch (UnauthorizedAccessException) when (attempt < 5)
            {
                Thread.Sleep(50);
            }
        }
    }
}
