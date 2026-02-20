namespace CaseGraph.Core.Diagnostics;

public interface IRegistryValueStore
{
    void SetString(string subKeyPath, string valueName, string value);

    void SetDword(string subKeyPath, string valueName, int value);

    string? GetString(string subKeyPath, string valueName);

    int? GetDword(string subKeyPath, string valueName);

    bool KeyExists(string subKeyPath);

    void DeleteKeyTree(string subKeyPath);
}

public sealed class WerLocalDumpSettingsManager
{
    private const string LocalDumpsBasePath = @"Software\Microsoft\Windows\Windows Error Reporting\LocalDumps";
    private readonly IRegistryValueStore _registryValueStore;
    private readonly string _processExecutableName;
    private readonly string _dumpDirectory;

    public WerLocalDumpSettingsManager(
        IRegistryValueStore registryValueStore,
        string processExecutableName,
        string dumpDirectory
    )
    {
        _registryValueStore = registryValueStore ?? throw new ArgumentNullException(nameof(registryValueStore));
        ArgumentException.ThrowIfNullOrWhiteSpace(processExecutableName);
        ArgumentException.ThrowIfNullOrWhiteSpace(dumpDirectory);

        _processExecutableName = processExecutableName.Trim();
        _dumpDirectory = dumpDirectory.Trim();
    }

    public string RegistrySubKeyPath => $"{LocalDumpsBasePath}\\{_processExecutableName}";

    public WerLocalDumpSettings GetSettings()
    {
        var keyExists = _registryValueStore.KeyExists(RegistrySubKeyPath);
        var dumpFolder = _registryValueStore.GetString(RegistrySubKeyPath, "DumpFolder") ?? _dumpDirectory;
        var dumpType = _registryValueStore.GetDword(RegistrySubKeyPath, "DumpType") ?? 2;
        var dumpCount = _registryValueStore.GetDword(RegistrySubKeyPath, "DumpCount") ?? 10;

        return new WerLocalDumpSettings(
            Enabled: keyExists,
            DumpFolder: dumpFolder,
            DumpType: dumpType,
            DumpCount: dumpCount,
            RegistrySubKeyPath: RegistrySubKeyPath
        );
    }

    public void Enable(int dumpType = 2, int dumpCount = 10)
    {
        Directory.CreateDirectory(_dumpDirectory);
        _registryValueStore.SetString(RegistrySubKeyPath, "DumpFolder", _dumpDirectory);
        _registryValueStore.SetDword(RegistrySubKeyPath, "DumpType", dumpType);
        _registryValueStore.SetDword(RegistrySubKeyPath, "DumpCount", dumpCount);
    }

    public void Disable()
    {
        _registryValueStore.DeleteKeyTree(RegistrySubKeyPath);
    }
}

public sealed record WerLocalDumpSettings(
    bool Enabled,
    string DumpFolder,
    int DumpType,
    int DumpCount,
    string RegistrySubKeyPath
);
