using CaseGraph.SyntheticDataGenerator.Models;
using CaseGraph.SyntheticDataGenerator.Services;
using System.Text.Json;

namespace CaseGraph.SyntheticDataGenerator.Tests;

public sealed class SyntheticDatasetGeneratorTests : IDisposable
{
    private readonly List<string> _tempDirectories = [];

    [Fact]
    public async Task GenerateAsync_SameSeedAndOptions_ProducesIdenticalOutput()
    {
        var generator = new SyntheticDatasetGenerator();
        var rootA = CreateTempDirectory();
        var rootB = CreateTempDirectory();

        var optionsA = BuildOptions(rootA);
        var optionsB = BuildOptions(rootB);

        await generator.GenerateAsync(optionsA);
        await generator.GenerateAsync(optionsB);

        var filesA = ReadRelativeFileMap(rootA);
        var filesB = ReadRelativeFileMap(rootB);

        Assert.Equal(filesA.Keys.OrderBy(key => key, StringComparer.Ordinal), filesB.Keys.OrderBy(key => key, StringComparer.Ordinal));
        foreach (var entry in filesA)
        {
            Assert.True(filesB.TryGetValue(entry.Key, out var otherContent), $"Missing file '{entry.Key}' in second output.");
            Assert.Equal(entry.Value, otherContent);
        }
    }

    [Fact]
    public async Task GenerateAsync_CreatesRequiredFilesAndManifestMetadata()
    {
        var generator = new SyntheticDatasetGenerator();
        var root = CreateTempDirectory();
        var options = BuildOptions(root);

        var manifests = await generator.GenerateAsync(options);

        var manifest = Assert.Single(manifests);
        var datasetFolder = Path.Combine(root, manifest.DatasetFolderName);
        Assert.True(Directory.Exists(datasetFolder));

        var expectedFiles = new[]
        {
            "contacts.csv",
            "message_threads.csv",
            "messages.csv",
            "locations.csv",
            "locations.json",
            "device_locations.plist",
            "expected_findings.md",
            "manifest.json",
            "README.md"
        };

        foreach (var fileName in expectedFiles)
        {
            Assert.True(File.Exists(Path.Combine(datasetFolder, fileName)), $"Expected file '{fileName}' was not generated.");
        }

        var manifestPath = Path.Combine(datasetFolder, "manifest.json");
        var manifestModel = JsonSerializer.Deserialize<GeneratorManifest>(await File.ReadAllTextAsync(manifestPath));
        Assert.NotNull(manifestModel);
        Assert.True(manifestModel!.IsSynthetic);
        Assert.Equal(GeneratorOptions.OffenseWindowProfile, manifestModel.Profile);
        Assert.Equal(options.Seed, manifestModel.Seed);
        Assert.Equal(options.PersonCount, manifestModel.PersonCount);
        Assert.Equal(options.ApproximateMessageCount, manifestModel.GeneratedMessageCount);
        Assert.Equal(options.ApproximateLocationCount, manifestModel.GeneratedLocationCount);

        var readme = await File.ReadAllTextAsync(Path.Combine(datasetFolder, "README.md"));
        var findings = await File.ReadAllTextAsync(Path.Combine(datasetFolder, "expected_findings.md"));
        var messagesCsv = await File.ReadAllTextAsync(Path.Combine(datasetFolder, "messages.csv"));

        Assert.Contains("synthetic", readme, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("fictional", readme, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Suggested offense window", findings, StringComparison.Ordinal);
        Assert.Contains("msg-0001", messagesCsv, StringComparison.Ordinal);
    }

    public void Dispose()
    {
        foreach (var directory in _tempDirectories)
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    private static GeneratorOptions BuildOptions(string outputFolder)
    {
        return new GeneratorOptions
        {
            Seed = 1337,
            DatasetCount = 1,
            PersonCount = 6,
            ApproximateMessageCount = 24,
            ApproximateLocationCount = 12,
            Profile = GeneratorOptions.OffenseWindowProfile,
            OutputFolder = outputFolder
        };
    }

    private string CreateTempDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), $"casegraph-synth-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        _tempDirectories.Add(path);
        return path;
    }

    private static Dictionary<string, string> ReadRelativeFileMap(string root)
    {
        return Directory
            .GetFiles(root, "*", SearchOption.AllDirectories)
            .ToDictionary(
                path => Path.GetRelativePath(root, path),
                path => File.ReadAllText(path),
                StringComparer.Ordinal
            );
    }
}
