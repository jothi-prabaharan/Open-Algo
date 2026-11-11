using EPTradeBot.Core.Indicators;

namespace EPTradeBot.Core.Tests;

/// <summary>What the recorded values cannot show: cases where rounding decides the answer.</summary>
public sealed class IndicatorKernelTests
{
    [Theory]
    [InlineData(1.3)]
    [InlineData(1.8)]
    [InlineData(2.3)]
    public void The_deviation_of_a_flat_series_is_zero_not_the_noise_of_rounding(double level)
    {
        // For these levels the variance comes out a hair below zero (-4e-16 and the like); its root is 0, not 2e-8.
        var data = Enumerable.Repeat(level, 20).ToArray();
        Assert.Equal(0.0, Kernels.Stdev(data, 14)[13]);
    }
}
