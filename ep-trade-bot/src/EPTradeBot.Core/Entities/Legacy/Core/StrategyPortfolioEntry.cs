// Generated from OpenAlgo table 'strategy_portfolio' (database.strategy_portfolio_db). Maintained by hand from here on.
namespace EPTradeBot.Core.Entities.Legacy.Core;

/// <summary>Port of OpenAlgo table <c>strategy_portfolio</c>.</summary>
public sealed class StrategyPortfolioEntry
{
    public int Id { get; set; }
    public string Watchlist { get; set; } = "";
    public string Name { get; set; } = "";
    public string Underlying { get; set; } = "";
    public string Exchange { get; set; } = "";
    public string? Expiry { get; set; }
    public string LegsJson { get; set; } = "[]";
    public string? Notes { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
