using EPTradeBot.Core.Options;

namespace EPTradeBot.Core.Tests;

/// <summary>PyNumber.Round must give what Python's round() gives: by the exact binary value, and only a true tie to the even digit.</summary>
public sealed class PyNumberTests
{
    [Theory]
    [InlineData(2.675, 2, 2.67)]      // the binary value is just under 2.675
    [InlineData(0.125, 2, 0.12)]      // an exact tie goes to the even digit
    [InlineData(0.375, 2, 0.38)]
    [InlineData(-0.125, 2, -0.12)]
    [InlineData(1234.5, 0, 1234.0)]
    [InlineData(1235.5, 0, 1236.0)]
    [InlineData(3138458.865, 2, 3138458.87)] // the binary value is just over .865
    [InlineData(1.005, 2, 1.0)]
    [InlineData(0.0, 2, 0.0)]
    [InlineData(123456.789012, 4, 123456.789)]
    public void Rounds_like_Python(double x, int digits, double expected) => Assert.Equal(expected, PyNumber.Round(x, digits));

    [Fact]
    public void Leaves_non_finite_values_alone()
    {
        Assert.True(double.IsNaN(PyNumber.Round(double.NaN, 2)));
        Assert.Equal(double.PositiveInfinity, PyNumber.Round(double.PositiveInfinity, 2));
    }
}
