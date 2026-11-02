// Generated from OpenAlgo table 'auth' (database.auth_db). Maintained by hand from here on.
namespace EPTradeBot.Core.Entities.Legacy.Core;

/// <summary>Port of OpenAlgo table <c>auth</c>.</summary>
public sealed class BrokerAuth
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public string Auth { get; set; } = "";
    public string? FeedToken { get; set; }
    public string Broker { get; set; } = "";
    public string? UserId { get; set; }
    public bool? IsRevoked { get; set; } = false;
    public string? SecretApiKey { get; set; }
    public string? PrimaryIp { get; set; }
    public string? SecondaryIp { get; set; }
    public DateTime? IpUpdatedAt { get; set; }
    public string? AuxParam1 { get; set; }
    public string? AuxParam2 { get; set; }
    public string? AuxParam3 { get; set; }
    public string? AuxParam4 { get; set; }
}
