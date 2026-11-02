// Generated from OpenAlgo table 'login_attempts' (database.auth_db). Maintained by hand from here on.
namespace EPTradeBot.Core.Entities.Legacy.Audit;

/// <summary>Port of OpenAlgo table <c>login_attempts</c>.</summary>
public sealed class LoginAttempt
{
    public int Id { get; set; }
    public string Username { get; set; } = "";
    public string? IpAddress { get; set; }
    public string? DeviceInfo { get; set; }
    public string Status { get; set; } = "";
    public string? LoginType { get; set; }
    public string? Broker { get; set; }
    public string? FailureReason { get; set; }
    public DateTime? Timestamp { get; set; } = DateTime.UtcNow;
}
