using CaseGraph.Infrastructure.IncidentWindow;

namespace CaseGraph.Infrastructure.Tests;

public sealed class GeoMathTests
{
    [Fact]
    public void HaversineDistanceMeters_ReturnsZero_ForSamePoint()
    {
        var distance = GeoMath.HaversineDistanceMeters(34.0, -118.0, 34.0, -118.0);
        Assert.Equal(0d, distance, precision: 6);
    }

    [Fact]
    public void HaversineDistanceMeters_MatchesKnownApproximation()
    {
        var distance = GeoMath.HaversineDistanceMeters(0d, 0d, 1d, 0d);
        Assert.InRange(distance, 111_000d, 111_400d);
    }

    [Fact]
    public void GetBoundingBox_ContainsCenter_AndExpandsLatitudeLongitude()
    {
        var box = GeoMath.GetBoundingBox(34.0522, -118.2437, 1_000d);

        Assert.True(box.MinLatitude < 34.0522);
        Assert.True(box.MaxLatitude > 34.0522);
        Assert.True(box.MinLongitude < -118.2437);
        Assert.True(box.MaxLongitude > -118.2437);
        Assert.False(box.CrossesAntiMeridian);
    }
}
