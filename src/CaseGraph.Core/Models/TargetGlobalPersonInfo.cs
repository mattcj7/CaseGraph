namespace CaseGraph.Core.Models;

public sealed record TargetGlobalPersonInfo(
    Guid GlobalEntityId,
    string DisplayName,
    IReadOnlyList<GlobalPersonAliasInfo> Aliases,
    IReadOnlyList<GlobalPersonIdentifierInfo> Identifiers,
    IReadOnlyList<GlobalPersonCaseReference> OtherCases
);
