namespace CaseGraph.Core.Abstractions;

public interface IAssociationGraphExportPathBuilder
{
    string BuildPath(Guid caseId, DateTimeOffset? timestampUtc = null);
}
