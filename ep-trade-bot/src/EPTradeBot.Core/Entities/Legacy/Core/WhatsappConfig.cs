// Generated from OpenAlgo table 'whatsapp_config' (database.whatsapp_db). Maintained by hand from here on.
namespace EPTradeBot.Core.Entities.Legacy.Core;

/// <summary>Port of OpenAlgo table <c>whatsapp_config</c>.</summary>
public sealed class WhatsappConfig
{
    public int Id { get; set; } = 1;
    public byte[]? SessionBlob { get; set; }
    public string? OwnJid { get; set; }
    public string? OwnPhone { get; set; }
    public string? BotUsername { get; set; }
    public int? OwnerUserId { get; set; }
    public string? OwnerUsername { get; set; }
    public bool? IsPaired { get; set; } = false;
    public bool? IsActive { get; set; } = false;
    public DateTime? PairedAt { get; set; }
    public int? MaxMessageLength { get; set; } = 4096;
    public int? RateLimitPerMinute { get; set; } = 30;
    public bool? BroadcastEnabled { get; set; } = true;
    public DateTime? CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? UpdatedAt { get; set; } = DateTime.UtcNow;
}
