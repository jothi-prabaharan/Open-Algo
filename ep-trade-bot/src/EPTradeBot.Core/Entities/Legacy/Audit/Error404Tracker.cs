// Generated from OpenAlgo table 'error_404_tracker' (database.traffic_db). Maintained by hand from here on.
namespace EPTradeBot.Core.Entities.Legacy.Audit;

/// <summary>Port of OpenAlgo table <c>error_404_tracker</c>.</summary>
public sealed class Error404Tracker
{
    public int Id { get; set; }
    public string IpAddress { get; set; } = "";
    public int? ErrorCount { get; set; } = 1;
    public DateTime? FirstErrorAt { get; set; } = DateTime.UtcNow;
    public DateTime? LastErrorAt { get; set; } = DateTime.UtcNow;
    public string? PathsAttempted { get; set; }
}
