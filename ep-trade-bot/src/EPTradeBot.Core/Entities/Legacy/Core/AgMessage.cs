// Generated from OpenAlgo table 'ag_message' (database.agent_db). Maintained by hand from here on.
namespace EPTradeBot.Core.Entities.Legacy.Core;

/// <summary>Port of OpenAlgo table <c>ag_message</c>.</summary>
public sealed class AgMessage
{
    public int Id { get; set; }
    public int ConversationId { get; set; }
    public string Role { get; set; } = "";
    public string Content { get; set; } = "";
    public string? Tools { get; set; }
    public string? Notices { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
