// Generated from OpenAlgo table 'broker_keys' (database.broker_keys_db). Maintained by hand from here on.
namespace EPTradeBot.Core.Entities.Legacy.Core;

/// <summary>Port of OpenAlgo table <c>broker_keys</c>.</summary>
public sealed class BrokerKey
{
    public int Id { get; set; }
    public string Broker { get; set; } = "";
    public string ApiKey { get; set; } = "";
    public string ApiSecret { get; set; } = "";
    public string ApiKeyMarket { get; set; } = "";
    public string ApiSecretMarket { get; set; } = "";
    public string RedirectUrl { get; set; } = "";
    public DateTime? CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? UpdatedAt { get; set; } = DateTime.UtcNow;
}
