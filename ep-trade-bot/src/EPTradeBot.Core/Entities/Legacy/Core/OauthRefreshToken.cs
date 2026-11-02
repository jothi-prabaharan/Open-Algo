// Generated from OpenAlgo table 'oauth_refresh_tokens' (database.oauth_db). Maintained by hand from here on.
namespace EPTradeBot.Core.Entities.Legacy.Core;

/// <summary>Port of OpenAlgo table <c>oauth_refresh_tokens</c>.</summary>
public sealed class OauthRefreshToken
{
    public int Id { get; set; }
    public string ClientId { get; set; } = "";
    public string TokenHash { get; set; } = "";
    public string Scopes { get; set; } = "";
    public string FamilyId { get; set; } = "";
    public int? ParentId { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime ExpiresAt { get; set; }
    public DateTime? LastUsedAt { get; set; }
    public DateTime? RevokedAt { get; set; }
    public string? RevokeReason { get; set; }
}
