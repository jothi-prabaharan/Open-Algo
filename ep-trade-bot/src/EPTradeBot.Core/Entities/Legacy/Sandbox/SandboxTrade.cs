// Generated from OpenAlgo table 'sandbox_trades' (database.sandbox_db). Maintained by hand from here on.
namespace EPTradeBot.Core.Entities.Legacy.Sandbox;

/// <summary>Port of OpenAlgo table <c>sandbox_trades</c>.</summary>
public sealed class SandboxTrade
{
    public int Id { get; set; }
    public string Tradeid { get; set; } = "";
    public string Orderid { get; set; } = "";
    public string UserId { get; set; } = "";
    public string Symbol { get; set; } = "";
    public string Exchange { get; set; } = "";
    public string Action { get; set; } = "";
    public int Quantity { get; set; }
    public decimal Price { get; set; }
    public string Product { get; set; } = "";
    public string? Strategy { get; set; }
    public DateTime TradeTimestamp { get; set; } = DateTime.UtcNow;
}
