using EPTradeBot.Core.Sandbox;

namespace EPTradeBot.Core.Tests.Trading;

public sealed class SandboxRulesTests
{
    private static readonly SandboxLeverage L = new();

    [Theory]
    [InlineData("NSE", "MIS", "SBIN", true, 5)]
    [InlineData("NSE", "CNC", "SBIN", true, 1)]
    [InlineData("NFO", "NRML", "NIFTY30DEC25FUT", true, 10)]
    [InlineData("NFO", "NRML", "NIFTY30DEC2524000CE", true, 1)]
    [InlineData("NFO", "NRML", "NIFTY30DEC2524000PE", false, 1)]
    [InlineData("NSE_INDEX", "MIS", "NIFTY", true, 1)]
    public void Leverage_follows_openalgo_defaults(string exchange, string product, string symbol, bool buy, int expected)
    {
        Assert.Equal(expected, SandboxRules.Leverage(L, exchange, product, symbol, buy));
    }

    [Fact]
    public void Margin_is_value_over_leverage()
    {
        Assert.Equal(2_000m, SandboxRules.Margin(L, "NSE", "MIS", "SBIN", true, 20, 500m));
    }

    [Theory]
    [InlineData("MARKET", true, 0, 0, 100, 99.5, 100.5, 100.5)]
    [InlineData("MARKET", false, 0, 0, 100, 99.5, 100.5, 99.5)]
    [InlineData("LIMIT", true, 99, 0, 100, null, null, null)]
    [InlineData("LIMIT", true, 101, 0, 100, null, null, 101.0)]
    [InlineData("LIMIT", false, 101, 0, 100, null, null, null)]
    [InlineData("SL", true, 106, 105, 104, null, null, null)]
    [InlineData("SL", true, 106, 105, 105.5, null, null, 105.5)]
    [InlineData("SL", true, 106, 105, 107, null, null, null)]
    [InlineData("SL-M", false, 0, 95, 94, null, null, 94.0)]
    public void Fill_prices_match_the_openalgo_engine(string type, bool buy, double limit, double trigger, double ltp, double? bid, double? ask, double? expected)
    {
        var actual = SandboxRules.FillPrice(type, buy, (decimal)limit, (decimal)trigger, (decimal)ltp, (decimal?)bid, (decimal?)ask);
        Assert.Equal((decimal?)expected, actual);
    }

    [Fact]
    public void A_closing_order_needs_no_margin()
    {
        Assert.Equal(0, SandboxRules.OpeningQuantity(netQuantity: 100, sign: -1, quantity: 100));
        Assert.Equal(20, SandboxRules.OpeningQuantity(netQuantity: 100, sign: -1, quantity: 120));
        Assert.Equal(50, SandboxRules.OpeningQuantity(netQuantity: 100, sign: 1, quantity: 50));
    }

    [Fact]
    public void Adding_averages_the_price_and_adds_margin()
    {
        var o = SandboxRules.ApplyFill(100, 10m, 200m, 1, 100, 20m, 400m);
        Assert.Equal(200, o.NetQuantity);
        Assert.Equal(15m, o.AveragePrice);
        Assert.Equal(600m, o.MarginRemaining);
        Assert.Equal(0m, o.RealizedPnl);
    }

    [Fact]
    public void Partial_close_realises_pnl_and_releases_margin_pro_rata()
    {
        var o = SandboxRules.ApplyFill(100, 10m, 200m, -1, 40, 12m, 0m);
        Assert.Equal(60, o.NetQuantity);
        Assert.Equal(80m, o.RealizedPnl);
        Assert.Equal(80m, o.MarginReleased);
        Assert.Equal(120m, o.MarginRemaining);
    }

    [Fact]
    public void Closing_a_short_realises_the_short_side_pnl()
    {
        var o = SandboxRules.ApplyFill(-50, 200m, 1000m, 1, 50, 190m, 0m);
        Assert.Equal(0, o.NetQuantity);
        Assert.Equal(500m, o.RealizedPnl);
        Assert.Equal(1000m, o.MarginReleased);
    }

    [Fact]
    public void A_fill_larger_than_the_position_flips_it()
    {
        var o = SandboxRules.ApplyFill(100, 10m, 200m, -1, 150, 11m, 110m);
        Assert.Equal(-50, o.NetQuantity);
        Assert.Equal(11m, o.AveragePrice);
        Assert.Equal(100m, o.RealizedPnl);
        Assert.Equal(200m, o.MarginReleased);
        Assert.Equal(110m, o.MarginRemaining);
    }
}
