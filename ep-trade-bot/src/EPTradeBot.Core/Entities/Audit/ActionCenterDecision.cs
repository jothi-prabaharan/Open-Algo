namespace EPTradeBot.Core.Entities.Audit;

public enum ApprovalDecision
{
    Approved = 0,
    Rejected = 1,
    Expired = 2,
}

/// <summary>Immutable record of an Action Center approval or rejection.</summary>
public sealed class ActionCenterDecision : IImmutableAuditRecord
{
    public long Id { get; init; }
    public DateTime OccurredAtUtc { get; init; }
    public DateTime OccurredAtIst { get; init; }
    public int PendingOrderId { get; init; }
    public Guid? AccountId { get; init; }
    public ApprovalDecision Decision { get; init; }
    public string Actor { get; init; } = "";
    public string? Reason { get; init; }
    public bool BulkAction { get; init; }
}
