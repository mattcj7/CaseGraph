namespace CaseGraph.Core.Models;

public static class AffiliationSummaryFormatter
{
    public const string NoOrganizationRecorded = "No organization recorded";

    public static string Format(
        IReadOnlyList<TargetOrganizationAffiliationInfo> affiliations,
        int maxVisible = 3
    )
    {
        if (affiliations.Count == 0 || maxVisible <= 0)
        {
            return NoOrganizationRecorded;
        }

        var ordered = affiliations
            .OrderByDescending(IsCurrentAffiliation)
            .ThenByDescending(item => item.LastConfirmedDateUtc ?? item.StartDateUtc ?? DateTimeOffset.MinValue)
            .ThenBy(item => item.OrganizationName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.OrganizationId)
            .ToList();

        if (ordered.Count == 0)
        {
            return NoOrganizationRecorded;
        }

        var visibleCount = Math.Min(maxVisible, ordered.Count);
        var names = ordered
            .Take(visibleCount)
            .Select(item => item.OrganizationName)
            .ToList();

        if (ordered.Count > visibleCount)
        {
            names[^1] = $"{names[^1]} +{ordered.Count - visibleCount} more";
        }

        return string.Join(", ", names);
    }

    public static bool IsCurrentAffiliation(TargetOrganizationAffiliationInfo affiliation)
    {
        return string.Equals(affiliation.OrganizationStatus, "active", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(affiliation.MembershipStatus, "former", StringComparison.OrdinalIgnoreCase);
    }
}
