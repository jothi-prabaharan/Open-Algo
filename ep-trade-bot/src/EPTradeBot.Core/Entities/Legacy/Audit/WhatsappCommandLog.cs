// Generated from OpenAlgo table 'whatsapp_command_logs' (database.whatsapp_db). Maintained by hand from here on.
namespace EPTradeBot.Core.Entities.Legacy.Audit;

/// <summary>Port of OpenAlgo table <c>whatsapp_command_logs</c>.</summary>
public sealed class WhatsappCommandLog
{
    public int Id { get; set; }
    public string WhatsappJid { get; set; } = "";
    public string Command { get; set; } = "";
    public string? Parameters { get; set; }
    public DateTime? ExecutedAt { get; set; } = DateTime.UtcNow;
}
