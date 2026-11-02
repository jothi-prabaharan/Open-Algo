// Generated from OpenAlgo table 'ag_conversation' (database.agent_db). Maintained by hand from here on.
namespace EPTradeBot.Core.Entities.Legacy.Core;

/// <summary>Port of OpenAlgo table <c>ag_conversation</c>.</summary>
public sealed class AgConversation
{
    public int Id { get; set; }
    public string UserId { get; set; } = "";
    public string? Title { get; set; }
    public string Surface { get; set; } = "chat";
    public string? AgnoSessionId { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
