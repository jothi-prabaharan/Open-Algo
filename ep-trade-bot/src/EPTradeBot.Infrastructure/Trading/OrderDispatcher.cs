using EPTradeBot.Core.Entities.Core;
using EPTradeBot.Core.Trading;
using EPTradeBot.Infrastructure.Persistence;
using EPTradeBot.Infrastructure.Sandbox;
using EPTradeBot.Infrastructure.Streaming;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace EPTradeBot.Infrastructure.Trading;

/// <summary>
/// The one path every order takes, whatever started it: environment guard, then the
/// account's risk limits, then the sandbox (PAPER) or the broker (LIVE). Every outcome,
/// refusals included, is written to the audit log. The account lock is held from the
/// risk check to the write, so two orders cannot both fit under a limit only one fits under.
/// </summary>
public sealed class OrderDispatcher(
    IDbContextFactory<EpTradeDbContext> contexts,
    ITradingGuardService guard,
    IAccountRiskManager risk,
    SandboxEngine sandbox,
    ILiveOrderRouter live,
    AuditTrail audit,
    OrderEvents events,
    KeyedAsyncLock<Guid> accountLocks,
    ILogger<OrderDispatcher> logger) : IOrderDispatcher
{
    public async Task<DispatchResult> DispatchAsync(OrderIntent intent, CancellationToken cancellationToken = default)
    {
        BrokerAccount? account;
        await using (var db = await contexts.CreateDbContextAsync(cancellationToken))
        {
            account = await db.BrokerAccounts.AsNoTracking().SingleOrDefaultAsync(a => a.Id == intent.AccountId, cancellationToken);
        }

        DispatchResult result;
        if (guard.Check(account) is { } refused)
        {
            result = DispatchResult.Refuse(refused);
        }
        else
        {
            using var _ = await accountLocks.AcquireAsync(intent.AccountId, cancellationToken);

            // Re-read under the lock: a kill switch engaged a moment ago must be seen here.
            await using (var db = await contexts.CreateDbContextAsync(cancellationToken))
            {
                account = await db.BrokerAccounts.AsNoTracking().SingleAsync(a => a.Id == intent.AccountId, cancellationToken);
            }

            if (guard.Check(account) is { } late) result = DispatchResult.Refuse(late);
            else if (await risk.CheckAsync(account, intent, cancellationToken) is { } limit) result = DispatchResult.Refuse(limit);
            else
            {
                try
                {
                    result = account.AccountType == AccountType.PAPER
                        ? await sandbox.PlaceAsync(intent, cancellationToken)
                        : await live.PlaceAsync(account, intent, cancellationToken);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogError(ex, "Order for account {AccountId} failed at its destination.", intent.AccountId);
                    result = new DispatchResult(DispatchStatus.Failed, "The order could not be placed because of an internal error. Nothing was filled. Details are in the server log.", Code: "internal_error");
                }
            }
        }

        await audit.RecordOrderAsync(intent, account, result, CancellationToken.None);
        await events.PublishAsync(intent, account, result);
        return result;
    }
}
