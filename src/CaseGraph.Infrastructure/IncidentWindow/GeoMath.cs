namespace CaseGraph.Infrastructure.IncidentWindow;

public static class GeoMath
{
    private const double EarthRadiusMeters = 6_371_008.8d;

    public static double HaversineDistanceMeters(
        double latitudeA,
        double longitudeA,
        double latitudeB,
        double longitudeB
    )
    {
        var lat1 = DegreesToRadians(latitudeA);
        var lat2 = DegreesToRadians(latitudeB);
        var deltaLat = DegreesToRadians(latitudeB - latitudeA);
        var deltaLon = DegreesToRadians(longitudeB - longitudeA);

        var sinLat = Math.Sin(deltaLat / 2d);
        var sinLon = Math.Sin(deltaLon / 2d);
        var a = (sinLat * sinLat)
            + (Math.Cos(lat1) * Math.Cos(lat2) * sinLon * sinLon);
        var c = 2d * Math.Atan2(Math.Sqrt(a), Math.Sqrt(1d - a));
        return EarthRadiusMeters * c;
    }

    public static GeoBoundingBox GetBoundingBox(
        double centerLatitude,
        double centerLongitude,
        double radiusMeters
    )
    {
        if (radiusMeters < 0d)
        {
            throw new ArgumentOutOfRangeException(nameof(radiusMeters), "Radius must be non-negative.");
        }

        if (centerLatitude is < -90d or > 90d)
        {
            throw new ArgumentOutOfRangeException(
                nameof(centerLatitude),
                "Latitude must be between -90 and 90 degrees."
            );
        }

        if (centerLongitude is < -180d or > 180d)
        {
            throw new ArgumentOutOfRangeException(
                nameof(centerLongitude),
                "Longitude must be between -180 and 180 degrees."
            );
        }

        if (radiusMeters == 0d)
        {
            return new GeoBoundingBox(
                MinLatitude: centerLatitude,
                MaxLatitude: centerLatitude,
                MinLongitude: centerLongitude,
                MaxLongitude: centerLongitude,
                CrossesAntiMeridian: false
            );
        }

        var angularDistance = radiusMeters / EarthRadiusMeters;
        var latitudeRadians = DegreesToRadians(centerLatitude);

        var minLatitude = Math.Max(-90d, centerLatitude - RadiansToDegrees(angularDistance));
        var maxLatitude = Math.Min(90d, centerLatitude + RadiansToDegrees(angularDistance));

        double longitudeDeltaDegrees;
        if (Math.Abs(Math.Cos(latitudeRadians)) < 1e-12 || minLatitude <= -90d || maxLatitude >= 90d)
        {
            longitudeDeltaDegrees = 180d;
        }
        else
        {
            var ratio = Math.Sin(angularDistance) / Math.Cos(latitudeRadians);
            longitudeDeltaDegrees = RadiansToDegrees(Math.Asin(Math.Clamp(ratio, -1d, 1d)));
        }

        var minLongitude = NormalizeLongitude(centerLongitude - longitudeDeltaDegrees);
        var maxLongitude = NormalizeLongitude(centerLongitude + longitudeDeltaDegrees);

        return new GeoBoundingBox(
            MinLatitude: minLatitude,
            MaxLatitude: maxLatitude,
            MinLongitude: minLongitude,
            MaxLongitude: maxLongitude,
            CrossesAntiMeridian: minLongitude > maxLongitude
        );
    }

    private static double DegreesToRadians(double degrees) => degrees * (Math.PI / 180d);

    private static double RadiansToDegrees(double radians) => radians * (180d / Math.PI);

    private static double NormalizeLongitude(double value)
    {
        var normalized = value % 360d;
        if (normalized > 180d)
        {
            normalized -= 360d;
        }
        else if (normalized < -180d)
        {
            normalized += 360d;
        }

        return normalized;
    }
}

public sealed record GeoBoundingBox(
    double MinLatitude,
    double MaxLatitude,
    double MinLongitude,
    double MaxLongitude,
    bool CrossesAntiMeridian
);
