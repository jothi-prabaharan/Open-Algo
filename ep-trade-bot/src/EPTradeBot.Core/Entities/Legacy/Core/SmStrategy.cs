// Generated from OpenAlgo table 'sm_strategy' (database.strategy_module_db). Maintained by hand from here on.
namespace EPTradeBot.Core.Entities.Legacy.Core;

/// <summary>Port of OpenAlgo table <c>sm_strategy</c>.</summary>
public sealed class SmStrategy
{
    public int Id { get; set; }
    public string UserId { get; set; } = "";
    public string Name { get; set; } = "";
    public string StrategyKind { get; set; } = "batch";
    public string Direction { get; set; } = "both";
    public string UniverseTab { get; set; } = "";
    public string Underlying { get; set; } = "";
    public string UnderlyingExchange { get; set; } = "";
    public string StrategyType { get; set; } = "intraday";
    public TimeOnly? EntryTime { get; set; }
    public TimeOnly? ExitTime { get; set; }
    public string Product { get; set; } = "NRML";
    public string Pricetype { get; set; } = "MARKET";
    public string Legs { get; set; } = "[]";
    public decimal? OverallSlMtm { get; set; }
    public decimal? OverallTargetMtm { get; set; }
    public string? LockProfit { get; set; }
    public bool TrailSlToEntry { get; set; } = false;
    public string? Scheduler { get; set; }
    public bool LiveEnabled { get; set; } = false;
    public string WebhookTokenHash { get; set; } = "";
    public string? WebhookIpAllowlist { get; set; }
    public bool WebhookLocked { get; set; } = false;
    public decimal? DailyLossLimitInr { get; set; }
    public string Status { get; set; } = "stopped";
    public int? CurrentRunId { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
