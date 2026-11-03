namespace EPTradeBot.Core.Sandbox;

/// <summary>Leverage settings, defaults as OpenAlgo's sandbox_config ships them.</summary>
public sealed record SandboxLeverage
{
    public decimal EquityMis { get; init; } = 5m;
    public decimal EquityCnc { get; init; } = 1m;
    public decimal Futures { get; init; } = 10m;
    public decimal OptionBuy { get; init; } = 1m;
    public decimal OptionSell { get; init; } = 1m;
}

/// <summary>Port of OpenAlgo's sandbox margin and fill rules. Pure.</summary>
public static class SandboxRules
{
    private static readonly HashSet<string> Derivatives = new(StringComparer.Ordinal) { "NFO", "BFO", "MCX", "CDS", "BCD", "NCDEX", "NCO" };

    public static bool IsFuture(string symbol, string exchange) => Derivatives.Contains(exchange) && symbol.EndsWith("FUT", StringComparison.Ordinal);

    public static bool IsOption(string symbol, string exchange) =>
        Derivatives.Contains(exchange) && (symbol.EndsWith("CE", StringComparison.Ordinal) || symbol.EndsWith("PE", StringComparison.Ordinal));

    public static decimal Leverage(SandboxLeverage l, string exchange, string product, string symbol, bool isBuy)
    {
        if (exchange is "NSE" or "BSE") return product == "MIS" ? l.EquityMis : l.EquityCnc;
        if (IsFuture(symbol, exchange)) return l.Futures;
        if (IsOption(symbol, exchange)) return isBuy ? l.OptionBuy : l.OptionSell;
        return 1m;
    }

    public static decimal Margin(SandboxLeverage l, string exchange, string product, string symbol, bool isBuy, int quantity, decimal price)
    {
        var leverage = Leverage(l, exchange, product, symbol, isBuy);
        if (leverage <= 0) leverage = 1m;
        return decimal.Round(Math.Abs(quantity) * price / leverage, 2, MidpointRounding.AwayFromZero);
    }

    /// <summary>
    /// The part of an order that opens or adds exposure. A closing order needs no
    /// margin, so a square-off can never be refused for lack of funds.
    /// </summary>
    public static int OpeningQuantity(int netQuantity, int sign, int quantity)
    {
        if (netQuantity == 0 || Math.Sign(netQuantity) == sign) return quantity;
        return Math.Max(0, quantity - Math.Abs(netQuantity));
    }

    /// <summary>
    /// The price an open order fills at for this quote, or null when it does not fill yet.
    /// MARKET takes the ask (buy) or bid (sell), else LTP; LIMIT fills at its limit when crossed;
    /// SL fills at LTP once triggered and inside the limit; SL-M fills at LTP once triggered.
    /// </summary>
    public static decimal? FillPrice(string priceType, bool isBuy, decimal limitPrice, decimal triggerPrice, decimal? ltp, decimal? bid, decimal? ask)
    {
        if (ltp is not { } p || p <= 0) return null;
        return priceType switch
        {
            "MARKET" => isBuy ? (ask is > 0 ? ask : p) : (bid is > 0 ? bid : p),
            "LIMIT" => isBuy ? (p <= limitPrice ? limitPrice : null) : (p >= limitPrice ? limitPrice : null),
            "SL" => isBuy ? (p >= triggerPrice && p <= limitPrice ? p : null) : (p <= triggerPrice && p >= limitPrice ? p : null),
            "SL-M" => isBuy ? (p >= triggerPrice ? p : null) : (p <= triggerPrice ? p : null),
            _ => null,
        };
    }

    /// <summary>The result of applying one fill to one position.</summary>
    public sealed record FillOutcome(int NetQuantity, decimal AveragePrice, decimal RealizedPnl, decimal MarginReleased, decimal MarginRemaining);

    /// <summary>
    /// Applies a fill of <paramref name="quantity"/> at <paramref name="price"/> to a position holding
    /// <paramref name="netQuantity"/> at <paramref name="averagePrice"/> with <paramref name="marginBlocked"/>.
    /// <paramref name="openingMargin"/> is the margin charged for the opening part of this fill.
    /// Closing quantity realises P&amp;L against the average and releases margin pro rata; a fill
    /// larger than the position flips it, the remainder opening at the fill price.
    /// </summary>
    public static FillOutcome ApplyFill(int netQuantity, decimal averagePrice, decimal marginBlocked, int sign, int quantity, decimal price, decimal openingMargin)
    {
        if (netQuantity == 0 || Math.Sign(netQuantity) == sign)
        {
            var held = Math.Abs(netQuantity);
            var avg = held + quantity == 0 ? 0 : (held * averagePrice + quantity * price) / (held + quantity);
            return new FillOutcome(netQuantity + sign * quantity, decimal.Round(avg, 4), 0m, 0m, marginBlocked + openingMargin);
        }

        var closing = Math.Min(quantity, Math.Abs(netQuantity));
        var realized = closing * (price - averagePrice) * Math.Sign(netQuantity);
        var released = Math.Abs(netQuantity) == 0 ? 0 : decimal.Round(marginBlocked * closing / Math.Abs(netQuantity), 2);
        var remainingMargin = marginBlocked - released;
        var after = netQuantity + sign * quantity;
        if (after == 0) return new FillOutcome(0, 0m, realized, marginBlocked, 0m);
        if (Math.Sign(after) == Math.Sign(netQuantity)) return new FillOutcome(after, averagePrice, realized, released, remainingMargin);

        // Flipped: the old side is fully closed, the remainder opens at the fill price.
        return new FillOutcome(after, price, realized, marginBlocked, openingMargin);
    }
}
