// Generated from OpenAlgo table 'sandbox_daily_pnl' (database.sandbox_db). Maintained by hand from here on.
namespace EPTradeBot.Core.Entities.Legacy.Sandbox;

/// <summary>Port of OpenAlgo table <c>sandbox_daily_pnl</c>.</summary>
public sealed class SandboxDailyPnl
{
    public int Id { get; set; }
    public string UserId { get; set; } = "";
    public DateOnly Date { get; set; }
    public decimal? RealizedPnl { get; set; } = 0.0m;
    public decimal? PositionsUnrealizedPnl { get; set; } = 0.0m;
    public decimal? HoldingsUnrealizedPnl { get; set; } = 0.0m;
    public decimal? TotalMtm { get; set; } = 0.0m;
    public decimal? AvailableBalance { get; set; } = 0.0m;
    public decimal? UsedMargin { get; set; } = 0.0m;
    public decimal? PortfolioValue { get; set; } = 0.0m;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
