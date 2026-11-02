// Generated from OpenAlgo table 'sandbox_funds' (database.sandbox_db). Maintained by hand from here on.
namespace EPTradeBot.Core.Entities.Legacy.Sandbox;

/// <summary>Port of OpenAlgo table <c>sandbox_funds</c>.</summary>
public sealed class SandboxFund
{
    public int Id { get; set; }
    public string UserId { get; set; } = "";
    public decimal? TotalCapital { get; set; } = 10000000.0m;
    public decimal? AvailableBalance { get; set; } = 10000000.0m;
    public decimal? UsedMargin { get; set; } = 0.0m;
    public decimal? RealizedPnl { get; set; } = 0.0m;
    public decimal? TodayRealizedPnl { get; set; } = 0.0m;
    public decimal? UnrealizedPnl { get; set; } = 0.0m;
    public decimal? TotalPnl { get; set; } = 0.0m;
    public DateTime LastResetDate { get; set; } = DateTime.UtcNow;
    public int? ResetCount { get; set; } = 0;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
