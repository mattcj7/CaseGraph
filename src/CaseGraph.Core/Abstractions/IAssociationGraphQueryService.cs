using CaseGraph.Core.Models;

namespace CaseGraph.Core.Abstractions;

public interface IAssociationGraphQueryService
{
    Task<AssociationGraphResult> BuildAsync(
        Guid caseId,
        AssociationGraphBuildOptions options,
        CancellationToken ct
    );
}
