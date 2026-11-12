using EPTradeBot.Core.Portfolio;

namespace EPTradeBot.Core.Tests;

/// <summary>Python's fixed-point formatting: the exact binary value rounded half to even, a negative zero keeping its sign.</summary>
public sealed class PyFormatTests
{
    [Theory]
    [InlineData(0.125, 2, "0.12")]
    [InlineData(0.375, 2, "0.38")]
    [InlineData(2.5, 0, "2")]
    [InlineData(3.5, 0, "4")]
    [InlineData(12.5, 0, "12")]
    [InlineData(1.005, 2, "1.00")]
    [InlineData(2.675, 2, "2.67")]
    [InlineData(-0.04, 1, "-0.0")]
    [InlineData(-1.25, 1, "-1.2")]
    [InlineData(0.0, 1, "0.0")]
    [InlineData(1234.5678, 2, "1234.57")]
    [InlineData(0.05, 0, "0")]
    public void Fixed_matches_python(double value, int digits, string expected) => Assert.Equal(expected, PyFormat.Fixed(value, digits));

    [Fact]
    public void Percent_scales_then_formats() => Assert.Equal("12.5%", PyFormat.Percent(0.125, 1));
}
