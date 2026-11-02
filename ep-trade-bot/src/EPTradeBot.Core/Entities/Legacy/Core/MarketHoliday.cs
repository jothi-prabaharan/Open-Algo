// Generated from OpenAlgo table 'market_holidays' (database.market_calendar_db). Maintained by hand from here on.
namespace EPTradeBot.Core.Entities.Legacy.Core;

/// <summary>Port of OpenAlgo table <c>market_holidays</c>.</summary>
public sealed class MarketHoliday
{
    public int Id { get; set; }
    public DateOnly HolidayDate { get; set; }
    public string Description { get; set; } = "";
    public string HolidayType { get; set; } = "TRADING_HOLIDAY";
    public int Year { get; set; }
}
