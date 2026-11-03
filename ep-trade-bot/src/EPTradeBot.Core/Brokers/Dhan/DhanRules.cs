using System.Globalization;

namespace EPTradeBot.Core.Brokers.Dhan;

/// <summary>Dhan's vocabulary, and the protective price Dhan needs for a stop-loss-market order. Pure.</summary>
public static class DhanRules
{
    public static string? Segment(string exchange) => exchange switch
    {
        "NSE" => "NSE_EQ", "BSE" => "BSE_EQ", "CDS" => "NSE_CURRENCY", "NFO" => "NSE_FNO", "BFO" => "BSE_FNO",
        "BCD" => "BSE_CURRENCY", "MCX" => "MCX_COMM", "NCO" => "NSE_COMM", _ => null,
    };

    /// <summary>The segment for market data, which also covers indices.</summary>
    public static string? DataSegment(string exchange) => exchange is "NSE_INDEX" or "BSE_INDEX" ? "IDX_I" : Segment(exchange);

    /// <summary>map_exchange: an unmapped segment stays visible as itself rather than becoming null.</summary>
    public static string Exchange(string segment) => segment switch
    {
        "NSE_EQ" => "NSE", "BSE_EQ" => "BSE", "NSE_CURRENCY" => "CDS", "NSE_FNO" => "NFO", "BSE_FNO" => "BFO",
        "BSE_CURRENCY" => "BCD", "MCX_COMM" => "MCX", "NSE_COMM" => "NCO", _ => segment,
    };

    /// <summary>map_product_type. MARGIN (an equity margin position read back from Dhan) goes back as itself.</summary>
    public static string Product(string product) => product switch { "CNC" => "CNC", "NRML" or "MARGIN" => "MARGIN", _ => "INTRADAY" };

    public static string OrderType(string priceType) => priceType switch
    {
        "LIMIT" => "LIMIT", "SL" => "STOP_LOSS", "SL-M" => "STOP_LOSS_MARKET", _ => "MARKET",
    };

    public static string PriceType(string orderType) => orderType switch
    {
        "STOP_LOSS" => "SL", "STOP_LOSS_MARKET" => "SL-M", _ => orderType,
    };

    /// <summary>The book's product for a Dhan product, applied only when the symbol is known (as OpenAlgo does).</summary>
    public static string BookProduct(string exchange, string product) =>
        (exchange is "NSE" or "BSE") && product == "CNC" ? "CNC"
        : product == "INTRADAY" ? "MIS"
        : exchange is "NFO" or "MCX" or "BFO" or "CDS" or "BCD" or "NCO" && product == "MARGIN" ? "NRML"
        : product;

    /// <summary>utils/mpp_slab.py: market price protection percentage by price and instrument type.</summary>
    public static double MppPercent(double price, string symbol)
    {
        var upper = symbol.ToUpperInvariant();
        var option = upper.EndsWith("CE", StringComparison.Ordinal) || upper.EndsWith("PE", StringComparison.Ordinal);
        if (option) return price < 10 ? 5.0 : price < 100 ? 3.0 : price < 500 ? 2.0 : 1.0;
        return price < 100 ? 2.0 : price < 500 ? 1.0 : 0.5;
    }

    /// <summary>
    /// _slm_protected_price. Dhan does not rest a bare stop-loss-market order under SEBI's
    /// market price protection, so SL-M is sent as a stop-limit whose limit sits the MPP
    /// percentage beyond the trigger (below it for a SELL, above for a BUY), at least one
    /// tick away and snapped to the tick. Null when the tick size is unknown: a guessed
    /// tick risks a rejection, so the order is refused instead.
    /// </summary>
    public static double? ProtectedLimit(string symbol, string action, double trigger, double tickSize)
    {
        if (!double.IsFinite(tickSize) || tickSize <= 0) return null;
        var pct = MppPercent(trigger, symbol) / 100.0;
        if (string.Equals(action, "SELL", StringComparison.OrdinalIgnoreCase))
        {
            var limit = Snap(Math.Min(trigger * (1 - pct), trigger - tickSize), tickSize, floor: true);
            return limit <= 0 ? null : limit;
        }

        return Snap(Math.Max(trigger * (1 + pct), trigger + tickSize), tickSize, floor: false);
    }

    private static double Snap(double value, double tick, bool floor)
    {
        var ratio = BookFormatting.RoundPy(value / tick, 6);
        var k = floor ? Math.Floor(ratio) : Math.Ceiling(ratio);
        return BookFormatting.RoundPy(k * tick, TickDecimals(tick));
    }

    /// <summary>Decimal places of a tick written the way Python's str(float) writes it (0.05 is 2, 0.0025 is 4).</summary>
    private static int TickDecimals(double tick)
    {
        var text = tick.ToString("R", CultureInfo.InvariantCulture);
        if (text.Contains('E', StringComparison.OrdinalIgnoreCase))
            return (int)Math.Max(0, -Math.Floor(Math.Log10(tick)));
        var dot = text.IndexOf('.');
        return dot < 0 ? 0 : text.Length - dot - 1;
    }

    /// <summary>The sandbox's chart instrument type (FUTIDX, OPTSTK, ...), for quotes derived from candles.</summary>
    public static string ChartInstrument(string exchange, string symbol)
    {
        string[] indices = ["NIFTY", "NIFTYNXT50", "FINNIFTY", "BANKNIFTY", "MIDCPNIFTY", "INDIAVIX", "SENSEX", "BANKEX", "SENSEX50"];
        var option = symbol.EndsWith("CE", StringComparison.Ordinal) || symbol.EndsWith("PE", StringComparison.Ordinal);
        return exchange switch
        {
            "NSE" or "BSE" => "EQUITY",
            "NSE_INDEX" or "BSE_INDEX" => "INDEX",
            "NFO" or "BFO" => option
                ? indices.Any(symbol.Contains) ? "OPTIDX" : "OPTSTK"
                : indices.Any(symbol.Contains) ? "FUTIDX" : "FUTSTK",
            "NCO" => option ? "OPTFUT" : "FUTCOM",
            "CDS" or "BCD" => option ? "OPTCUR" : "FUTCUR",
            _ => "EQUITY",
        };
    }
}
