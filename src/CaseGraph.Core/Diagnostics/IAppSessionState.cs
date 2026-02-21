namespace CaseGraph.Core.Diagnostics;

public interface IAppSessionState
{
    Guid? CurrentCaseId { get; set; }

    Guid? CurrentEvidenceId { get; set; }
}
