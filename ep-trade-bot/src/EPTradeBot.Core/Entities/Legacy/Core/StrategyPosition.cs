// Generated from OpenAlgo table 'strategy_positions' (database.strategy_book_db). Maintained by hand from here on.
namespace EPTradeBot.Core.Entities.Legacy.Core;

/// <summary>Port of OpenAlgo table <c>strategy_positions</c>.</summary>
public sealed class StrategyPosition
{
    public int Id { get; set; }
    public string UserId { get; set; } = "";
    public string Strategy { get; set; } = "";
    public string Symbol { get; set; } = "";
    public string Exchange { get; set; } = "";
    public string Product { get; set; } = "";
    public double Quantity { get; set; } = 0.0;
    public double AveragePrice { get; set; } = 0.0;
    public double RealizedPnl { get; set; } = 0.0;
    public double TodayRealizedPnl { get; set; } = 0.0;
    public string? TradeDate { get; set; }
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
