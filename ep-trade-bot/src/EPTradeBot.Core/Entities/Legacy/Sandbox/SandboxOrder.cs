// Generated from OpenAlgo table 'sandbox_orders' (database.sandbox_db). Maintained by hand from here on.
namespace EPTradeBot.Core.Entities.Legacy.Sandbox;

/// <summary>Port of OpenAlgo table <c>sandbox_orders</c>.</summary>
public sealed class SandboxOrder
{
    public int Id { get; set; }
    public string Orderid { get; set; } = "";
    public string UserId { get; set; } = "";
    public string? Strategy { get; set; }
    public string Symbol { get; set; } = "";
    public string Exchange { get; set; } = "";
    public string Action { get; set; } = "";
    public int Quantity { get; set; }
    public decimal? Price { get; set; }
    public decimal? TriggerPrice { get; set; }
    public string PriceType { get; set; } = "";
    public string Product { get; set; } = "";
    public string OrderStatus { get; set; } = "open";
    public decimal? AveragePrice { get; set; }
    public int? FilledQuantity { get; set; } = 0;
    public int PendingQuantity { get; set; }
    public string? RejectionReason { get; set; }
    public decimal? MarginBlocked { get; set; } = 0.0m;
    public int? GttLegId { get; set; }
    public DateTime OrderTimestamp { get; set; } = DateTime.UtcNow;
    public DateTime UpdateTimestamp { get; set; } = DateTime.UtcNow;
}
