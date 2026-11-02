// Generated from OpenAlgo table 'command_logs' (database.telegram_db). Maintained by hand from here on.
namespace EPTradeBot.Core.Entities.Legacy.Audit;

/// <summary>Port of OpenAlgo table <c>command_logs</c>.</summary>
public sealed class TelegramCommandLog
{
    public int Id { get; set; }
    public int TelegramId { get; set; }
    public string Command { get; set; } = "";
    public int? ChatId { get; set; }
    public string? Parameters { get; set; }
    public DateTime? ExecutedAt { get; set; } = DateTime.UtcNow;
}
