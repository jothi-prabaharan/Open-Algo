// Generated from OpenAlgo table 'sandbox_positions' (database.sandbox_db). Maintained by hand from here on.
namespace EPTradeBot.Core.Entities.Legacy.Sandbox;

/// <summary>Port of OpenAlgo table <c>sandbox_positions</c>.</summary>
public sealed class SandboxPosition
{
    public int Id { get; set; }
    public string UserId { get; set; } = "";
    public string Symbol { get; set; } = "";
    public string Exchange { get; set; } = "";
    public string Product { get; set; } = "";
    public int Quantity { get; set; }
    public decimal AveragePrice { get; set; }
    public decimal? Ltp { get; set; }
    public decimal? Pnl { get; set; } = 0.0m;
    public decimal? PnlPercent { get; set; } = 0.0m;
    public decimal? AccumulatedRealizedPnl { get; set; } = 0.0m;
    public decimal? TodayRealizedPnl { get; set; } = 0.0m;
    public decimal? MarginBlocked { get; set; } = 0.0m;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>Optimistic concurrency token (PostgreSQL <c>xmin</c>). Added in the port: two writers can no longer both spend the same margin.</summary>
    public uint Version { get; set; }
}
