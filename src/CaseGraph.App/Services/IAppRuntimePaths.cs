namespace CaseGraph.App.Services;

public interface IAppRuntimePaths
{
    string RuntimeRoot { get; }

    string DumpsDirectory { get; }

    string SessionDirectory { get; }
}
