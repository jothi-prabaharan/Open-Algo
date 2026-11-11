namespace EPTradeBot.Core.Brokers.Gtt;

/// <summary>
/// Port of utils/mpp_slab.py. A GTT fires a LIMIT order at Zerodha and Angel One, so a MARKET request is turned into a LIMIT
/// a protection percentage beyond the reference price (above it for a BUY, below for a SELL), rounded to the tick.
/// </summary>
public static class MarketPriceProtection
{
    /// <summary>The instrument type the slabs are chosen by: the master contract's, else read from the symbol.</summary>
    public static string InstrumentType(string? masterType, string symbol)
    {
        if (!string.IsNullOrEmpty(masterType)) return masterType;
        var upper = symbol.ToUpperInvariant();
        if (upper.EndsWith("CE", StringComparison.Ordinal)) return "CE";
        if (upper.EndsWith("PE", StringComparison.Ordinal)) return "PE";
        return upper.EndsWith("FUT", StringComparison.Ordinal) ? "FUT" : "EQ";
    }

    public static double Percentage(double price, string instrumentType) => instrumentType is "CE" or "PE"
        ? price < 10 ? 5.0 : price < 100 ? 3.0 : price < 500 ? 2.0 : 1.0
        : price < 100 ? 2.0 : price < 500 ? 1.0 : 0.5;

    /// <summary>calculate_protected_price: the reference price moved by the slab percentage, then rounded to the tick (two places when the tick is unknown).</summary>
    public static double ProtectedPrice(double price, string action, string instrumentType, double? tickSize)
    {
        var multiplier = Percentage(price, instrumentType) / 100;
        var moved = string.Equals(action, "BUY", StringComparison.OrdinalIgnoreCase) ? price * (1 + multiplier) : price * (1 - multiplier);
        if (tickSize is not { } tick || tick <= 0) return BookFormatting.RoundPy(moved, 2);
        return BookFormatting.RoundPy(RoundHalfEven(moved / tick) * tick, 2);
    }

    /// <summary>Python's round(x) with no digits: half to even.</summary>
    private static double RoundHalfEven(double value) => Math.Round(value, MidpointRounding.ToEven);
}
