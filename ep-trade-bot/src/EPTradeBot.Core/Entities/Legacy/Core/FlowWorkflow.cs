// Generated from OpenAlgo table 'flow_workflows' (database.flow_db). Maintained by hand from here on.
namespace EPTradeBot.Core.Entities.Legacy.Core;

/// <summary>Port of OpenAlgo table <c>flow_workflows</c>.</summary>
public sealed class FlowWorkflow
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public string? Description { get; set; }
    public string? Nodes { get; set; } = "[]";
    public string? Edges { get; set; } = "[]";
    public bool? IsActive { get; set; } = false;
    public string? ScheduleJobId { get; set; }
    public string? WebhookToken { get; set; }
    public string? WebhookSecret { get; set; }
    public bool? WebhookEnabled { get; set; } = false;
    public string? WebhookAuthType { get; set; } = "payload";
    public string? ApiKey { get; set; }
    public DateTime? CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? UpdatedAt { get; set; } = DateTime.UtcNow;
}
