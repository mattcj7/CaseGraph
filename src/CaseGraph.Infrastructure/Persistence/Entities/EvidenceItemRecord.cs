namespace CaseGraph.Infrastructure.Persistence.Entities;

public sealed class EvidenceItemRecord
{
    public Guid EvidenceItemId { get; set; }

    public Guid CaseId { get; set; }

    public string DisplayName { get; set; } = string.Empty;

    public string OriginalPath { get; set; } = string.Empty;

    public string OriginalFileName { get; set; } = string.Empty;

    public DateTimeOffset AddedAtUtc { get; set; }

    public long SizeBytes { get; set; }

    public string Sha256Hex { get; set; } = string.Empty;

    public string FileExtension { get; set; } = string.Empty;

    public string SourceType { get; set; } = string.Empty;

    public string ManifestRelativePath { get; set; } = string.Empty;

    public string StoredRelativePath { get; set; } = string.Empty;

    public CaseRecord? Case { get; set; }
}
