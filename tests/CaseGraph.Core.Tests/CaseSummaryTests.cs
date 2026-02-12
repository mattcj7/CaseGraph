using CaseGraph.Core.Models;

namespace CaseGraph.Core.Tests;

public class CaseSummaryTests
{
    [Fact]
    public void Constructor_SetsExpectedValues()
    {
        var summary = new CaseSummary("CASE-001", "Homicide Unit - Baseline Case");

        Assert.Equal("CASE-001", summary.CaseId);
        Assert.Equal("Homicide Unit - Baseline Case", summary.DisplayName);
    }
}
