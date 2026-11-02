// Generated from OpenAlgo table 'sm_strategy_event' (database.strategy_module_db). Maintained by hand from here on.
namespace EPTradeBot.Core.Entities.Legacy.Core;

/// <summary>Port of OpenAlgo table <c>sm_strategy_event</c>.</summary>
public sealed class SmStrategyEvent
{
    public int Id { get; set; }
    public int? RunId { get; set; }
    public int StrategyId { get; set; }
    public string UserId { get; set; } = "";
    public DateTime Ts { get; set; } = DateTime.UtcNow;
    public string Kind { get; set; } = "";
    public string Severity { get; set; } = "info";
    public int? LegId { get; set; }
    public string Message { get; set; } = "";
    public string? Payload { get; set; }
}
