// Generated from OpenAlgo table 'notification_queue' (database.telegram_db). Maintained by hand from here on.
namespace EPTradeBot.Core.Entities.Legacy.Core;

/// <summary>Port of OpenAlgo table <c>notification_queue</c>.</summary>
public sealed class TelegramNotification
{
    public int Id { get; set; }
    public int TelegramId { get; set; }
    public string Message { get; set; } = "";
    public int? Priority { get; set; } = 5;
    public string? Status { get; set; } = "pending";
    public DateTime? CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? SentAt { get; set; }
    public string? ErrorMessage { get; set; }
}
