// Generated from OpenAlgo table 'whatsapp_notification_queue' (database.whatsapp_db). Maintained by hand from here on.
namespace EPTradeBot.Core.Entities.Legacy.Core;

/// <summary>Port of OpenAlgo table <c>whatsapp_notification_queue</c>.</summary>
public sealed class WhatsappNotificationQueue
{
    public int Id { get; set; }
    public string WhatsappJid { get; set; } = "";
    public string Message { get; set; } = "";
    public string? MediaPath { get; set; }
    public string? MediaKind { get; set; }
    public int? Priority { get; set; } = 5;
    public string? Status { get; set; } = "pending";
    public DateTime? CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? SentAt { get; set; }
    public string? ErrorMessage { get; set; }
}
