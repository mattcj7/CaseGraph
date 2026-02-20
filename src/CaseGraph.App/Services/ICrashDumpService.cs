using CaseGraph.Core.Diagnostics;

namespace CaseGraph.App.Services;

public interface ICrashDumpService
{
    WerLocalDumpSettings GetSettings();

    void SetEnabled(bool enabled);
}
