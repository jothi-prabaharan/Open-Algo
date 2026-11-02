// Generated from OpenAlgo table 'sm_strategy_run' (database.strategy_module_db). Maintained by hand from here on.
namespace EPTradeBot.Core.Entities.Legacy.Core;

/// <summary>Port of OpenAlgo table <c>sm_strategy_run</c>.</summary>
public sealed class SmStrategyRun
{
    public int Id { get; set; }
    public int StrategyId { get; set; }
    public string Mode { get; set; } = "";
    public string Broker { get; set; } = "";
    public string? BrokerAccount { get; set; }
    public DateTime StartedAt { get; set; } = DateTime.UtcNow;
    public DateTime? StoppedAt { get; set; }
    public string? StopReason { get; set; }
    public DateTime? StopRequestedAt { get; set; }
    public string? StopRequestedReason { get; set; }
    public decimal PnlRealized { get; set; } = 0m;
    public decimal PnlPeak { get; set; } = 0m;
    public decimal PnlTrough { get; set; } = 0m;
    public string TriggerSource { get; set; } = "manual";
    public int? WebhookEventId { get; set; }
    public string? ResolvedExpiries { get; set; }
}
