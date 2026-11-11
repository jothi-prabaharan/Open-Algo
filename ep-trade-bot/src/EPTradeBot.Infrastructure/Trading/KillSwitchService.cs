using EPTradeBot.Core.Entities.Audit;
using EPTradeBot.Core.Entities.Core;
using EPTradeBot.Core.Trading;
using EPTradeBot.Infrastructure.Persistence;
using EPTradeBot.Infrastructure.Sandbox;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace EPTradeBot.Infrastructure.Trading;

/// <summary>Outcome for one account. <see cref="Completed"/> is true only when every cancel and square-off succeeded.</summary>
public sealed record AccountKillResult(
    Guid AccountId,
    string DisplayName,
    bool Engaged,
    IReadOnlyList<string> CancelledOrders,
    IReadOnlyList<string> SquaredOff,
    IReadOnlyList<string> Failures)
{
    public bool Completed => Failures.Count == 0;
}

public sealed record GlobalKillResult(int Accounts, int StrategiesHalted, IReadOnlyList<AccountKillResult> Results, IReadOnlyList<string>? StrategyFailures = null)
{
    public bool Completed => Results.All(r => r.Completed) && (StrategyFailures is null || StrategyFailures.Count == 0);
}

/// <summary>
/// Account and global kill switches. Engaging one sets the account's flag first (so new
/// orders are refused from that instant), then cancels open orders, then squares off
/// every open position with market orders through the normal dispatcher. A failure is
/// reported as a failure and the flag stays on: a position the switch could not close
/// is still there, and saying otherwise is how it ends up unwatched.
/// </summary>
public sealed class KillSwitchService(
    IDbContextFactory<EpTradeDbContext> contexts,
    IOrderDispatcher dispatcher,
    SandboxEngine sandbox,
    ILiveOrderRouter live,
    IFreezeQuantityLookup freeze,
    AuditTrail audit,
    EPTradeBot.Infrastructure.Scheduling.StrategyScheduleService strategies,
    ILogger<KillSwitchService> logger)
{
    public async Task<AccountKillResult?> SetAccountAsync(Guid accountId, bool engage, string actor, string? reason, CancellationToken cancellationToken = default)
    {
        var result = await RunAccountAsync(accountId, engage, actor, reason, cancellationToken);
        if (result is not null)
            await audit.RecordKillSwitchAsync(KillSwitchScope.Account, accountId, engage, actor, reason, result.Completed, result, CancellationToken.None);
        return result;
    }

    public async Task<GlobalKillResult> EngageGlobalAsync(string actor, string? reason, CancellationToken cancellationToken = default)
    {
        List<Guid> ids;
        List<Guid> running;
        int halted;
        await using (var db = await contexts.CreateDbContextAsync(cancellationToken))
        {
            running = await db.HostedStrategies.AsNoTracking().Where(s => s.IsRunning).Select(s => s.Id).ToListAsync(cancellationToken);
            ids = await db.BrokerAccounts.AsNoTracking().Select(a => a.Id).ToListAsync(cancellationToken);
            halted = await db.HostedStrategies.Where(s => s.IsEnabled || s.IsRunning)
                .ExecuteUpdateAsync(u => u.SetProperty(s => s.IsEnabled, false).SetProperty(s => s.UpdatedAtUtc, DateTime.UtcNow), cancellationToken);
        }

        // Each account is isolated: one broker failing never stops the others.
        var tasks = ids.Select(async id =>
        {
            try
            {
                return await RunAccountAsync(id, true, actor, reason, CancellationToken.None);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Global kill switch failed for account {AccountId}.", id);
                return new AccountKillResult(id, id.ToString(), false, [], [], ["The kill switch could not be applied to this account. See the server log."]);
            }
        });
        var results = (await Task.WhenAll(tasks)).Where(r => r is not null).Select(r => r!).ToList();
        // Every running strategy is stopped and held stopped, so the schedule does not bring it back; one that will not stop is reported.
        var strategyFailures = new List<string>();
        foreach (var id in running)
        {
            var stopped = await StopStrategyAsync(id, cancellationToken: CancellationToken.None);
            if (stopped is not null) strategyFailures.Add(stopped);
        }

        var global = new GlobalKillResult(ids.Count, halted, results, strategyFailures);

        foreach (var r in results)
            await audit.RecordKillSwitchAsync(KillSwitchScope.Account, r.AccountId, true, actor, reason, r.Completed, r, CancellationToken.None);
        await audit.RecordKillSwitchAsync(KillSwitchScope.Global, null, true, actor, reason, global.Completed, global, CancellationToken.None);
        return global;
    }

    private async Task<AccountKillResult?> RunAccountAsync(Guid accountId, bool engage, string actor, string? reason, CancellationToken cancellationToken)
    {
        BrokerAccount account;
        await using (var db = await contexts.CreateDbContextAsync(cancellationToken))
        {
            var tracked = await db.BrokerAccounts.SingleOrDefaultAsync(a => a.Id == accountId, cancellationToken);
            if (tracked is null) return null;
            tracked.KillSwitchEngaged = engage;
            tracked.KillSwitchEngagedAtUtc = engage ? DateTime.UtcNow : null;
            tracked.KillSwitchReason = engage ? reason ?? "Kill switch" : null;
            tracked.UpdatedAtUtc = DateTime.UtcNow;
            await db.SaveChangesAsync(cancellationToken);
            account = tracked;
        }

        if (!engage) return new AccountKillResult(account.Id, account.DisplayName, false, [], [], []);

        var cancelled = new List<string>();
        var squared = new List<string>();
        var failures = new List<string>();
        IReadOnlyList<OpenPosition> positions = [];

        // The account's strategies stop first, so they cannot send a new order while the switch squares the account off.
        List<Guid> accountStrategies;
        await using (var sdb = await contexts.CreateDbContextAsync(cancellationToken))
            accountStrategies = await sdb.HostedStrategies.AsNoTracking().Where(s => s.AccountId == accountId && s.IsRunning).Select(s => s.Id).ToListAsync(cancellationToken);
        foreach (var strategyId in accountStrategies)
        {
            var failed = await StopStrategyAsync(strategyId, cancellationToken);
            if (failed is not null) failures.Add(failed);
        }

        try
        {
            if (account.AccountType == AccountType.PAPER)
            {
                cancelled.AddRange(await sandbox.CancelAllAsync(account.Id, cancellationToken));
                positions = await sandbox.GetOpenPositionsAsync(account.Id, cancellationToken);
            }
            else
            {
                cancelled.AddRange(await live.CancelAllAsync(account, cancellationToken));
                positions = await live.GetOpenPositionsAsync(account, cancellationToken);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Kill switch could not cancel orders or read positions for {AccountId}.", account.Id);
            failures.Add(ex is InvalidOperationException ? ex.Message : "Open orders or positions could not be read. See the server log.");
        }

        foreach (var p in positions)
        {
            var remaining = Math.Abs(p.NetQuantity);
            var limit = await freeze.GetAsync(p.Exchange, p.Symbol, cancellationToken);
            var chunk = limit > 0 ? limit : remaining;
            while (remaining > 0)
            {
                var qty = Math.Min(chunk, remaining);
                var result = await dispatcher.DispatchAsync(new OrderIntent
                {
                    AccountId = account.Id,
                    Source = OrderSource.KillSwitch,
                    Symbol = p.Symbol,
                    Exchange = p.Exchange,
                    Product = p.Product,
                    Action = p.NetQuantity > 0 ? "SELL" : "BUY",
                    Quantity = qty,
                    PriceType = "MARKET",
                    Strategy = "kill_switch",
                    Actor = actor,
                }, cancellationToken);
                if (result.Status != DispatchStatus.Accepted)
                {
                    failures.Add($"{p.Symbol} ({p.Exchange}, {p.Product}): {result.Message}");
                    break;
                }

                remaining -= qty;
            }

            if (remaining == 0) squared.Add($"{p.Symbol} ({p.Exchange}, {p.Product}) {p.NetQuantity}");
        }

        return new AccountKillResult(account.Id, account.DisplayName, true, cancelled, squared, failures);
    }

    private async Task<string?> StopStrategyAsync(Guid strategyId, CancellationToken cancellationToken)
    {
        try
        {
            var result = await strategies.StopManuallyAsync(strategyId, cancellationToken);
            return result.Ok ? null : $"A running strategy could not be stopped: {result.Message}";
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Kill switch could not stop strategy {StrategyId}.", strategyId);
            return "A running strategy could not be stopped. See the server log.";
        }
    }
}
