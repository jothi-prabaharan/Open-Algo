// Generated from OpenAlgo table 'flow_workflow_executions' (database.flow_db). Maintained by hand from here on.
namespace EPTradeBot.Core.Entities.Legacy.Core;

/// <summary>Port of OpenAlgo table <c>flow_workflow_executions</c>.</summary>
public sealed class FlowWorkflowExecution
{
    public int Id { get; set; }
    public int WorkflowId { get; set; }
    public string? Status { get; set; } = "pending";
    public DateTime? StartedAt { get; set; }
    public DateTime? CompletedAt { get; set; }
    public string? Logs { get; set; } = "[]";
    public string? Error { get; set; }
}
