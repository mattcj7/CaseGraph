using CaseGraph.Core.Abstractions;
using System.Globalization;

namespace CaseGraph.Infrastructure.Services;

public sealed class AssociationGraphExportPathBuilder : IAssociationGraphExportPathBuilder
{
    private readonly IWorkspacePathProvider _workspacePathProvider;
    private readonly IClock _clock;

    public AssociationGraphExportPathBuilder(
        IWorkspacePathProvider workspacePathProvider,
        IClock clock
    )
    {
        _workspacePathProvider = workspacePathProvider;
        _clock = clock;
    }

    public string BuildPath(Guid caseId, DateTimeOffset? timestampUtc = null)
    {
        if (caseId == Guid.Empty)
        {
            throw new ArgumentException("CaseId is required.", nameof(caseId));
        }

        var exportsRoot = Path.Combine(_workspacePathProvider.WorkspaceRoot, "session", "exports");
        Directory.CreateDirectory(exportsRoot);

        var timestamp = (timestampUtc ?? _clock.UtcNow)
            .ToUniversalTime()
            .ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        var fileName = $"graph-{caseId:D}-{timestamp}.png";
        return Path.Combine(exportsRoot, fileName);
    }
}
