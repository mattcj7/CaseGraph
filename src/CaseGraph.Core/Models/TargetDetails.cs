namespace CaseGraph.Core.Models;

public sealed record TargetDetails(
    TargetSummary Summary,
    IReadOnlyList<TargetAliasInfo> Aliases,
    IReadOnlyList<TargetOrganizationAffiliationInfo> Affiliations,
    IReadOnlyList<TargetIdentifierInfo> Identifiers,
    int WhereSeenMessageCount,
    TargetGlobalPersonInfo? GlobalPerson
);
