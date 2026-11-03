using System.Text.Json;
using EPTradeBot.Core.Entities.Audit;
using EPTradeBot.Core.Entities.Core;
using EPTradeBot.Core.Time;
using EPTradeBot.Core.Trading;
using EPTradeBot.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace EPTradeBot.Infrastructure.Trading;

/// <summary>
/// Appends immutable audit records. Each write uses its own context, so a record of a
/// refusal or failure survives the rollback of whatever the order itself was doing.
/// </summary>
public sealed class AuditTrail(IDbContextFactory<EpTradeDbContext> contexts, ITradingGuardService guard)
{
    public async Task RecordOrderAsync(OrderIntent intent, BrokerAccount? account, DispatchResult result, CancellationToken cancellationToken = default)
    {
        var now = DateTime.UtcNow;
        await using var db = await contexts.CreateDbContextAsync(cancellationToken);
        db.OrderAuditLogs.Add(new OrderAuditLog
        {
            OccurredAtUtc = now,
            OccurredAtIst = Ist.FromUtc(now),
            AccountId = account?.Id,
            AccountType = account?.AccountType.ToString(),
            Environment = guard.EnvironmentName,
            Source = intent.Source,
            Outcome = result.Status switch
            {
                DispatchStatus.Accepted => OrderOutcome.Accepted,
                DispatchStatus.Queued => OrderOutcome.Queued,
                DispatchStatus.Refused => OrderOutcome.Refused,
                _ => OrderOutcome.Failed,
            },
            Action = intent.Action.ToUpperInvariant(),
            Symbol = intent.Symbol,
            Exchange = intent.Exchange,
            Quantity = intent.Quantity,
            Price = intent.Price,
            Product = intent.Product,
            PriceType = intent.PriceType,
            BrokerOrderId = result.OrderId,
            Reason = result.Code is null ? result.Message : $"{result.Code}: {result.Message}",
            RequestJson = JsonSerializer.Serialize(intent),
            CorrelationId = intent.CorrelationId,
            Actor = intent.Actor,
        });
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task RecordKillSwitchAsync(KillSwitchScope scope, Guid? accountId, bool engaged, string actor, string? reason, bool completed, object result, CancellationToken cancellationToken = default)
    {
        var now = DateTime.UtcNow;
        await using var db = await contexts.CreateDbContextAsync(cancellationToken);
        db.KillSwitchEvents.Add(new KillSwitchEvent
        {
            OccurredAtUtc = now,
            OccurredAtIst = Ist.FromUtc(now),
            Scope = scope,
            AccountId = accountId,
            Engaged = engaged,
            Actor = actor,
            Reason = reason,
            Completed = completed,
            ResultJson = JsonSerializer.Serialize(result),
        });
        await db.SaveChangesAsync(cancellationToken);
    }
}
