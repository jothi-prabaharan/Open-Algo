// Generated from OpenAlgo table 'telegram_users' (database.telegram_db). Maintained by hand from here on.
namespace EPTradeBot.Core.Entities.Legacy.Core;

/// <summary>Port of OpenAlgo table <c>telegram_users</c>.</summary>
public sealed class TelegramUser
{
    public int Id { get; set; }
    public int TelegramId { get; set; }
    public string OpenalgoUsername { get; set; } = "";
    public string? EncryptedApiKey { get; set; }
    public string? HostUrl { get; set; }
    public string? FirstName { get; set; }
    public string? LastName { get; set; }
    public string? TelegramUsername { get; set; }
    public string? Broker { get; set; } = "default";
    public bool? IsActive { get; set; } = true;
    public bool? NotificationsEnabled { get; set; } = true;
    public DateTime? CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? UpdatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? LastCommandAt { get; set; }
}
