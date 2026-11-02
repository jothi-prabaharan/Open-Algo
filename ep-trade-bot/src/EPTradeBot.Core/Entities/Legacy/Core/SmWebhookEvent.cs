// Generated from OpenAlgo table 'sm_webhook_event' (database.strategy_module_db). Maintained by hand from here on.
namespace EPTradeBot.Core.Entities.Legacy.Core;

/// <summary>Port of OpenAlgo table <c>sm_webhook_event</c>.</summary>
public sealed class SmWebhookEvent
{
    public int Id { get; set; }
    public int? StrategyId { get; set; }
    public string? Action { get; set; }
    public string? Mode { get; set; }
    public string? Payload { get; set; }
    public string? Ip { get; set; }
    public string? UserAgent { get; set; }
    public DateTime ReceivedAt { get; set; } = DateTime.UtcNow;
    public string Result { get; set; } = "";
    public string? Error { get; set; }
}
