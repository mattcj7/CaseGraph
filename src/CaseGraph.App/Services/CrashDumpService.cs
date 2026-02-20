using CaseGraph.Core.Diagnostics;
using System.IO;

namespace CaseGraph.App.Services;

public sealed class CrashDumpService : ICrashDumpService
{
    private const string ProcessExecutableName = "CaseGraph.App.exe";
    private readonly WerLocalDumpSettingsManager _settingsManager;

    public CrashDumpService(IRegistryValueStore registryValueStore, IAppRuntimePaths runtimePaths)
    {
        var registryDumpFolder = ResolveRegistryDumpFolder(runtimePaths.DumpsDirectory);
        _settingsManager = new WerLocalDumpSettingsManager(
            registryValueStore,
            ProcessExecutableName,
            runtimePaths.DumpsDirectory,
            registryDumpFolder
        );
    }

    public WerLocalDumpSettings GetSettings()
    {
        return _settingsManager.GetSettings();
    }

    public void SetEnabled(bool enabled)
    {
        if (enabled)
        {
            _settingsManager.Enable(dumpType: 1, dumpCount: 10);
            return;
        }

        _settingsManager.Disable();
    }

    private static string ResolveRegistryDumpFolder(string absoluteDumpPath)
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (!string.IsNullOrWhiteSpace(localAppData)
            && absoluteDumpPath.StartsWith(localAppData, StringComparison.OrdinalIgnoreCase))
        {
            var relative = absoluteDumpPath.Substring(localAppData.Length)
                .TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            return string.IsNullOrWhiteSpace(relative)
                ? "%LOCALAPPDATA%"
                : $@"%LOCALAPPDATA%\{relative}";
        }

        return absoluteDumpPath;
    }
}
