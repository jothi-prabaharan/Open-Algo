// Generated from OpenAlgo table 'sm_strategy_order' (database.strategy_module_db). Maintained by hand from here on.
namespace EPTradeBot.Core.Entities.Legacy.Core;

/// <summary>Port of OpenAlgo table <c>sm_strategy_order</c>.</summary>
public sealed class SmStrategyOrder
{
    public int Id { get; set; }
    public int RunId { get; set; }
    public int LegId { get; set; }
    public string Kind { get; set; } = "";
    public string? PositionRef { get; set; }
    public string? BrokerOrderId { get; set; }
    public string Symbol { get; set; } = "";
    public string Exchange { get; set; } = "";
    public string Action { get; set; } = "";
    public int Qty { get; set; }
    public string? Product { get; set; }
    public string Pricetype { get; set; } = "MARKET";
    public decimal Price { get; set; } = 0m;
    public decimal TriggerPrice { get; set; } = 0m;
    public string Status { get; set; } = "pending";
    public DateTime PlacedAt { get; set; } = DateTime.UtcNow;
    public DateTime? FilledAt { get; set; }
    public decimal? AvgFillPrice { get; set; }
    public int? FilledQty { get; set; }
    public string? RejectReason { get; set; }
}
