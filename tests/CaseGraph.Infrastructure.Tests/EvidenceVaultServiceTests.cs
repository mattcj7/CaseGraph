using CaseGraph.Core.Abstractions;
using CaseGraph.Core.Models;
using CaseGraph.Infrastructure.Persistence;
using CaseGraph.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace CaseGraph.Infrastructure.Tests;

public sealed class EvidenceVaultServiceTests
{
    [Fact]
    public async Task CreateCaseAsync_WritesCaseRecordToSqlite()
    {
        await using var fixture = await WorkspaceFixture.CreateAsync();
        var workspace = fixture.Services.GetRequiredService<ICaseWorkspaceService>();

        var caseInfo = await workspace.CreateCaseAsync("Case One", CancellationToken.None);

        await using var db = await fixture.CreateDbContextAsync();
        var caseRecord = await db.Cases.FirstOrDefaultAsync(c => c.CaseId == caseInfo.CaseId);

        Assert.NotNull(caseRecord);
        Assert.Equal("Case One", caseRecord.Name);
    }

    [Fact]
    public async Task ImportEvidenceFileAsync_CreatesStoredFileManifestHashAndAudit()
    {
        await using var fixture = await WorkspaceFixture.CreateAsync();
        var workspace = fixture.Services.GetRequiredService<ICaseWorkspaceService>();
        var vault = fixture.Services.GetRequiredService<IEvidenceVaultService>();

        var caseInfo = await workspace.CreateCaseAsync("Import Case", CancellationToken.None);
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

        await using var db = await fixture.CreateDbContextAsync();
        var evidenceRecord = await db.EvidenceItems.FirstOrDefaultAsync(e => e.EvidenceItemId == evidenceItem.EvidenceItemId);
        Assert.NotNull(evidenceRecord);

        var importAudit = await db.AuditEvents
            .Where(a => a.ActionType == "EvidenceImported" && a.EvidenceItemId == evidenceItem.EvidenceItemId)
            .FirstOrDefaultAsync();
        Assert.NotNull(importAudit);
    }

    [Fact]
    public async Task VerifyEvidenceAsync_WritesOkAndFailAuditEvents()
    {
        await using var fixture = await WorkspaceFixture.CreateAsync();
        var workspace = fixture.Services.GetRequiredService<ICaseWorkspaceService>();
        var vault = fixture.Services.GetRequiredService<IEvidenceVaultService>();

        var caseInfo = await workspace.CreateCaseAsync("Verify Case", CancellationToken.None);
        var sourceFile = fixture.CreateSourceFile("verify.bin", "Integrity baseline");
        var evidenceItem = await vault.ImportEvidenceFileAsync(caseInfo, sourceFile, null, CancellationToken.None);

        var (okBeforeTamper, _) = await vault.VerifyEvidenceAsync(
            caseInfo,
            evidenceItem,
            null,
            CancellationToken.None
        );
        Assert.True(okBeforeTamper);

        var storedPath = fixture.ResolveCasePath(caseInfo.CaseId, evidenceItem.StoredRelativePath);
        await File.AppendAllTextAsync(storedPath, "tampered");

        var (okAfterTamper, _) = await vault.VerifyEvidenceAsync(
            caseInfo,
            evidenceItem,
            null,
            CancellationToken.None
        );
        Assert.False(okAfterTamper);

        await using var db = await fixture.CreateDbContextAsync();
        var okAudit = await db.AuditEvents
            .Where(a => a.ActionType == "EvidenceVerifiedOk" && a.EvidenceItemId == evidenceItem.EvidenceItemId)
            .FirstOrDefaultAsync();
        var failAudit = await db.AuditEvents
            .Where(a => a.ActionType == "EvidenceVerifiedFail" && a.EvidenceItemId == evidenceItem.EvidenceItemId)
            .FirstOrDefaultAsync();

        Assert.NotNull(okAudit);
        Assert.NotNull(failAudit);
    }

    private static async Task<string> ComputeSha256Async(string filePath)
    {
        await using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var sha256 = SHA256.Create();
        var hashBytes = await sha256.ComputeHashAsync(stream);
        return Convert.ToHexString(hashBytes).ToLowerInvariant();
    }

    private sealed class WorkspaceFixture : IAsyncDisposable
    {
        private readonly ServiceProvider _provider;
        private readonly TestWorkspacePathProvider _pathProvider;

        private WorkspaceFixture(ServiceProvider provider, TestWorkspacePathProvider pathProvider)
        {
            _provider = provider;
            _pathProvider = pathProvider;
        }

        public IServiceProvider Services => _provider;

        public static async Task<WorkspaceFixture> CreateAsync()
        {
            var workspaceRoot = Path.Combine(
                Path.GetTempPath(),
                "CaseGraph.Infrastructure.Tests",
                Guid.NewGuid().ToString("N")
            );
            Directory.CreateDirectory(workspaceRoot);

            var pathProvider = new TestWorkspacePathProvider(workspaceRoot);
            var services = new ServiceCollection();
            services.AddSingleton<IClock>(
                new FixedClock(new DateTimeOffset(2026, 2, 13, 12, 0, 0, TimeSpan.Zero))
            );
            services.AddSingleton<IWorkspacePathProvider>(pathProvider);
            services.AddDbContextFactory<WorkspaceDbContext>(options =>
            {
                Directory.CreateDirectory(pathProvider.WorkspaceRoot);
                options.UseSqlite($"Data Source={pathProvider.WorkspaceDbPath}");
            });
            services.AddSingleton<IWorkspaceDatabaseInitializer, WorkspaceDatabaseInitializer>();
            services.AddSingleton<IAuditLogService, AuditLogService>();
            services.AddSingleton<ICaseWorkspaceService, CaseWorkspaceService>();
            services.AddSingleton<IEvidenceVaultService, EvidenceVaultService>();

            var provider = services.BuildServiceProvider();

            var initializer = provider.GetRequiredService<IWorkspaceDatabaseInitializer>();
            await initializer.EnsureInitializedAsync(CancellationToken.None);

            return new WorkspaceFixture(provider, pathProvider);
        }

        public string CreateSourceFile(string fileName, string content)
        {
            var sourceDirectory = Path.Combine(_pathProvider.WorkspaceRoot, "source");
            Directory.CreateDirectory(sourceDirectory);

            var path = Path.Combine(sourceDirectory, fileName);
            File.WriteAllText(path, content, Encoding.UTF8);
            return path;
        }

        public string ResolveCasePath(Guid caseId, string relativePath)
        {
            var root = Path.Combine(_pathProvider.CasesRoot, caseId.ToString("D"));
            return Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar));
        }

        public Task<WorkspaceDbContext> CreateDbContextAsync()
        {
            var factory = _provider.GetRequiredService<IDbContextFactory<WorkspaceDbContext>>();
            return factory.CreateDbContextAsync(CancellationToken.None);
        }

        public ValueTask DisposeAsync()
        {
            return DisposeInternalAsync();
        }

        private async ValueTask DisposeInternalAsync()
        {
            await _provider.DisposeAsync();

            if (!Directory.Exists(_pathProvider.WorkspaceRoot))
            {
                return;
            }

            for (var attempt = 1; attempt <= 5; attempt++)
            {
                try
                {
                    Directory.Delete(_pathProvider.WorkspaceRoot, recursive: true);
                    return;
                }
                catch (IOException)
                {
                    if (attempt == 5)
                    {
                        return;
                    }

                    await Task.Delay(50);
                }
                catch (UnauthorizedAccessException)
                {
                    if (attempt == 5)
                    {
                        return;
                    }

                    await Task.Delay(50);
                }
            }
        }
    }

    private sealed class TestWorkspacePathProvider : IWorkspacePathProvider
    {
        public TestWorkspacePathProvider(string workspaceRoot)
        {
            WorkspaceRoot = workspaceRoot;
            WorkspaceDbPath = Path.Combine(workspaceRoot, "workspace.db");
            CasesRoot = Path.Combine(workspaceRoot, "cases");
        }

        public string WorkspaceRoot { get; }

        public string WorkspaceDbPath { get; }

        public string CasesRoot { get; }
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
