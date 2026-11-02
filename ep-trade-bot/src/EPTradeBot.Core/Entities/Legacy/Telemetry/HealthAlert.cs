// Generated from OpenAlgo table 'health_alerts' (database.health_db). Maintained by hand from here on.
namespace EPTradeBot.Core.Entities.Legacy.Telemetry;

/// <summary>Port of OpenAlgo table <c>health_alerts</c>.</summary>
public sealed class HealthAlert
{
    public int Id { get; set; }
    public DateTime? Timestamp { get; set; } = DateTime.UtcNow;
    public string? AlertType { get; set; }
    public string? Severity { get; set; }
    public string? MetricName { get; set; }
    public double? MetricValue { get; set; }
    public double? ThresholdValue { get; set; }
    public string? Message { get; set; }
    public bool? Acknowledged { get; set; } = false;
    public DateTime? AcknowledgedAt { get; set; }
    public bool? Resolved { get; set; } = false;
    public DateTime? ResolvedAt { get; set; }
}
