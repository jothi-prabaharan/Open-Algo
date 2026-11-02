// Generated from OpenAlgo table 'traffic_logs' (database.traffic_db). Maintained by hand from here on.
namespace EPTradeBot.Core.Entities.Legacy.Audit;

/// <summary>Port of OpenAlgo table <c>traffic_logs</c>.</summary>
public sealed class TrafficLog
{
    public int Id { get; set; }
    public DateTime? Timestamp { get; set; } = DateTime.UtcNow;
    public string ClientIp { get; set; } = "";
    public string Method { get; set; } = "";
    public string Path { get; set; } = "";
    public int StatusCode { get; set; }
    public double DurationMs { get; set; }
    public string? Host { get; set; }
    public string? Error { get; set; }
    public int? UserId { get; set; }
}
