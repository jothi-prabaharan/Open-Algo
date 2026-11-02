// Generated from OpenAlgo table 'health_metrics' (database.health_db). Maintained by hand from here on.
namespace EPTradeBot.Core.Entities.Legacy.Telemetry;

/// <summary>Port of OpenAlgo table <c>health_metrics</c>.</summary>
public sealed class HealthMetric
{
    public int Id { get; set; }
    public DateTime? Timestamp { get; set; } = DateTime.UtcNow;
    public int? FdCount { get; set; }
    public int? FdLimit { get; set; }
    public double? FdUsagePercent { get; set; }
    public int? FdAvailable { get; set; }
    public string? FdStatus { get; set; }
    public double? MemoryRssMb { get; set; }
    public double? MemoryVmsMb { get; set; }
    public double? MemoryPercent { get; set; }
    public double? MemoryAvailableMb { get; set; }
    public double? MemorySwapMb { get; set; }
    public string? MemoryStatus { get; set; }
    public int? DbConnectionsTotal { get; set; }
    public string? DbConnections { get; set; }
    public string? DbStatus { get; set; }
    public int? WsConnectionsTotal { get; set; }
    public string? WsConnections { get; set; }
    public int? WsTotalSymbols { get; set; }
    public string? WsStatus { get; set; }
    public int? ThreadCount { get; set; }
    public int? StuckThreads { get; set; }
    public string? ThreadDetails { get; set; }
    public string? ThreadStatus { get; set; }
    public string? ProcessDetails { get; set; }
    public string? OverallStatus { get; set; }
}
