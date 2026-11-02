// Generated from OpenAlgo table 'active_sessions' (database.auth_db). Maintained by hand from here on.
namespace EPTradeBot.Core.Entities.Legacy.Core;

/// <summary>Port of OpenAlgo table <c>active_sessions</c>.</summary>
public sealed class ActiveSession
{
    public int Id { get; set; }
    public string Username { get; set; } = "";
    public string SessionId { get; set; } = "";
    public string? DeviceInfo { get; set; }
    public string? IpAddress { get; set; }
    public string? Broker { get; set; }
    public DateTime? LoginTime { get; set; } = DateTime.UtcNow;
    public DateTime? LastSeen { get; set; } = DateTime.UtcNow;
}
