using AirBridge.App;

namespace AirBridge.Tests;

public sealed class SystemTextScaleTests
{
    [Theory]
    [InlineData(null, 1.0f)]
    [InlineData(100, 1.0f)]
    [InlineData(125, 1.25f)]
    [InlineData(225, 2.25f)]
    [InlineData(75, 1.0f)]
    [InlineData(300, 2.25f)]
    [InlineData("150", 1.5f)]
    [InlineData("invalid", 1.0f)]
    public void Normalize_ClampsWindowsPercentage(object? value, float expected)
    {
        Assert.Equal(expected, SystemTextScale.Normalize(value), 3);
    }

    [Fact]
    public void Normalize_ClampsLargeRegistryInteger()
    {
        Assert.Equal(2.25f, SystemTextScale.Normalize(long.MaxValue), 3);
    }
}
