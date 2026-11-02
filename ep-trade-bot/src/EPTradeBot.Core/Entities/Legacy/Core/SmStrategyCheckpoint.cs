// Generated from OpenAlgo table 'sm_strategy_checkpoint' (database.strategy_module_db). Maintained by hand from here on.
namespace EPTradeBot.Core.Entities.Legacy.Core;

/// <summary>Port of OpenAlgo table <c>sm_strategy_checkpoint</c>.</summary>
public sealed class SmStrategyCheckpoint
{
    public int Id { get; set; }
    public int RunId { get; set; }
    public DateTime Ts { get; set; } = DateTime.UtcNow;
    public decimal PnlRealized { get; set; } = 0m;
    public decimal PnlUnrealized { get; set; } = 0m;
    public decimal PnlTotal { get; set; } = 0m;
    public decimal PnlPeak { get; set; } = 0m;
    public decimal PnlTrough { get; set; } = 0m;
    public decimal? LockFloor { get; set; }
    public bool TrailToEntryActive { get; set; } = false;
    public string LegState { get; set; } = "{}";
}
