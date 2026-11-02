namespace EPTradeBot.Core.Entities.MarketData;

/// <summary>
/// One OHLCV bar (replaces Historify's DuckDB <c>market_data</c>). Keyed on
/// symbol, exchange, interval and bar time; <see cref="Ts"/> carries a BRIN index.
/// </summary>
public sealed class Candle
{
    public string Symbol { get; set; } = "";
    public string Exchange { get; set; } = "";
    public string Interval { get; set; } = "";

    /// <summary>Bar open time, UTC.</summary>
    public DateTime Ts { get; set; }

    public double Open { get; set; }
    public double High { get; set; }
    public double Low { get; set; }
    public double Close { get; set; }
    public long Volume { get; set; }
    public long Oi { get; set; }
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
}
