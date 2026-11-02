// Generated from OpenAlgo table 'api_keys' (database.auth_db). Maintained by hand from here on.
namespace EPTradeBot.Core.Entities.Legacy.Core;

/// <summary>Port of OpenAlgo table <c>api_keys</c>.</summary>
public sealed class ApiKey
{
    public int Id { get; set; }
    public string UserId { get; set; } = "";
    public string ApiKeyHash { get; set; } = "";
    public string ApiKeyEncrypted { get; set; } = "";
    public DateTime? CreatedAt { get; set; } = DateTime.UtcNow;
    public string? OrderMode { get; set; } = "auto";
}
