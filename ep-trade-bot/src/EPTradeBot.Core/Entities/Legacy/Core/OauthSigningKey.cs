// Generated from OpenAlgo table 'oauth_signing_keys' (database.oauth_db). Maintained by hand from here on.
namespace EPTradeBot.Core.Entities.Legacy.Core;

/// <summary>Port of OpenAlgo table <c>oauth_signing_keys</c>.</summary>
public sealed class OauthSigningKey
{
    public int Id { get; set; }
    public string Kid { get; set; } = "";
    public string Algorithm { get; set; } = "RS256";
    public string PublicJwk { get; set; } = "";
    public string PrivatePath { get; set; } = "";
    public bool IsActive { get; set; } = true;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? RotatedAt { get; set; }
}
