using System.Globalization;
using System.Text.Json.Nodes;
using EPTradeBot.Core.Entities.Core;
using EPTradeBot.Core.Entities.Legacy.Sandbox;
using EPTradeBot.Core.Options;
using EPTradeBot.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace EPTradeBot.Infrastructure.OpenAlgoApi;

/// <summary>
/// Per-strategy realized, unrealized and total profit. The book is rebuilt from the sandbox account's fills, in the order they happened, tagged with the strategy that placed each order;
/// a live account's fills carry no strategy label here yet, so its figures are reported as unavailable rather than as zero.
/// </summary>
public sealed partial class OpenAlgoApiService
{
    private async Task<JsonArray> StrategyLegsAsync(BrokerAccount account, string? strategy, CancellationToken ct)
    {
        await using var db = await contexts.CreateDbContextAsync(ct);
        var userId = Sandbox.SandboxEngine.UserId(account.Id);
        var trades = await db.Set<SandboxTrade>().AsNoTracking().Where(t => t.UserId == userId && t.Strategy != null && t.Strategy != "").OrderBy(t => t.TradeTimestamp).ThenBy(t => t.Id).ToListAsync(ct);
        var ledger = new StrategyBookLedger();
        var cumulative = new Dictionary<string, (double Quantity, double Notional)>();
        foreach (var trade in trades)
        {
            var day = StrategyBookLedger.SessionDate(EPTradeBot.Core.Time.Ist.FromUtc(DateTime.SpecifyKind(trade.TradeTimestamp, DateTimeKind.Utc)));
            ledger.RecordTag(trade.Orderid, userId, trade.Strategy!, trade.Symbol, trade.Exchange, trade.Product, day);
            // An order filled in pieces reports its running average, as a broker does.
            var (quantity, notional) = cumulative.GetValueOrDefault(trade.Orderid);
            quantity += trade.Quantity;
            notional += trade.Quantity * (double)trade.Price;
            cumulative[trade.Orderid] = (quantity, notional);
            ledger.ApplyFill(trade.Orderid, quantity, notional / quantity, trade.Action, day);
        }

        var today = StrategyBookLedger.SessionDate(NowIst());
        return ledger.Legs(today, userId, string.IsNullOrEmpty(strategy) ? null : strategy);
    }

    public async Task<V1Result> StrategyPnlAsync(BrokerAccount account, string? strategy, CancellationToken ct)
    {
        if (account.AccountType != AccountType.PAPER)
            return V1Result.Error(409, "Strategy P&L is kept for sandbox accounts only so far; fills on a live account carry no strategy label here yet.");
        var legs = await StrategyLegsAsync(account, strategy, ct);
        var positions = await PositionBookAsync(account, ct);
        var result = StrategyPnlTool.Get(() => legs, () => positions.Status == 200 ? positions.Body : new JsonObject { ["status"] = "error", ["message"] = Text(positions.Body["message"]) ?? "unavailable" }, string.IsNullOrEmpty(strategy) ? null : strategy);
        return result["status"]?.GetValue<string>() == "error" ? new V1Result(502, result) : new V1Result(200, result);
    }
}
