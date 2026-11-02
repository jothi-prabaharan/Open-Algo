// Generated from OpenAlgo table 'user_preferences' (database.telegram_db). Maintained by hand from here on.
namespace EPTradeBot.Core.Entities.Legacy.Core;

/// <summary>Port of OpenAlgo table <c>user_preferences</c>.</summary>
public sealed class TelegramUserPreference
{
    public int TelegramId { get; set; }
    public bool? OrderNotifications { get; set; } = true;
    public bool? TradeNotifications { get; set; } = true;
    public bool? PnlNotifications { get; set; } = true;
    public bool? DailySummary { get; set; } = true;
    public string? SummaryTime { get; set; } = "18:00";
    public string? Language { get; set; } = "en";
    public string? Timezone { get; set; } = "Asia/Kolkata";
    public DateTime? CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? UpdatedAt { get; set; } = DateTime.UtcNow;
}
