using System.Text.Json.Nodes;
using EPTradeBot.Core.Contracts.V1;
using EPTradeBot.Core.Entities.Core;
using EPTradeBot.Core.Trading;
using Microsoft.Extensions.Configuration;

namespace EPTradeBot.Infrastructure.OpenAlgoApi;

/// <summary>
/// The endpoints that place several orders for one request: /basketorder, /splitorder,
/// /optionsorder and /optionsmultiorder, ported from services/basket_order_service.py,
/// split_order_service.py, place_options_order_service.py and options_multiorder_service.py.
/// Every part is its own order through the dispatcher, so the environment guard, the kill
/// switch and the account's risk limits apply to each one, and each is audited. OpenAlgo's
/// live basket called the broker directly, past its own order checks.
/// </summary>
public sealed partial class OpenAlgoApiService
{
    /// <summary>OpenAlgo's MAX_ORDERS and MAX_SPLIT_ORDERS(_PER_LEG).</summary>
    private const int MaxSplitOrders = 100;

    /// <summary>
    /// The pause between the parts of one request, so a split of 100 stays within a broker's
    /// order rate. OpenAlgo derives it from ORDER_RATE_LIMIT (10 per second gives 100 ms).
    /// </summary>
    private TimeSpan OrderSpacing => TimeSpan.FromMilliseconds(configuration.GetValue("OpenAlgoApi:OrderSpacingMs", 100));

    private Task PauseAsync(CancellationToken ct) => OrderSpacing > TimeSpan.Zero ? Task.Delay(OrderSpacing, clock, ct) : Task.CompletedTask;

    private Task<DispatchResult> PlacePartAsync(BrokerAccount account, string strategy, string symbol, string exchange, string action, int quantity,
        string priceType, string product, decimal price, decimal trigger, int disclosed, CancellationToken ct) =>
        dispatcher.DispatchAsync(Intent(account, strategy, symbol, exchange, action, quantity, priceType, product, price, trigger, disclosed), ct);

    /// <summary>A queued part (Action Center) has no order id yet; it is reported with the queue's message.</summary>
    private static bool Placed(DispatchResult r) => r.Status is DispatchStatus.Accepted or DispatchStatus.Queued;

    // ---- basket ----------------------------------------------------------------------------------------------

    public async Task<V1Result> BasketOrderAsync(BrokerAccount account, BasketOrderRequest r, CancellationToken ct)
    {
        if (await QueueIfSemiAutoAsync(account, "basketorder", r, ct) is { } queued) return queued;
        if (r.Orders.FirstOrDefault(o => Fractional(account, o.Quantity) is not null) is { } fractionalItem) return Fractional(account, fractionalItem.Quantity)!;
        if (guard.Check(account) is { } refused) return Refusal(account, refused);

        // BUY orders first, then SELL, as OpenAlgo sends them (margin from the hedge first).
        var sorted = r.Orders.Where(o => o.Action.Equals("BUY", StringComparison.OrdinalIgnoreCase))
            .Concat(r.Orders.Where(o => o.Action.Equals("SELL", StringComparison.OrdinalIgnoreCase))).ToList();
        var results = new JsonArray();
        for (var i = 0; i < sorted.Count; i++)
        {
            var o = sorted[i];
            // OpenAlgo sends ten at a time and waits a second between batches; one at a time with spacing keeps the same rate.
            if (i > 0) await PauseAsync(ct);
            var unknown = await UnknownSymbolAsync(account, o.Symbol, o.Exchange, ct);
            var result = unknown is not null
                ? new DispatchResult(DispatchStatus.Refused, Text(unknown.Body["message"]) ?? "Unknown symbol.")
                : await PlacePartAsync(account, r.Strategy, o.Symbol, o.Exchange, o.Action, (int)o.Quantity, o.PriceType, o.Product, o.Price, o.TriggerPrice, o.DisclosedQuantity, ct);
            if (Placed(result))
            {
                var item = new JsonObject { ["symbol"] = o.Symbol, ["exchange"] = o.Exchange, ["product"] = o.Product, ["status"] = "success", ["orderid"] = result.OrderId };
                if (result.Status == DispatchStatus.Queued) item["message"] = result.Message;
                if (account.AccountType == AccountType.PAPER)
                {
                    item["batch_order"] = true;
                    item["is_last_order"] = i == sorted.Count - 1;
                }

                results.Add(item);
            }
            else
            {
                results.Add(new JsonObject { ["symbol"] = o.Symbol, ["status"] = "error", ["message"] = result.Message });
            }
        }

        return Mark(account, new V1Result(200, new JsonObject { ["status"] = "success", ["results"] = results }));
    }

    // ---- split -----------------------------------------------------------------------------------------------

    public async Task<V1Result> SplitOrderAsync(BrokerAccount account, SplitOrderRequest r, CancellationToken ct)
    {
        if (await QueueIfSemiAutoAsync(account, "splitorder", r, ct) is { } queued) return queued;
        if (Fractional(account, r.Quantity) is { } fractional) return fractional;
        var total = (int)r.Quantity;
        var size = r.SplitSize;
        if (size <= 0) return Mark(account, V1Result.Error(400, "Split size must be greater than 0"));
        var parts = Parts(total, size);
        if (parts.Count > MaxSplitOrders) return Mark(account, V1Result.Error(400, $"Total number of orders would exceed maximum limit of {MaxSplitOrders}"));
        if (await UnknownSymbolAsync(account, r.Symbol, r.Exchange, ct) is { } unknown) return unknown;
        if (guard.Check(account) is { } refused) return Refusal(account, refused);

        var results = await PlacePartsAsync(account, parts, q => PlacePartAsync(account, r.Strategy, r.Symbol, r.Exchange, r.Action, q, r.PriceType, r.Product,
            r.Price, r.TriggerPrice, r.DisclosedQuantity, ct), ct);
        return Mark(account, new V1Result(200, new JsonObject { ["status"] = "success", ["total_quantity"] = total, ["split_size"] = size, ["results"] = results }));
    }

    /// <summary>Full parts of <paramref name="size"/>, then the remainder.</summary>
    internal static List<int> Parts(int total, int size)
    {
        var parts = Enumerable.Repeat(size, total / size).ToList();
        if (total % size > 0) parts.Add(total % size);
        return parts;
    }

    private async Task<JsonArray> PlacePartsAsync(BrokerAccount account, List<int> parts, Func<int, Task<DispatchResult>> place, CancellationToken ct)
    {
        var results = new JsonArray();
        for (var i = 0; i < parts.Count; i++)
        {
            if (i > 0) await PauseAsync(ct);
            var result = await place(parts[i]);
            var item = new JsonObject { ["order_num"] = i + 1, ["quantity"] = parts[i] };
            if (Placed(result))
            {
                item["status"] = "success";
                item["orderid"] = result.OrderId;
                if (result.Status == DispatchStatus.Queued) item["message"] = result.Message;
            }
            else
            {
                item["status"] = "error";
                item["message"] = result.Message;
            }

            results.Add(item);
        }

        return results;
    }

    // ---- options order ---------------------------------------------------------------------------------------

    public async Task<V1Result> OptionsOrderAsync(BrokerAccount account, OptionsOrderRequest r, CancellationToken ct)
    {
        if (await QueueIfSemiAutoAsync(account, "optionsorder", r, ct) is { } queued) return queued;
        if (guard.Check(account) is { } refused) return Refusal(account, refused);
        var (option, error) = await ResolveOptionAsync(account, r.Underlying, r.Exchange, r.ExpiryDate, r.StrikeInterval, r.Offset, r.OptionType, null, ct);
        if (error is not null) return Mark(account, error);

        var size = r.SplitSize ?? 0;
        if (size > 0)
        {
            var parts = Parts(r.Quantity, size);
            if (parts.Count > MaxSplitOrders) return Mark(account, V1Result.Error(400, $"Total number of orders would exceed maximum limit of {MaxSplitOrders}"));
            var results = await PlacePartsAsync(account, parts, q => PlacePartAsync(account, r.Strategy, option!.Symbol, option.Exchange, r.Action, q, r.PriceType, r.Product,
                r.Price, r.TriggerPrice, r.DisclosedQuantity, ct), ct);
            return Mark(account, new V1Result(200, new JsonObject
            {
                ["status"] = "success", ["symbol"] = option!.Symbol, ["exchange"] = option.Exchange, ["underlying"] = r.Underlying,
                ["underlying_ltp"] = option.UnderlyingLtp?.DeepClone(), ["offset"] = r.Offset, ["option_type"] = r.OptionType.ToUpperInvariant(),
                ["total_quantity"] = r.Quantity, ["split_size"] = size, ["results"] = results,
            }));
        }

        var placed = await PlacePartAsync(account, r.Strategy, option!.Symbol, option.Exchange, r.Action, r.Quantity, r.PriceType, r.Product, r.Price, r.TriggerPrice, r.DisclosedQuantity, ct);
        if (placed.Status != DispatchStatus.Accepted) return OrderAnswer(account, placed);
        return Mark(account, new V1Result(200, new JsonObject
        {
            ["status"] = "success", ["orderid"] = placed.OrderId, ["symbol"] = option.Symbol, ["exchange"] = option.Exchange, ["underlying"] = r.Underlying,
            ["underlying_ltp"] = option.UnderlyingLtp?.DeepClone(), ["offset"] = r.Offset, ["option_type"] = r.OptionType.ToUpperInvariant(),
        }));
    }

    // ---- options multi-order ---------------------------------------------------------------------------------

    public async Task<V1Result> OptionsMultiOrderAsync(BrokerAccount account, OptionsMultiOrderRequest r, CancellationToken ct)
    {
        if (await QueueIfSemiAutoAsync(account, "optionsmultiorder", r, ct) is { } queued) return queued;
        if (r.Legs.Count == 0) return Mark(account, V1Result.Error(400, "No legs provided in the request"));
        if (guard.Check(account) is { } refused) return Refusal(account, refused);

        // The underlying is priced once for every leg. A failure here is not final: each leg then prices it itself.
        JsonNode? ltp = null;
        var (baseSymbol, embedded) = ParseUnderlying(r.Underlying);
        var quoteSymbol = embedded is null ? r.Underlying : r.Exchange.ToUpperInvariant() is "MCX" or "CDS" ? r.Underlying.ToUpperInvariant() : baseSymbol;
        var quote = await QuoteAsync(account, quoteSymbol, QuoteExchange(baseSymbol, r.Exchange), ct);
        if (quote.Status == 200) ltp = quote.Body["data"]?["ltp"]?.DeepClone();

        var order = r.Legs.Select((leg, index) => (leg, index))
            .OrderBy(x => x.leg.Action.Equals("BUY", StringComparison.OrdinalIgnoreCase) ? 0 : x.leg.Action.Equals("SELL", StringComparison.OrdinalIgnoreCase) ? 1 : 2)
            .Where(x => x.leg.Action.ToUpperInvariant() is "BUY" or "SELL")
            .ToList();
        var spaced = r.Legs.Any(l => (l.SplitSize ?? 0) > 0);
        var results = new List<JsonObject>();
        for (var i = 0; i < order.Count; i++)
        {
            if (spaced && i > 0) await PauseAsync(ct);
            results.Add(await PlaceLegAsync(account, r, order[i].leg, order[i].index, ltp, ct));
        }

        var body = new JsonObject
        {
            ["status"] = "success", ["underlying"] = r.Underlying, ["underlying_ltp"] = ltp?.DeepClone(),
            ["results"] = new JsonArray(results.OrderBy(x => x["leg"]!.GetValue<int>()).Select(x => (JsonNode?)x).ToArray()),
        };
        return Mark(account, new V1Result(200, body));
    }

    private async Task<JsonObject> PlaceLegAsync(BrokerAccount account, OptionsMultiOrderRequest r, OptionsMultiOrderLeg leg, int index, JsonNode? ltp, CancellationToken ct)
    {
        JsonObject Head(ResolvedOption? option)
        {
            var o = new JsonObject { ["leg"] = index + 1 };
            if (option is not null)
            {
                o["symbol"] = option.Symbol;
                o["exchange"] = option.Exchange;
            }

            o["offset"] = leg.Offset;
            o["strike"] = null;
            o["option_type"] = leg.OptionType.ToUpperInvariant();
            o["action"] = leg.Action.ToUpperInvariant();
            return o;
        }

        var (option, error) = await ResolveOptionAsync(account, r.Underlying, r.Exchange, leg.ExpiryDate ?? r.ExpiryDate, r.StrikeInterval, leg.Offset, leg.OptionType, ltp, ct);
        if (error is not null)
        {
            var failed = Head(null);
            failed["status"] = "error";
            failed["message"] = Text(error.Body["message"]) ?? "Failed to resolve option symbol";
            return failed;
        }

        var mode = account.AccountType == AccountType.PAPER ? "analyze" : "live";
        var size = leg.SplitSize ?? 0;
        if (size > 0)
        {
            var parts = Parts(leg.Quantity, size);
            if (parts.Count > MaxSplitOrders)
            {
                var tooMany = Head(option);
                tooMany["status"] = "error";
                tooMany["message"] = $"Split orders would exceed maximum limit of {MaxSplitOrders} per leg";
                return tooMany;
            }

            var split = await PlacePartsAsync(account, parts, q => PlacePartAsync(account, r.Strategy, option!.Symbol, option.Exchange, leg.Action, q, leg.PriceType, leg.Product,
                leg.Price, leg.TriggerPrice, leg.DisclosedQuantity, ct), ct);
            var o = Head(option);
            o["product"] = leg.Product;
            o["status"] = split.OfType<JsonObject>().Any(x => Text(x["status"]) == "success") ? "success" : "error";
            o["total_quantity"] = leg.Quantity;
            o["split_size"] = size;
            o["split_results"] = split;
            o["mode"] = mode;
            return o;
        }

        var placed = await PlacePartAsync(account, r.Strategy, option!.Symbol, option.Exchange, leg.Action, leg.Quantity, leg.PriceType, leg.Product, leg.Price, leg.TriggerPrice, leg.DisclosedQuantity, ct);
        var result = Head(option);
        if (Placed(placed))
        {
            result["product"] = leg.Product;
            result["status"] = "success";
            result["orderid"] = placed.OrderId;
            result["mode"] = mode;
            if (placed.Status == DispatchStatus.Queued) result["message"] = placed.Message;
        }
        else
        {
            result["status"] = "error";
            result["message"] = placed.Message;
        }

        return result;
    }
}
