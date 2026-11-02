// Generated from OpenAlgo table 'market_timings' (database.market_calendar_db). Maintained by hand from here on.
namespace EPTradeBot.Core.Entities.Legacy.Core;

/// <summary>Port of OpenAlgo table <c>market_timings</c>.</summary>
public sealed class MarketTiming
{
    public int Id { get; set; }
    public string ExchangeCode { get; set; } = "";
    public string StartTime { get; set; } = "";
    public string EndTime { get; set; } = "";
    public long StartOffset { get; set; }
    public long EndOffset { get; set; }
}
