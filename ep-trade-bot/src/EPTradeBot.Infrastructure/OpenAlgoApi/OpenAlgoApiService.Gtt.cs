using System.Text.Json.Nodes;
using EPTradeBot.Core.Brokers;
using EPTradeBot.Core.Brokers.Gtt;
using EPTradeBot.Core.Contracts.V1;
using EPTradeBot.Core.Entities.Core;
using EPTradeBot.Core.Trading;

namespace EPTradeBot.Infrastructure.OpenAlgoApi;

/// <summary>
/// The four GTT endpoints (placegttorder, modifygttorder, cancelgttorder, gttorderbook), ported from services/*_gtt_*_service.py.
/// A GTT becomes a real order at the broker when it triggers, so placing and changing one is checked by the guard and the
/// kill switch like any order. Cancelling one is checked by the guard (Development never reaches a live broker) but not by the kill switch,
/// because it can only reduce what is live; reading the book is never checked.
/// </summary>
public sealed partial class OpenAlgoApiService
{
    private static V1Result SandboxGtt(BrokerAccount account) =>
        Mark(account, V1Result.Error(400, "GTT orders are not available on sandbox accounts yet. Use a live account."));

    private async Task<(GttOrder? Order, V1Result? Refused)> GttOrderAsync(BrokerAccount account, GttOrderRequest r, string? triggerId, IBrokerGtt gtt, CancellationToken ct)
    {
        var exchange = r.Exchange;
        if (await symbols.FindAsync(gtt.Name, r.Symbol, exchange, ct) is not { } instrument)
            return (null, Mark(account, V1Result.Error(400, $"{r.Symbol} on {exchange} is not in the {gtt.DisplayName} master contract. Check the symbol, or download the master contract again.")));
        var oco = string.Equals(r.TriggerType, "OCO", StringComparison.OrdinalIgnoreCase);
        var sl = r.TriggerPriceSl ?? 0;
        var tg = r.TriggerPriceTg ?? 0;
        // SINGLE keeps the one trigger that was given (the stoploss trigger wins if both are) and no second leg.
        var order = new GttOrder(instrument, oco ? "OCO" : "SINGLE", r.Action.ToUpperInvariant(), r.Product, (int)r.Quantity, r.PriceType, r.Price ?? 0,
            oco || sl > 0 ? sl : 0, oco ? tg : sl > 0 ? 0 : tg, oco ? r.StopLoss ?? 0 : 0, oco ? r.Target ?? 0 : 0, triggerId);
        return (order, null);
    }

    public async Task<V1Result> PlaceGttOrderAsync(BrokerAccount account, PlaceGttOrderRequest r, CancellationToken ct)
    {
        if (account.AccountType == AccountType.PAPER) return SandboxGtt(account);
        if (guard.Check(account) is { } refused) return Refusal(account, refused);
        if (account.KillSwitchEngaged) return Refusal(account, new Refusal("kill_switch_engaged", $"The kill switch is on for {account.DisplayName}, so GTT orders cannot be placed. Release it first."));
        if (!Broker<IBrokerGtt>(account, out var gtt, out var session, out var missing)) return missing!;
        var (order, bad) = await GttOrderAsync(account, r, null, gtt!, ct);
        if (bad is not null) return bad;

        return await Guarded(async () =>
        {
            var result = await gtt!.PlaceGttAsync(session!, order!, ct);
            return result.Ok && result.TriggerId is { Length: > 0 } id
                ? new V1Result(200, new JsonObject { ["status"] = "success", ["trigger_id"] = id })
                : V1Result.Error(result.HttpStatus == 200 ? 500 : result.HttpStatus, result.Message);
        }, "Failed to place GTT due to internal error");
    }

    public async Task<V1Result> ModifyGttOrderAsync(BrokerAccount account, ModifyGttOrderRequest r, CancellationToken ct)
    {
        if (account.AccountType == AccountType.PAPER) return SandboxGtt(account);
        if (guard.Check(account) is { } refused) return Refusal(account, refused);
        if (account.KillSwitchEngaged) return Refusal(account, new Refusal("kill_switch_engaged", $"The kill switch is on for {account.DisplayName}, so GTT orders cannot be changed. Release it first."));
        if (!Broker<IBrokerGtt>(account, out var gtt, out var session, out var missing)) return missing!;
        var (order, bad) = await GttOrderAsync(account, r, r.TriggerId, gtt!, ct);
        if (bad is not null) return bad;

        return await Guarded(async () =>
        {
            var result = await gtt!.ModifyGttAsync(session!, order!, ct);
            return result.Ok
                ? new V1Result(200, new JsonObject { ["status"] = "success", ["trigger_id"] = result.TriggerId ?? r.TriggerId })
                : V1Result.Error(result.HttpStatus, result.Message);
        }, "Failed to modify GTT due to internal error");
    }

    public async Task<V1Result> CancelGttOrderAsync(BrokerAccount account, CancelGttOrderRequest r, CancellationToken ct)
    {
        if (account.AccountType == AccountType.PAPER) return SandboxGtt(account);
        // A write to a live broker, so the environment guard applies (Development never reaches one). The kill switch does not stop a cancel: it only reduces what is live.
        if (guard.Check(account) is { } refused) return Refusal(account, refused);
        if (!Broker<IBrokerGtt>(account, out var gtt, out var session, out var missing)) return missing!;
        return await Guarded(async () =>
        {
            var result = await gtt!.CancelGttAsync(session!, r.TriggerId, ct);
            return result.Ok
                ? new V1Result(200, new JsonObject { ["status"] = "success", ["trigger_id"] = result.TriggerId ?? r.TriggerId })
                : V1Result.Error(result.HttpStatus, result.Message);
        }, "Failed to cancel GTT due to internal error");
    }

    public async Task<V1Result> GttOrderBookAsync(BrokerAccount account, GttOrderBookRequest r, CancellationToken ct)
    {
        if (account.AccountType == AccountType.PAPER) return SandboxGtt(account);
        if (!Broker<IBrokerGtt>(account, out var gtt, out var session, out var missing)) return missing!;
        return await Guarded(async () =>
        {
            var book = await gtt!.GetGttBookAsync(session!, string.Equals(r.Status, "all", StringComparison.OrdinalIgnoreCase), ct);
            // Triggers that can still fire come first; a stable sort keeps each group in the order the broker gave it.
            var ordered = new JsonArray(book.OfType<JsonObject>().Select((row, i) => (row, i))
                .OrderBy(x => !string.Equals(Text(x.row["status"]), "active", StringComparison.OrdinalIgnoreCase)).ThenBy(x => x.i)
                .Select(x => (JsonNode?)x.row.DeepClone()).ToArray());
            return new V1Result(200, new JsonObject { ["status"] = "success", ["data"] = ordered });
        });
    }
}
