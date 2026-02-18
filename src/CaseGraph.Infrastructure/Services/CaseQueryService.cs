using CaseGraph.Core.Abstractions;
using CaseGraph.Core.Models;
using CaseGraph.Infrastructure.Persistence;
using CaseGraph.Infrastructure.Persistence.Entities;
using Microsoft.EntityFrameworkCore;

namespace CaseGraph.Infrastructure.Services;

public sealed class CaseQueryService : ICaseQueryService
{
    private readonly IDbContextFactory<WorkspaceDbContext> _dbContextFactory;
    private readonly IWorkspaceDatabaseInitializer _databaseInitializer;

    public CaseQueryService(
        IDbContextFactory<WorkspaceDbContext> dbContextFactory,
        IWorkspaceDatabaseInitializer databaseInitializer
    )
    {
        _dbContextFactory = dbContextFactory;
        _databaseInitializer = databaseInitializer;
    }

    public async Task<IReadOnlyList<CaseInfo>> GetRecentCasesAsync(CancellationToken ct)
    {
        await _databaseInitializer.EnsureInitializedAsync(ct);

        await using var db = await _dbContextFactory.CreateDbContextAsync(ct);
        var orderedCaseIds = await db.CaseOrderKeys
            .AsNoTracking()
            .OrderByDescending(
                record =>
                    EF.Property<string?>(record, nameof(CaseOrderKeyRecord.LastOpenedAtUtc))
                    ?? EF.Property<string>(record, nameof(CaseOrderKeyRecord.CreatedAtUtc))
            )
            .ThenByDescending(
                record => EF.Property<string>(record, nameof(CaseOrderKeyRecord.CreatedAtUtc))
            )
            .ThenByDescending(record => record.CaseId)
            .Select(record => record.CaseId)
            .ToListAsync(ct);

        if (orderedCaseIds.Count == 0)
        {
            return Array.Empty<CaseInfo>();
        }

        var records = await db.Cases
            .AsNoTracking()
            .Where(record => orderedCaseIds.Contains(record.CaseId))
            .ToListAsync(ct);
        var recordsById = records.ToDictionary(record => record.CaseId);

        var orderedCases = new List<CaseInfo>(orderedCaseIds.Count);
        foreach (var caseId in orderedCaseIds)
        {
            if (recordsById.TryGetValue(caseId, out var record))
            {
                orderedCases.Add(MapCaseInfo(record));
            }
        }

        return orderedCases;
    }

    public async Task<CaseInfo?> GetCaseAsync(Guid caseId, CancellationToken ct)
    {
        if (caseId == Guid.Empty)
        {
            throw new ArgumentException("Case id is required.", nameof(caseId));
        }

        await _databaseInitializer.EnsureInitializedAsync(ct);

        await using var db = await _dbContextFactory.CreateDbContextAsync(ct);
        var record = await db.Cases
            .AsNoTracking()
            .FirstOrDefaultAsync(caseRecord => caseRecord.CaseId == caseId, ct);
        return record is null ? null : MapCaseInfo(record);
    }

    public async Task<IReadOnlyList<EvidenceItem>> GetEvidenceForCaseAsync(Guid caseId, CancellationToken ct)
    {
        if (caseId == Guid.Empty)
        {
            throw new ArgumentException("Case id is required.", nameof(caseId));
        }

        await _databaseInitializer.EnsureInitializedAsync(ct);

        await using var db = await _dbContextFactory.CreateDbContextAsync(ct);
        var orderedEvidenceIds = await db.EvidenceOrderKeys
            .AsNoTracking()
            .Where(record => record.CaseId == caseId)
            .OrderByDescending(
                record => EF.Property<string>(record, nameof(EvidenceOrderKeyRecord.AddedAtUtc))
            )
            .ThenByDescending(record => record.EvidenceItemId)
            .Select(record => record.EvidenceItemId)
            .ToListAsync(ct);

        if (orderedEvidenceIds.Count == 0)
        {
            return Array.Empty<EvidenceItem>();
        }

        var records = await db.EvidenceItems
            .AsNoTracking()
            .Where(record => record.CaseId == caseId)
            .Where(record => orderedEvidenceIds.Contains(record.EvidenceItemId))
            .ToListAsync(ct);
        var recordsById = records.ToDictionary(record => record.EvidenceItemId);

        var orderedEvidence = new List<EvidenceItem>(orderedEvidenceIds.Count);
        foreach (var evidenceId in orderedEvidenceIds)
        {
            if (recordsById.TryGetValue(evidenceId, out var record))
            {
                orderedEvidence.Add(MapEvidenceItem(record));
            }
        }

        return orderedEvidence;
    }

    private static CaseInfo MapCaseInfo(CaseRecord record)
    {
        return new CaseInfo
        {
            CaseId = record.CaseId,
            Name = record.Name,
            CreatedAtUtc = record.CreatedAtUtc,
            LastOpenedAtUtc = record.LastOpenedAtUtc
        };
    }

    private static EvidenceItem MapEvidenceItem(EvidenceItemRecord record)
    {
        return new EvidenceItem
        {
            EvidenceItemId = record.EvidenceItemId,
            CaseId = record.CaseId,
            DisplayName = record.DisplayName,
            OriginalPath = record.OriginalPath,
            OriginalFileName = record.OriginalFileName,
            AddedAtUtc = record.AddedAtUtc,
            SizeBytes = record.SizeBytes,
            Sha256Hex = record.Sha256Hex,
            FileExtension = record.FileExtension,
            SourceType = record.SourceType,
            ManifestRelativePath = record.ManifestRelativePath,
            StoredRelativePath = record.StoredRelativePath
        };
    }
}
