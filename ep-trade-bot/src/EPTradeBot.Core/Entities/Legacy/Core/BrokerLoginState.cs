// Generated from OpenAlgo table 'broker_login_states' (database.broker_keys_db). Maintained by hand from here on.
namespace EPTradeBot.Core.Entities.Legacy.Core;

/// <summary>Port of OpenAlgo table <c>broker_login_states</c>.</summary>
public sealed class BrokerLoginState
{
    public int Id { get; set; }
    public string NonceHash { get; set; } = "";
    public string StateHash { get; set; } = "";
    public string Broker { get; set; } = "";
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime ExpiresAt { get; set; }
}
