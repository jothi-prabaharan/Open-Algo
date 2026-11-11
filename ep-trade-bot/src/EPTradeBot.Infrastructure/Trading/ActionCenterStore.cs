using EPTradeBot.Core.Entities.Audit;
using EPTradeBot.Core.Entities.Legacy.Audit;
using EPTradeBot.Core.Time;
using EPTradeBot.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace EPTradeBot.Infrastructure.Trading;

/// <summary>
/// The Action Center's table of orders waiting for a person (OpenAlgo's pending_orders). A row's life: <c>pending</c>, then <c>submitting</c> while an
/// approval runs (claimed in one statement, so two approvals of one order cannot both send it), then <c>approved</c> (it was sent), <c>refused</c>
/// (the guard or a risk limit stopped it at approval; nothing was sent), <c>rejected</c> (a person said no) or <c>expired</c> (it waited past the day).
/// Every decision is also written to the immutable decision log.
/// </summary>
public sealed class ActionCenterStore(IDbContextFactory<EpTradeDbContext> contexts, TimeProvider clock)
{
    public const string Pending = "pending", Submitting = "submitting", Approved = "approved", Refused = "refused", Rejected = "rejected", Expired = "expired";

    private static string IstText(DateTime utc) => Ist.FromUtc(utc).ToString("yyyy-MM-dd HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture);

    public async Task<int> QueueAsync(Guid accountId, string apiType, string orderData, CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        await using var db = await contexts.CreateDbContextAsync(cancellationToken);
        var row = new ActionCenterPendingOrder
        {
            UserId = accountId.ToString("N"), ApiType = apiType, OrderData = orderData, CreatedAt = now, CreatedAtIst = IstText(now), Status = Pending,
        };
        db.Set<ActionCenterPendingOrder>().Add(row);
        await db.SaveChangesAsync(cancellationToken);
        return row.Id;
    }

    public async Task<ActionCenterPendingOrder?> GetAsync(int id, CancellationToken cancellationToken)
    {
        await using var db = await contexts.CreateDbContextAsync(cancellationToken);
        return await db.Set<ActionCenterPendingOrder>().AsNoTracking().SingleOrDefaultAsync(o => o.Id == id, cancellationToken);
    }

    /// <summary>Newest first. <paramref name="status"/> is one status or null for all.</summary>
    public async Task<IReadOnlyList<ActionCenterPendingOrder>> ListAsync(Guid? accountId, string? status, CancellationToken cancellationToken)
    {
        await ExpireStaleAsync(cancellationToken);
        await using var db = await contexts.CreateDbContextAsync(cancellationToken);
        var query = db.Set<ActionCenterPendingOrder>().AsNoTracking().AsQueryable();
        if (accountId is { } id) { var user = id.ToString("N"); query = query.Where(o => o.UserId == user); }
        if (status is not null) query = query.Where(o => o.Status == status);
        return await query.OrderByDescending(o => o.Id).Take(500).ToListAsync(cancellationToken);
    }

    /// <summary>Takes the order for approval, once: true only for the one caller that moved it from pending.</summary>
    public async Task<bool> ClaimAsync(int id, CancellationToken cancellationToken)
    {
        await using var db = await contexts.CreateDbContextAsync(cancellationToken);
        return await db.Set<ActionCenterPendingOrder>().Where(o => o.Id == id && o.Status == Pending)
            .ExecuteUpdateAsync(s => s.SetProperty(o => o.Status, Submitting), cancellationToken) == 1;
    }

    public async Task FinishApprovalAsync(int id, Guid? accountId, string actor, bool sent, string? brokerOrderId, string message, bool bulk, CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        await using var db = await contexts.CreateDbContextAsync(cancellationToken);
        var row = await db.Set<ActionCenterPendingOrder>().SingleAsync(o => o.Id == id, cancellationToken);
        row.Status = sent ? Approved : Refused;
        row.ApprovedAt = now;
        row.ApprovedAtIst = IstText(now);
        row.ApprovedBy = actor;
        row.BrokerOrderId = brokerOrderId;
        row.BrokerStatus = sent ? "sent" : "refused";
        if (!sent) row.RejectedReason = message;
        db.ActionCenterDecisions.Add(Decision(id, accountId, ApprovalDecision.Approved, actor, sent ? null : message, bulk, now));
        await db.SaveChangesAsync(cancellationToken);
    }

    /// <summary>Records a person's no. False when the order is no longer waiting.</summary>
    public async Task<bool> RejectAsync(int id, Guid? accountId, string actor, string? reason, bool bulk, CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        await using var db = await contexts.CreateDbContextAsync(cancellationToken);
        var moved = await db.Set<ActionCenterPendingOrder>().Where(o => o.Id == id && o.Status == Pending)
            .ExecuteUpdateAsync(s => s.SetProperty(o => o.Status, Rejected).SetProperty(o => o.RejectedAt, now).SetProperty(o => o.RejectedAtIst, IstText(now))
                .SetProperty(o => o.RejectedBy, actor).SetProperty(o => o.RejectedReason, reason), cancellationToken);
        if (moved != 1) return false;
        db.ActionCenterDecisions.Add(Decision(id, accountId, ApprovalDecision.Rejected, actor, reason, bulk, now));
        await db.SaveChangesAsync(cancellationToken);
        return true;
    }

    /// <summary>Orders that waited past the 03:00 IST session boundary can no longer be approved: the day they were meant for is over.</summary>
    public async Task<int> ExpireStaleAsync(CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        var boundary = Ist.LastSessionExpiryUtc(now);
        await using var db = await contexts.CreateDbContextAsync(cancellationToken);
        var stale = await db.Set<ActionCenterPendingOrder>().Where(o => o.Status == Pending && o.CreatedAt < boundary).ToListAsync(cancellationToken);
        foreach (var row in stale)
        {
            row.Status = Expired;
            row.RejectedAt = now;
            row.RejectedAtIst = IstText(now);
            row.RejectedBy = "system";
            row.RejectedReason = "It was not approved before the trading day ended.";
            db.ActionCenterDecisions.Add(Decision(row.Id, Guid.TryParseExact(row.UserId, "N", out var account) ? account : null, ApprovalDecision.Expired, "system", row.RejectedReason, false, now));
        }

        await db.SaveChangesAsync(cancellationToken);
        return stale.Count;
    }

    private static ActionCenterDecision Decision(int pendingId, Guid? accountId, ApprovalDecision decision, string actor, string? reason, bool bulk, DateTime now) => new()
    {
        OccurredAtUtc = now, OccurredAtIst = Ist.FromUtc(now), PendingOrderId = pendingId, AccountId = accountId, Decision = decision, Actor = actor, Reason = reason, BulkAction = bulk,
    };
}
