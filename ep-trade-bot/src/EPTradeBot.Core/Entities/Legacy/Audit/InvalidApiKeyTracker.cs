// Generated from OpenAlgo table 'invalid_api_key_tracker' (database.traffic_db). Maintained by hand from here on.
namespace EPTradeBot.Core.Entities.Legacy.Audit;

/// <summary>Port of OpenAlgo table <c>invalid_api_key_tracker</c>.</summary>
public sealed class InvalidApiKeyTracker
{
    public int Id { get; set; }
    public string IpAddress { get; set; } = "";
    public int? AttemptCount { get; set; } = 1;
    public DateTime? FirstAttemptAt { get; set; } = DateTime.UtcNow;
    public DateTime? LastAttemptAt { get; set; } = DateTime.UtcNow;
    public string? ApiKeysTried { get; set; }
}
