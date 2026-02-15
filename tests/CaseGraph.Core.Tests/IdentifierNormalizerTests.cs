using CaseGraph.Core.Models;

namespace CaseGraph.Core.Tests;

public sealed class IdentifierNormalizerTests
{
    [Theory]
    [InlineData(TargetIdentifierType.Phone, "(555) 123-0001", "+15551230001")]
    [InlineData(TargetIdentifierType.Phone, "+1 (555) 123-0001", "+15551230001")]
    [InlineData(TargetIdentifierType.Email, " Test@Example.COM ", "test@example.com")]
    [InlineData(TargetIdentifierType.SocialHandle, " @StreetCrew ", "streetcrew")]
    [InlineData(TargetIdentifierType.VehiclePlate, "ab- 123 cd", "AB123CD")]
    [InlineData(TargetIdentifierType.VIN, "1hg-cm8263 3a004352", "1HGCM82633A004352")]
    public void Normalize_ReturnsExpectedValue(
        TargetIdentifierType type,
        string raw,
        string expectedNormalized
    )
    {
        var normalized = IdentifierNormalizer.Normalize(type, raw);
        Assert.Equal(expectedNormalized, normalized);
    }

    [Theory]
    [InlineData("@crewlead", TargetIdentifierType.SocialHandle)]
    [InlineData("PERSON@Example.com", TargetIdentifierType.Email)]
    [InlineData("(555) 123-0001", TargetIdentifierType.Phone)]
    [InlineData("KnownAlias", TargetIdentifierType.Username)]
    public void InferType_ReturnsExpectedType(string raw, TargetIdentifierType expectedType)
    {
        var inferred = IdentifierNormalizer.InferType(raw);
        Assert.Equal(expectedType, inferred);
    }
}
