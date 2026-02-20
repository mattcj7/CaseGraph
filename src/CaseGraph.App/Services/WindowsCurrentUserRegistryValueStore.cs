using CaseGraph.Core.Diagnostics;
using Microsoft.Win32;

namespace CaseGraph.App.Services;

public sealed class WindowsCurrentUserRegistryValueStore : IRegistryValueStore
{
    public void SetString(string subKeyPath, string valueName, string value)
    {
        using var key = Registry.CurrentUser.CreateSubKey(subKeyPath, writable: true);
        key?.SetValue(valueName, value, RegistryValueKind.String);
    }

    public void SetExpandString(string subKeyPath, string valueName, string value)
    {
        using var key = Registry.CurrentUser.CreateSubKey(subKeyPath, writable: true);
        key?.SetValue(valueName, value, RegistryValueKind.ExpandString);
    }

    public void SetDword(string subKeyPath, string valueName, int value)
    {
        using var key = Registry.CurrentUser.CreateSubKey(subKeyPath, writable: true);
        key?.SetValue(valueName, value, RegistryValueKind.DWord);
    }

    public string? GetString(string subKeyPath, string valueName)
    {
        using var key = Registry.CurrentUser.OpenSubKey(subKeyPath, writable: false);
        return key?.GetValue(valueName) as string;
    }

    public int? GetDword(string subKeyPath, string valueName)
    {
        using var key = Registry.CurrentUser.OpenSubKey(subKeyPath, writable: false);
        var value = key?.GetValue(valueName);
        return value switch
        {
            int dword => dword,
            null => null,
            _ => null
        };
    }

    public bool KeyExists(string subKeyPath)
    {
        using var key = Registry.CurrentUser.OpenSubKey(subKeyPath, writable: false);
        return key is not null;
    }

    public void DeleteKeyTree(string subKeyPath)
    {
        Registry.CurrentUser.DeleteSubKeyTree(subKeyPath, throwOnMissingSubKey: false);
    }
}
