// Generated from OpenAlgo table 'strategy_pending_fills' (database.strategy_book_db). Maintained by hand from here on.
namespace EPTradeBot.Core.Entities.Legacy.Core;

/// <summary>Port of OpenAlgo table <c>strategy_pending_fills</c>.</summary>
public sealed class StrategyPendingFill
{
    public int Id { get; set; }
    public string Orderid { get; set; } = "";
    public double FilledQuantity { get; set; }
    public double AveragePrice { get; set; }
    public string Action { get; set; } = "";
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
