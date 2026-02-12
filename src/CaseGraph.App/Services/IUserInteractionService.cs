namespace CaseGraph.App.Services;

public interface IUserInteractionService
{
    string? PromptForCaseName();

    IReadOnlyList<string> PickEvidenceFiles();

    void CopyToClipboard(string value);
}
