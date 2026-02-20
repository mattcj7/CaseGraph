namespace CaseGraph.Core.Diagnostics;

public interface IRegistryValueStore
{
    void SetString(string subKeyPath, string valueName, string value);

    void SetExpandString(string subKeyPath, string valueName, string value);

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
    private readonly string _dumpDirectoryPath;
    private readonly string _dumpFolderRegistryValue;

    public WerLocalDumpSettingsManager(
        IRegistryValueStore registryValueStore,
        string processExecutableName,
        string dumpDirectoryPath,
        string? dumpFolderRegistryValue = null
    )
    {
        _registryValueStore = registryValueStore ?? throw new ArgumentNullException(nameof(registryValueStore));
        ArgumentException.ThrowIfNullOrWhiteSpace(processExecutableName);
        ArgumentException.ThrowIfNullOrWhiteSpace(dumpDirectoryPath);

        _processExecutableName = processExecutableName.Trim();
        _dumpDirectoryPath = dumpDirectoryPath.Trim();
        _dumpFolderRegistryValue = string.IsNullOrWhiteSpace(dumpFolderRegistryValue)
            ? _dumpDirectoryPath
            : dumpFolderRegistryValue.Trim();
    }

    public string RegistrySubKeyPath => $"{LocalDumpsBasePath}\\{_processExecutableName}";

    public WerLocalDumpSettings GetSettings()
    {
        var keyExists = _registryValueStore.KeyExists(RegistrySubKeyPath);
        var dumpFolderRaw = _registryValueStore.GetString(RegistrySubKeyPath, "DumpFolder")
            ?? _dumpFolderRegistryValue;
        var dumpFolder = Environment.ExpandEnvironmentVariables(dumpFolderRaw);
        var dumpType = _registryValueStore.GetDword(RegistrySubKeyPath, "DumpType") ?? 1;
        var dumpCount = _registryValueStore.GetDword(RegistrySubKeyPath, "DumpCount") ?? 10;

        return new WerLocalDumpSettings(
            Enabled: keyExists,
            DumpFolder: dumpFolder,
            DumpType: dumpType,
            DumpCount: dumpCount,
            RegistrySubKeyPath: RegistrySubKeyPath
        );
    }

    public void Enable(int dumpType = 1, int dumpCount = 10)
    {
        Directory.CreateDirectory(_dumpDirectoryPath);
        _registryValueStore.SetExpandString(RegistrySubKeyPath, "DumpFolder", _dumpFolderRegistryValue);
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
