namespace CaseGraph.App.Services;

public interface IUserInteractionService
{
    string? PromptForCaseName();

    IReadOnlyList<string> PickEvidenceFiles();

    string? PickDebugBundleOutputPath(string defaultFileName);

    string? PickReportOutputPath(string defaultFileName);

    void CopyToClipboard(string value);
}
