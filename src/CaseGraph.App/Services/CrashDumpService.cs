using CaseGraph.Core.Diagnostics;

namespace CaseGraph.App.Services;

public sealed class CrashDumpService : ICrashDumpService
{
    private const string ProcessExecutableName = "CaseGraph.App.exe";
    private readonly WerLocalDumpSettingsManager _settingsManager;

    public CrashDumpService(IRegistryValueStore registryValueStore, IAppRuntimePaths runtimePaths)
    {
        _settingsManager = new WerLocalDumpSettingsManager(
            registryValueStore,
            ProcessExecutableName,
            runtimePaths.DumpsDirectory
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
            _settingsManager.Enable(dumpType: 2, dumpCount: 10);
            return;
        }

        _settingsManager.Disable();
    }
}
