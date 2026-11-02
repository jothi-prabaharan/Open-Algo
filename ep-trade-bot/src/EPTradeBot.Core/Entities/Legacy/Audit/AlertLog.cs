// Generated from OpenAlgo table 'alert_log' (database.alert_log_db). Maintained by hand from here on.
namespace EPTradeBot.Core.Entities.Legacy.Audit;

/// <summary>Port of OpenAlgo table <c>alert_log</c>.</summary>
public sealed class AlertLog
{
    public int Id { get; set; }
    public string UserId { get; set; } = "";
    public string AlertId { get; set; } = "";
    public string Title { get; set; } = "";
    public string Kind { get; set; } = "price";
    public string Condition { get; set; } = "";
    public string Symbol { get; set; } = "";
    public string Exchange { get; set; } = "";
    public string Interval { get; set; } = "";
    public double? Price { get; set; }
    public string? Message { get; set; }
    public string Delivered { get; set; } = "";
    public DateTime FiredAt { get; set; } = DateTime.UtcNow;
}
