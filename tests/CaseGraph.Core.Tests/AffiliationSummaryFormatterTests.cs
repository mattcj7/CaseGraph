using CaseGraph.Core.Models;

namespace CaseGraph.Core.Tests;

public sealed class AffiliationSummaryFormatterTests
{
    [Fact]
    public void Format_WhenNoAffiliations_ReturnsFallback()
    {
        var result = AffiliationSummaryFormatter.Format([]);

        Assert.Equal("No organization recorded", result);
    }

    [Fact]
    public void Format_WhenSingleAffiliation_ReturnsOrganizationName()
    {
        var result = AffiliationSummaryFormatter.Format(
            [CreateAffiliation("Rollin 60s", organizationStatus: "active", membershipStatus: "member")]
        );

        Assert.Equal("Rollin 60s", result);
    }

    [Fact]
    public void Format_PrioritizesCurrentAffiliations_AndAddsOverflowSuffix()
    {
        var result = AffiliationSummaryFormatter.Format(
            [
                CreateAffiliation("Former Set", "active", "former", new DateTimeOffset(2026, 3, 1, 0, 0, 0, TimeSpan.Zero)),
                CreateAffiliation("Current A", "active", "member", new DateTimeOffset(2026, 2, 10, 0, 0, 0, TimeSpan.Zero)),
                CreateAffiliation("Inactive Org", "inactive", "member", new DateTimeOffset(2026, 3, 5, 0, 0, 0, TimeSpan.Zero)),
                CreateAffiliation("Current B", "active", "associate", new DateTimeOffset(2026, 2, 1, 0, 0, 0, TimeSpan.Zero))
            ]
        );

        Assert.Equal("Current A, Current B, Inactive Org +1 more", result);
    }

    private static TargetOrganizationAffiliationInfo CreateAffiliation(
        string organizationName,
        string organizationStatus,
        string membershipStatus,
        DateTimeOffset? lastConfirmedDateUtc = null
    )
    {
        return new TargetOrganizationAffiliationInfo(
            Guid.NewGuid(),
            Guid.NewGuid(),
            organizationName,
            "gang",
            organizationStatus,
            null,
            null,
            membershipStatus,
            70,
            null,
            null,
            lastConfirmedDateUtc
        );
    }
}
