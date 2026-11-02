// Generated from OpenAlgo table 'bot_config' (database.telegram_db). Maintained by hand from here on.
namespace EPTradeBot.Core.Entities.Legacy.Core;

/// <summary>Port of OpenAlgo table <c>bot_config</c>.</summary>
public sealed class TelegramBotConfig
{
    public int Id { get; set; } = 1;
    public string? Token { get; set; }
    public bool? IsActive { get; set; } = false;
    public string? BotUsername { get; set; }
    public int? MaxMessageLength { get; set; } = 4096;
    public int? RateLimitPerMinute { get; set; } = 30;
    public bool? BroadcastEnabled { get; set; } = true;
    public DateTime? CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? UpdatedAt { get; set; } = DateTime.UtcNow;
}
