// Generated from OpenAlgo table 'market_holiday_exchanges' (database.market_calendar_db). Maintained by hand from here on.
namespace EPTradeBot.Core.Entities.Legacy.Core;

/// <summary>Port of OpenAlgo table <c>market_holiday_exchanges</c>.</summary>
public sealed class MarketHolidayExchange
{
    public int Id { get; set; }
    public int HolidayId { get; set; }
    public string ExchangeCode { get; set; } = "";
    public bool IsOpen { get; set; } = false;
    public long? StartTime { get; set; }
    public long? EndTime { get; set; }
}
