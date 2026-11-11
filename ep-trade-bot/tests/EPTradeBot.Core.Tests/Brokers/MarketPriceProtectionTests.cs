using EPTradeBot.Core.Brokers.Gtt;

namespace EPTradeBot.Core.Tests.Brokers;

/// <summary>Port of utils/mpp_slab.py. Expected values were taken from OpenAlgo's calculate_protected_price on the same inputs.</summary>
public sealed class MarketPriceProtectionTests
{
    [Theory]
    [InlineData(805.45, "BUY", "EQ", 0.05, 809.5)]
    [InlineData(805.45, "SELL", "EQ", 0.05, 801.4)]
    [InlineData(90.0, "BUY", "EQ", 0.05, 91.8)]
    [InlineData(7.5, "BUY", "CE", 0.05, 7.9)]
    [InlineData(7.5, "SELL", "PE", 0.05, 7.1)]
    [InlineData(250.0, "BUY", "FUT", 0.1, 252.5)]
    [InlineData(780.0, "SELL", "EQ", 0.05, 776.1)]
    [InlineData(600.0, "BUY", "EQ", 0.05, 603.0)]
    [InlineData(12.345, "BUY", "CE", 0.05, 12.7)]
    public void The_price_moves_by_the_slab_and_lands_on_the_tick(double price, string action, string type, double tick, double expected) =>
        Assert.Equal(expected, MarketPriceProtection.ProtectedPrice(price, action, type, tick));

    [Theory]
    [InlineData(830.0, "SELL", "EQ", null, 825.85)]
    [InlineData(99.99, "BUY", "EQ", 0.0, 101.99)]
    public void Without_a_tick_the_price_is_rounded_to_two_places(double price, string action, string type, double? tick, double expected) =>
        Assert.Equal(expected, MarketPriceProtection.ProtectedPrice(price, action, type, tick));

    [Theory]
    [InlineData(99.99, "EQ", 2.0)]
    [InlineData(100.0, "EQ", 1.0)]
    [InlineData(499.99, "FUT", 1.0)]
    [InlineData(500.0, "EQ", 0.5)]
    [InlineData(9.99, "CE", 5.0)]
    [InlineData(10.0, "PE", 3.0)]
    [InlineData(99.99, "CE", 3.0)]
    [InlineData(100.0, "CE", 2.0)]
    [InlineData(500.0, "PE", 1.0)]
    public void The_slab_changes_exactly_at_its_edge(double price, string type, double percent) =>
        Assert.Equal(percent, MarketPriceProtection.Percentage(price, type));

    [Theory]
    [InlineData("OPTIDX", "NIFTY25NOV2624000CE", "OPTIDX")]
    [InlineData(null, "NIFTY25NOV2624000CE", "CE")]
    [InlineData("", "NIFTY25NOV2624000PE", "PE")]
    [InlineData(null, "NIFTY25NOV26FUT", "FUT")]
    [InlineData(null, "SBIN", "EQ")]
    public void The_instrument_type_is_the_master_contract_s_else_read_from_the_symbol(string? master, string symbol, string expected) =>
        Assert.Equal(expected, MarketPriceProtection.InstrumentType(master, symbol));
}
