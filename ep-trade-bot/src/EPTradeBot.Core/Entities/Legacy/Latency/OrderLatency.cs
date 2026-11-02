// Generated from OpenAlgo table 'order_latency' (database.latency_db). Maintained by hand from here on.
namespace EPTradeBot.Core.Entities.Legacy.Latency;

/// <summary>Port of OpenAlgo table <c>order_latency</c>.</summary>
public sealed class OrderLatency
{
    public int Id { get; set; }
    public DateTime? Timestamp { get; set; } = DateTime.UtcNow;
    public string OrderId { get; set; } = "";
    public int? UserId { get; set; }
    public string? Broker { get; set; }
    public string? Symbol { get; set; }
    public string? OrderType { get; set; }
    public double? RttMs { get; set; }
    public double? ValidationLatencyMs { get; set; }
    public double? ResponseLatencyMs { get; set; }
    public double? OverheadMs { get; set; }
    public double TotalLatencyMs { get; set; }
    public string? RequestBody { get; set; }
    public string? ResponseBody { get; set; }
    public string? Status { get; set; }
    public string? Error { get; set; }
}
