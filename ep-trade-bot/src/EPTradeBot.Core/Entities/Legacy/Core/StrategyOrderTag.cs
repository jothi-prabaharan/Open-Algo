// Generated from OpenAlgo table 'strategy_order_tags' (database.strategy_book_db). Maintained by hand from here on.
namespace EPTradeBot.Core.Entities.Legacy.Core;

/// <summary>Port of OpenAlgo table <c>strategy_order_tags</c>.</summary>
public sealed class StrategyOrderTag
{
    public int Id { get; set; }
    public string Orderid { get; set; } = "";
    public string UserId { get; set; } = "";
    public string Strategy { get; set; } = "";
    public string Symbol { get; set; } = "";
    public string Exchange { get; set; } = "";
    public string Product { get; set; } = "";
    public double AppliedQuantity { get; set; } = 0.0;
    public double AppliedNotional { get; set; } = 0.0;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
