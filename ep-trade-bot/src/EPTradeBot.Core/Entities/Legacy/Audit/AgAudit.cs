// Generated from OpenAlgo table 'ag_audit' (database.agent_db). Maintained by hand from here on.
namespace EPTradeBot.Core.Entities.Legacy.Audit;

/// <summary>Port of OpenAlgo table <c>ag_audit</c>.</summary>
public sealed class AgAudit
{
    public int Id { get; set; }
    public DateTime Ts { get; set; } = DateTime.UtcNow;
    public string Phase { get; set; } = "";
    public string Tool { get; set; } = "";
    public int? ConversationId { get; set; }
    public string? RunId { get; set; }
    public string? Args { get; set; }
    public string? RiskVerdict { get; set; }
    public bool? Ok { get; set; }
    public string? Response { get; set; }
    public string? OrderIds { get; set; }
}
