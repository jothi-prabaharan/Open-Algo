// Generated from OpenAlgo table 'session_revocations' (database.auth_db). Maintained by hand from here on.
namespace EPTradeBot.Core.Entities.Legacy.Core;

/// <summary>Port of OpenAlgo table <c>session_revocations</c>.</summary>
public sealed class SessionRevocation
{
    public string Username { get; set; } = "";
    public DateTime RevokedAt { get; set; }
    public DateTime? PreBrokerRevokedAt { get; set; }
}
