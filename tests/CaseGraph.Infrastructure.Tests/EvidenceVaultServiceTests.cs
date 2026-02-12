using CaseGraph.Core.Abstractions;
using CaseGraph.Infrastructure.Services;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace CaseGraph.Infrastructure.Tests;

public sealed class EvidenceVaultServiceTests
{
    [Fact]
    public async Task ImportEvidenceFileAsync_CreatesStoredFileManifestAndMatchingHash()
    {
        using var fixture = new WorkspaceFixture();
        var (workspace, vault, caseInfo) = await fixture.CreateAsync();
        var sourceFile = fixture.CreateSourceFile("sample.txt", "Alpha bravo charlie.");

        var evidenceItem = await vault.ImportEvidenceFileAsync(caseInfo, sourceFile, null, CancellationToken.None);

        var storedPath = fixture.ResolveCasePath(caseInfo.CaseId, evidenceItem.StoredRelativePath);
        var manifestPath = fixture.ResolveCasePath(caseInfo.CaseId, evidenceItem.ManifestRelativePath);

        Assert.True(File.Exists(storedPath));
        Assert.True(File.Exists(manifestPath));

        var computedHash = await ComputeSha256Async(storedPath);
        Assert.Equal(evidenceItem.Sha256Hex, computedHash);

        var manifestJson = await File.ReadAllTextAsync(manifestPath);
        using var manifest = JsonDocument.Parse(manifestJson);
        Assert.Equal(1, manifest.RootElement.GetProperty("SchemaVersion").GetInt32());
        Assert.Equal(evidenceItem.Sha256Hex, manifest.RootElement.GetProperty("Sha256Hex").GetString());
    }

    [Fact]
    public async Task VerifyEvidenceAsync_ReturnsOkTrue_WhenFileUntouched()
    {
        using var fixture = new WorkspaceFixture();
        var (_, vault, caseInfo) = await fixture.CreateAsync();
        var sourceFile = fixture.CreateSourceFile("evidence.bin", "Integrity baseline");
        var evidenceItem = await vault.ImportEvidenceFileAsync(caseInfo, sourceFile, null, CancellationToken.None);

        var (ok, message) = await vault.VerifyEvidenceAsync(
            caseInfo,
            evidenceItem,
            null,
            CancellationToken.None
        );

        Assert.True(ok, message);
    }

    [Fact]
    public async Task VerifyEvidenceAsync_ReturnsOkFalse_AfterTampering()
    {
        using var fixture = new WorkspaceFixture();
        var (_, vault, caseInfo) = await fixture.CreateAsync();
        var sourceFile = fixture.CreateSourceFile("tamper.dat", "Original bytes");
        var evidenceItem = await vault.ImportEvidenceFileAsync(caseInfo, sourceFile, null, CancellationToken.None);

        var storedPath = fixture.ResolveCasePath(caseInfo.CaseId, evidenceItem.StoredRelativePath);
        await File.AppendAllTextAsync(storedPath, "tampered");

        var (ok, _) = await vault.VerifyEvidenceAsync(
            caseInfo,
            evidenceItem,
            null,
            CancellationToken.None
        );

        Assert.False(ok);
    }

    private static async Task<string> ComputeSha256Async(string filePath)
    {
        await using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var sha256 = SHA256.Create();
        var hashBytes = await sha256.ComputeHashAsync(stream);

        return Convert.ToHexString(hashBytes).ToLowerInvariant();
    }

    private sealed class WorkspaceFixture : IDisposable
    {
        public string WorkspaceRoot { get; } = Path.Combine(
            Path.GetTempPath(),
            "CaseGraph.Infrastructure.Tests",
            Guid.NewGuid().ToString("N")
        );

        public async Task<(CaseWorkspaceService workspace, EvidenceVaultService vault, CaseGraph.Core.Models.CaseInfo caseInfo)> CreateAsync()
        {
            Directory.CreateDirectory(WorkspaceRoot);

            var clock = new FixedClock(new DateTimeOffset(2026, 2, 12, 12, 0, 0, TimeSpan.Zero));
            var workspaceService = new CaseWorkspaceService(clock, WorkspaceRoot);
            var caseInfo = await workspaceService.CreateCaseAsync("Demo Case", CancellationToken.None);
            var vaultService = new EvidenceVaultService(clock, workspaceService, WorkspaceRoot);

            return (workspaceService, vaultService, caseInfo);
        }

        public string CreateSourceFile(string fileName, string content)
        {
            var sourceDirectory = Path.Combine(WorkspaceRoot, "source");
            Directory.CreateDirectory(sourceDirectory);

            var path = Path.Combine(sourceDirectory, fileName);
            File.WriteAllText(path, content, Encoding.UTF8);

            return path;
        }

        public string ResolveCasePath(Guid caseId, string relativePath)
        {
            var root = Path.Combine(WorkspaceRoot, "cases", caseId.ToString("D"));
            return Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar));
        }

        public void Dispose()
        {
            if (Directory.Exists(WorkspaceRoot))
            {
                Directory.Delete(WorkspaceRoot, recursive: true);
            }
        }
    }

    private sealed class FixedClock : IClock
    {
        public FixedClock(DateTimeOffset utcNow)
        {
            UtcNow = utcNow;
        }

        public DateTimeOffset UtcNow { get; }
    }
}
