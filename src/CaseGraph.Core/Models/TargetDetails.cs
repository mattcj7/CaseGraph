namespace CaseGraph.Core.Models;

public sealed record TargetDetails(
    TargetSummary Summary,
    IReadOnlyList<TargetAliasInfo> Aliases,
    IReadOnlyList<TargetIdentifierInfo> Identifiers,
    int WhereSeenMessageCount,
    TargetGlobalPersonInfo? GlobalPerson
);
