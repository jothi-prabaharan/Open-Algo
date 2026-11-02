// Generated from OpenAlgo table 'pending_orders' (database.action_center_db). Maintained by hand from here on.
namespace EPTradeBot.Core.Entities.Legacy.Audit;

/// <summary>Port of OpenAlgo table <c>pending_orders</c>.</summary>
public sealed class ActionCenterPendingOrder
{
    public int Id { get; set; }
    public string UserId { get; set; } = "";
    public string ApiType { get; set; } = "";
    public string OrderData { get; set; } = "";
    public DateTime? CreatedAt { get; set; } = DateTime.UtcNow;
    public string? CreatedAtIst { get; set; }
    public string? Status { get; set; } = "pending";
    public DateTime? ApprovedAt { get; set; }
    public string? ApprovedAtIst { get; set; }
    public string? ApprovedBy { get; set; }
    public DateTime? RejectedAt { get; set; }
    public string? RejectedAtIst { get; set; }
    public string? RejectedBy { get; set; }
    public string? RejectedReason { get; set; }
    public string? BrokerOrderId { get; set; }
    public string? BrokerStatus { get; set; }
}
