using System.IO;

namespace CaseGraph.App.Services;

public sealed class AppRuntimePaths : IAppRuntimePaths
{
    private const string RuntimeRootOverrideEnvironmentVariable = "CASEGRAPH_RUNTIME_ROOT";

    public AppRuntimePaths()
    {
        var overrideRoot = Environment.GetEnvironmentVariable(RuntimeRootOverrideEnvironmentVariable);
        RuntimeRoot = string.IsNullOrWhiteSpace(overrideRoot)
            ? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "CaseGraphOffline"
            )
            : overrideRoot.Trim();

        DumpsDirectory = Path.Combine(RuntimeRoot, "dumps");
        SessionDirectory = Path.Combine(RuntimeRoot, "session");
    }

    public string RuntimeRoot { get; }

    public string DumpsDirectory { get; }

    public string SessionDirectory { get; }
}
