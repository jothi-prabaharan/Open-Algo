// Generated from OpenAlgo table 'whatsapp_users' (database.whatsapp_db). Maintained by hand from here on.
namespace EPTradeBot.Core.Entities.Legacy.Core;

/// <summary>Port of OpenAlgo table <c>whatsapp_users</c>.</summary>
public sealed class WhatsappUser
{
    public int Id { get; set; }
    public string WhatsappJid { get; set; } = "";
    public string PhoneNumber { get; set; } = "";
    public string OpenalgoUsername { get; set; } = "";
    public string? EncryptedApiKey { get; set; }
    public string? HostUrl { get; set; }
    public string? DisplayName { get; set; }
    public string? Broker { get; set; } = "default";
    public bool? IsActive { get; set; } = true;
    public bool? NotificationsEnabled { get; set; } = true;
    public DateTime? CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? UpdatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? LastCommandAt { get; set; }
}
