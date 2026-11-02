// Generated from OpenAlgo table 'oauth_clients' (database.oauth_db). Maintained by hand from here on.
namespace EPTradeBot.Core.Entities.Legacy.Core;

/// <summary>Port of OpenAlgo table <c>oauth_clients</c>.</summary>
public sealed class OauthClient
{
    public int Id { get; set; }
    public string ClientId { get; set; } = "";
    public string ClientName { get; set; } = "";
    public string RedirectUris { get; set; } = "";
    public string? ClientSecretHash { get; set; }
    public string? ScopesRequested { get; set; } = "";
    public bool Approved { get; set; } = false;
    public DateTime? ApprovedAt { get; set; }
    public DateTime? RevokedAt { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? LastUsedAt { get; set; }
}
