// Generated from OpenAlgo table 'sandbox_holdings' (database.sandbox_db). Maintained by hand from here on.
namespace EPTradeBot.Core.Entities.Legacy.Sandbox;

/// <summary>Port of OpenAlgo table <c>sandbox_holdings</c>.</summary>
public sealed class SandboxHolding
{
    public int Id { get; set; }
    public string UserId { get; set; } = "";
    public string Symbol { get; set; } = "";
    public string Exchange { get; set; } = "";
    public int Quantity { get; set; }
    public decimal AveragePrice { get; set; }
    public decimal? Ltp { get; set; }
    public decimal? Pnl { get; set; } = 0.0m;
    public decimal? PnlPercent { get; set; } = 0.0m;
    public DateOnly SettlementDate { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
