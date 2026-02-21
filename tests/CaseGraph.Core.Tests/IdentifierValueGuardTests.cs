using CaseGraph.Core.Models;

namespace CaseGraph.Core.Tests;

public sealed class IdentifierValueGuardTests
{
    [Theory]
    [InlineData(TargetIdentifierType.Phone, "   ")]
    [InlineData(TargetIdentifierType.Phone, "()")]
    [InlineData(TargetIdentifierType.SocialHandle, "@")]
    public void TryPrepare_InvalidInputs_ReturnsFalse(TargetIdentifierType type, string valueRaw)
    {
        var isValid = IdentifierValueGuard.TryPrepare(type, valueRaw, out var prepared);

        Assert.False(isValid);
        Assert.True(string.IsNullOrWhiteSpace(prepared) || prepared == valueRaw.Trim());
    }

    [Fact]
    public void TryPrepare_ValidInput_TrimsAndReturnsTrue()
    {
        var isValid = IdentifierValueGuard.TryPrepare(
            TargetIdentifierType.Phone,
            " +1 (555) 123-0001 ",
            out var prepared
        );

        Assert.True(isValid);
        Assert.Equal("+1 (555) 123-0001", prepared);
    }
}
