namespace CaseGraph.Core.Diagnostics;

public sealed class AppSessionState : IAppSessionState
{
    private readonly object _sync = new();
    private Guid? _currentCaseId;
    private Guid? _currentEvidenceId;

    public Guid? CurrentCaseId
    {
        get
        {
            lock (_sync)
            {
                return _currentCaseId;
            }
        }
        set
        {
            lock (_sync)
            {
                _currentCaseId = value;
            }
        }
    }

    public Guid? CurrentEvidenceId
    {
        get
        {
            lock (_sync)
            {
                return _currentEvidenceId;
            }
        }
        set
        {
            lock (_sync)
            {
                _currentEvidenceId = value;
            }
        }
    }
}
