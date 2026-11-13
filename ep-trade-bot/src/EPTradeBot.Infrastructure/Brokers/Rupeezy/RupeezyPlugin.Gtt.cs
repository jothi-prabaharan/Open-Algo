using System.Text.Json.Nodes;
using EPTradeBot.Core.Brokers;
using EPTradeBot.Core.Brokers.Gtt;
using static EPTradeBot.Infrastructure.Brokers.JsonRead;

namespace EPTradeBot.Infrastructure.Brokers.Rupeezy;

/// <summary>
/// Rupeezy GTT (broker/rupeezy/api/gtt_api.py and mapping/gtt_data.py), single and OCO. OpenAlgo names the two legs of an OCO by price (the stoploss is the
/// lower trigger); Vortex names them by role, and which one is lower depends on the side: a SELL GTT has its stoploss below, a BUY GTT above. Vortex fires
/// only limit orders (RL) or market orders (RL-MKT). A modify changes the legs a GTT already has, each by its own id, so the book is read first.
/// </summary>
public sealed partial class RupeezyPlugin
{
    private const string GttPath = "/trading/orders/gtt";

    private static string GttVariety(string? priceType) => string.Equals(priceType, "MARKET", StringComparison.OrdinalIgnoreCase) ? "RL-MKT" : "RL";

    private static JsonObject GttLeg(long quantity, decimal price, decimal trigger, string variety) => new()
    {
        ["quantity"] = quantity, ["price"] = variety == "RL-MKT" ? 0.0 : (double)price, ["trigger_price"] = (double)trigger, ["variety"] = variety,
    };

    public async Task<GttResult> PlaceGttAsync(BrokerSession session, GttOrder order, CancellationToken cancellationToken)
    {
        var instrument = order.Instrument;
        var lot = instrument.LotSize > 0 ? instrument.LotSize : 1;
        if (WholeLots(order.Quantity, instrument.BrokerExchange, lot, out var quantity) is { } refusal) return GttResult.Refused(400, refusal);

        var variety = GttVariety(order.PriceType);
        var body = new JsonObject
        {
            ["ticker"] = Ticker(instrument.BrokerExchange, instrument.BrokerSymbol), ["transaction_type"] = order.Action.ToUpperInvariant(), ["product"] = Product(order.Product),
        };
        if (order.IsOco)
        {
            var lower = GttLeg(quantity, order.StopLoss, order.TriggerSl, variety);
            var higher = GttLeg(quantity, order.Target, order.TriggerTg, variety);
            var sell = string.Equals(order.Action, "SELL", StringComparison.OrdinalIgnoreCase);
            body["gtt_trigger_type"] = "oco";
            body["stoploss"] = sell ? lower : higher;
            body["profit"] = sell ? higher : lower;
        }
        else
        {
            body["gtt_trigger_type"] = "single";
            foreach (var (key, value) in GttLeg(quantity, order.Price, order.Trigger, variety)) body[key] = value?.DeepClone();
        }

        var (reply, _) = await SendAsync(session, HttpMethod.Post, GttPath, body, cancellationToken);
        if (Str(reply["status"]) == "success")
            return Str((reply["data"] as JsonObject)?["order_id"]) is { Length: > 0 } id ? GttResult.Success(id) : GttResult.Refused(500, "Failed to place GTT");
        return GttResult.Refused(400, Str(reply["message"]) ?? "Failed to place GTT");
    }

    public async Task<GttResult> ModifyGttAsync(BrokerSession session, GttOrder order, CancellationToken cancellationToken)
    {
        var triggerId = order.TriggerId ?? "";
        if (triggerId.Length == 0) return GttResult.Refused(400, "trigger_id is required");

        var (raw, _) = await SendAsync(session, HttpMethod.Get, GttPath, null, cancellationToken);
        if (Str(raw["status"]) != "success") return GttResult.Refused(502, Str(raw["message"]) ?? "Failed to read the GTT book");
        var existing = (raw["data"] as JsonArray ?? []).OfType<JsonObject>().FirstOrDefault(g => Str(g["id"]) == triggerId);
        if (existing is null) return GttResult.Refused(404, $"GTT {triggerId} was not found.");

        var instrument = order.Instrument;
        var lot = instrument.LotSize > 0 ? instrument.LotSize : 1;
        if (WholeLots(order.Quantity, instrument.BrokerExchange, lot, out var quantity) is { } refusal) return GttResult.Refused(400, refusal);

        // Legs are matched by price order: the lower leg takes the new lower trigger, the higher one the new higher trigger.
        var legs = (existing["orders"] as JsonArray ?? []).OfType<JsonObject>().OrderBy(o => F(o["trigger_price"])).ToList();
        var existingOco = legs.Count >= 2;
        if (order.IsOco != existingOco) return GttResult.Refused(400, "A GTT cannot be changed between SINGLE and OCO. Cancel it and place a new one instead.");
        var targets = existingOco ? new[] { (order.StopLoss, order.TriggerSl), (order.Target, order.TriggerTg) } : [(order.Price, order.Trigger)];
        if (legs.Count != targets.Length) return GttResult.Refused(400, $"GTT {triggerId} does not have the legs a change needs. Cancel it and place a new one instead.");

        var variety = GttVariety(order.PriceType);
        var body = new JsonArray();
        for (var i = 0; i < legs.Count; i++)
        {
            var entry = GttLeg(quantity, targets[i].Item1, targets[i].Item2, variety);
            entry["id"] = legs[i]["id"]?.DeepClone();
            if (legs[i]["trail"] is JsonObject trail && trail.Count > 0)
                entry["trail"] = new JsonObject { ["id"] = trail["id"]?.DeepClone(), ["trail_jump_point"] = trail["trail_jump_point"]?.DeepClone(), ["trail_jump_type"] = trail["trail_jump_type"]?.DeepClone() };
            body.Add(entry);
        }

        var (reply, _) = await SendAsync(session, HttpMethod.Put, OrderPath(GttPath, triggerId), body, cancellationToken);
        return Str(reply["status"]) == "success" ? GttResult.Success(triggerId) : GttResult.Refused(400, Str(reply["message"]) ?? "Failed to modify GTT");
    }

    public async Task<GttResult> CancelGttAsync(BrokerSession session, string triggerId, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(triggerId)) return GttResult.Refused(400, "trigger_id is required");
        var (reply, _) = await SendAsync(session, HttpMethod.Delete, OrderPath(GttPath, triggerId), null, cancellationToken);
        return Str(reply["status"]) == "success" ? GttResult.Success(triggerId) : GttResult.Refused(400, Str(reply["message"]) ?? "Failed to cancel GTT");
    }

    /// <summary>One status for a GTT from its legs: active while any leg can still fire, then triggered (in an OCO the leg that fired describes the outcome), else the first leg's.</summary>
    private static string GttStatus(List<JsonObject> legs)
    {
        var statuses = legs.Select(o => (Str(o["status"]) ?? "").ToLowerInvariant()).ToList();
        foreach (var preferred in new[] { "active", "triggered" }) if (statuses.Contains(preferred)) return preferred;
        return statuses.Count > 0 ? statuses[0] : "";
    }

    public async Task<JsonArray> GetGttBookAsync(BrokerSession session, bool includeHistory, CancellationToken cancellationToken)
    {
        var (raw, _) = await SendAsync(session, HttpMethod.Get, GttPath, null, cancellationToken);
        if (Str(raw["status"]) != "success") throw new BrokerException(Str(raw["message"]) ?? "Failed to fetch GTT book", 400);

        var book = new JsonArray();
        foreach (var gtt in (raw["data"] as JsonArray ?? []).OfType<JsonObject>())
        {
            var legs = (gtt["orders"] as JsonArray ?? []).OfType<JsonObject>().OrderBy(o => F(o["trigger_price"])).ToList();
            var status = GttStatus(legs);
            if (!includeHistory && status != "active") continue;

            var vortexExchange = Str(gtt["exchange"]) ?? "";
            var exchange = OpenAlgoExchange(vortexExchange);
            var brokerSymbol = Str(gtt["ticker"]) is { Length: > 0 } ticker ? SplitTicker(ticker) : Str(gtt["symbol"]) ?? "";
            var known = brokerSymbol.Length > 0 ? await symbols.FindByBrokerSymbolAsync(Name, brokerSymbol, exchange, cancellationToken) : null;
            var product = ReverseProduct(exchange, Str(gtt["product"])) is { Length: > 0 } p ? p : Str(gtt["product"]) ?? "";
            var lot = (int)Int(gtt["lot_size"]);

            var rows = new JsonArray();
            foreach (var o in legs)
            {
                rows.Add(new JsonObject
                {
                    ["action"] = (Str(o["transaction_type"]) is { Length: > 0 } side ? side : Str(gtt["transaction_type"]) ?? "").ToUpperInvariant(),
                    ["quantity"] = FromVortexQuantity(o["quantity"], vortexExchange, lot),
                    ["price"] = F(o["price"]),
                    ["pricetype"] = Str(o["variety"]) == "RL-MKT" ? "MARKET" : "LIMIT",
                    ["product"] = product,
                });
            }

            var first = legs.Count > 0 ? legs[0] : null;
            book.Add(new JsonObject
            {
                ["trigger_id"] = Str(gtt["id"]) ?? "",
                ["trigger_type"] = string.Equals(Str(gtt["trigger_type"]), "oco", StringComparison.OrdinalIgnoreCase) ? "two-leg" : "single",
                ["status"] = status,
                ["symbol"] = known?.Symbol is { Length: > 0 } s ? s : brokerSymbol,
                ["exchange"] = exchange,
                ["trigger_prices"] = new JsonArray(legs.Select(o => (JsonNode?)F(o["trigger_price"])).ToArray()),
                ["last_price"] = 0,
                ["legs"] = rows,
                ["created_at"] = Str(first?["created_at"]) ?? "",
                ["updated_at"] = Str(first?["updated_at"]) ?? "",
                ["expires_at"] = Str(first?["expires_at"]) ?? "",
            });
        }

        return book;
    }
}
