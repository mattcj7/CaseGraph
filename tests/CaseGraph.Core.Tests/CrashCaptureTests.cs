using CaseGraph.Core.Diagnostics;

namespace CaseGraph.Core.Tests;

public sealed class CrashCaptureTests
{
    [Fact]
    public void SessionJournal_BoundsEntries_And_DetectsUnexpectedPreviousSession()
    {
        var root = CreateTempDirectory();
        var sessionDirectory = Path.Combine(root, "session");

        try
        {
            var first = new SessionJournal(sessionDirectory, maxLines: 5);
            _ = first.StartNewSession();
            for (var i = 0; i < 10; i++)
            {
                first.WriteEvent(
                    "UiActionStarted",
                    new Dictionary<string, object?>
                    {
                        ["index"] = i
                    }
                );
            }

            var second = new SessionJournal(sessionDirectory, maxLines: 5);
            var secondStart = second.StartNewSession();
            Assert.True(secondStart.PreviousSessionEndedUnexpectedly);

            second.RecordStartupComplete();
            second.MarkCleanExit("test");

            var lines = File.ReadAllLines(second.JournalPath);
            Assert.True(lines.Length <= 5);
            Assert.Contains(lines, line => line.Contains("SessionCleanExit", StringComparison.Ordinal));
        }
        finally
        {
            TryDeleteDirectory(root);
        }
    }

    [Fact]
    public void WerLocalDumpSettingsManager_EnableDisable_UsesRegistryStore()
    {
        var dumpDirectory = Path.Combine(Path.GetTempPath(), "CaseGraph.WerTests", Guid.NewGuid().ToString("N"));
        var registry = new InMemoryRegistryValueStore();
        var manager = new WerLocalDumpSettingsManager(
            registry,
            "CaseGraph.App.exe",
            dumpDirectory
        );

        try
        {
            manager.Enable();
            var settings = manager.GetSettings();

            Assert.True(settings.Enabled);
            Assert.Equal(dumpDirectory, settings.DumpFolder);
            Assert.Equal(1, settings.DumpType);
            Assert.Equal(10, settings.DumpCount);
            Assert.True(Directory.Exists(dumpDirectory));
            Assert.Equal(
                InMemoryRegistryValueStore.RegistryValueKindHint.ExpandString,
                registry.GetValueKind(settings.RegistrySubKeyPath, "DumpFolder")
            );

            manager.Disable();
            Assert.False(registry.KeyExists(settings.RegistrySubKeyPath));
        }
        finally
        {
            TryDeleteDirectory(dumpDirectory);
        }
    }

    [Fact]
    public void WerLocalDumpSettingsManager_Enable_UsesProvidedExpandStringDumpFolder()
    {
        var dumpDirectory = Path.Combine(Path.GetTempPath(), "CaseGraph.WerTests", Guid.NewGuid().ToString("N"));
        const string registryDumpFolder = @"%LOCALAPPDATA%\CaseGraphOffline\dumps";
        var registry = new InMemoryRegistryValueStore();
        var manager = new WerLocalDumpSettingsManager(
            registry,
            "CaseGraph.App.exe",
            dumpDirectory,
            registryDumpFolder
        );

        try
        {
            manager.Enable();
            var settings = manager.GetSettings();

            Assert.Equal(
                Environment.ExpandEnvironmentVariables(registryDumpFolder),
                settings.DumpFolder
            );
            Assert.Equal(
                registryDumpFolder,
                registry.GetString(settings.RegistrySubKeyPath, "DumpFolder")
            );
            Assert.Equal(
                InMemoryRegistryValueStore.RegistryValueKindHint.ExpandString,
                registry.GetValueKind(settings.RegistrySubKeyPath, "DumpFolder")
            );
        }
        finally
        {
            manager.Disable();
            TryDeleteDirectory(dumpDirectory);
        }
    }

    private static string CreateTempDirectory()
    {
        var path = Path.Combine(
            Path.GetTempPath(),
            "CaseGraph.Core.Tests.CrashCapture",
            Guid.NewGuid().ToString("N")
        );
        Directory.CreateDirectory(path);
        return path;
    }

    private static void TryDeleteDirectory(string path)
    {
        if (!Directory.Exists(path))
        {
            return;
        }

        for (var attempt = 1; attempt <= 5; attempt++)
        {
            try
            {
                Directory.Delete(path, recursive: true);
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

    private sealed class InMemoryRegistryValueStore : IRegistryValueStore
    {
        private readonly Dictionary<string, Dictionary<string, RegistryValue>> _store =
            new(StringComparer.OrdinalIgnoreCase);

        public void SetString(string subKeyPath, string valueName, string value)
        {
            Upsert(subKeyPath, valueName, value, RegistryValueKindHint.String);
        }

        public void SetExpandString(string subKeyPath, string valueName, string value)
        {
            Upsert(subKeyPath, valueName, value, RegistryValueKindHint.ExpandString);
        }

        public void SetDword(string subKeyPath, string valueName, int value)
        {
            Upsert(subKeyPath, valueName, value, RegistryValueKindHint.Dword);
        }

        public string? GetString(string subKeyPath, string valueName)
        {
            return TryGet(subKeyPath, valueName) as string;
        }

        public int? GetDword(string subKeyPath, string valueName)
        {
            return TryGet(subKeyPath, valueName) is int value ? value : null;
        }

        public bool KeyExists(string subKeyPath)
        {
            return _store.ContainsKey(subKeyPath);
        }

        public void DeleteKeyTree(string subKeyPath)
        {
            _store.Remove(subKeyPath);
        }

        public RegistryValueKindHint? GetValueKind(string subKeyPath, string valueName)
        {
            if (!_store.TryGetValue(subKeyPath, out var values)
                || !values.TryGetValue(valueName, out var value))
            {
                return null;
            }

            return value.Kind;
        }

        private void Upsert(string subKeyPath, string valueName, object value, RegistryValueKindHint kind)
        {
            if (!_store.TryGetValue(subKeyPath, out var values))
            {
                values = new Dictionary<string, RegistryValue>(StringComparer.OrdinalIgnoreCase);
                _store[subKeyPath] = values;
            }

            values[valueName] = new RegistryValue(value, kind);
        }

        private object? TryGet(string subKeyPath, string valueName)
        {
            return _store.TryGetValue(subKeyPath, out var values)
                && values.TryGetValue(valueName, out var value)
                ? value.Data
                : null;
        }

        private sealed record RegistryValue(object Data, RegistryValueKindHint Kind);

        public enum RegistryValueKindHint
        {
            String,
            ExpandString,
            Dword
        }
    }
}
