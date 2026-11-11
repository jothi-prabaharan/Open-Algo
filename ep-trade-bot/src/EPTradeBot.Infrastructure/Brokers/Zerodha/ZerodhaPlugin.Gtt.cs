using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using EPTradeBot.Core.Brokers;
using EPTradeBot.Core.Brokers.Gtt;
using EPTradeBot.Core.Brokers.Zerodha;

namespace EPTradeBot.Infrastructure.Brokers.Zerodha;

/// <summary>Zerodha GTT triggers (broker/zerodha/api/gtt_api.py and mapping/gtt_data.py). Kite takes a single or two-leg trigger as JSON inside a form.</summary>
public sealed partial class ZerodhaPlugin
{
    public async Task<GttResult> PlaceGttAsync(BrokerSession session, GttOrder order, CancellationToken cancellationToken)
    {
        if (await PrepareGttAsync(session, order, cancellationToken) is not { } prepared) return GttResult.Refused(502, LastPriceFailed);
        if (prepared.Refusal is { } refusal) return refusal;

        using var request = Request(session, HttpMethod.Post, "/gtt/triggers");
        request.Content = prepared.Form;
        using var response = await SendAsync(request, cancellationToken);
        var body = await ReadJsonAsync(response, cancellationToken);
        // OpenAlgo reads the answer as JSON without a fallback, so an answer that is not JSON ends as its internal error.
        if (body is null) return GttResult.Refused(500, "Failed to place GTT due to internal error");
        var status = (int)response.StatusCode;
        if (Str(body["status"]) == "success" && Str(body["data"]?["trigger_id"]) is { Length: > 0 } id) return GttResult.Success(id);
        // An error in an OK answer is mapped to a failure status by the service.
        return GttResult.Refused(status, Str(body["message"]) ?? "Failed to place GTT");
    }

    public async Task<GttResult> ModifyGttAsync(BrokerSession session, GttOrder order, CancellationToken cancellationToken)
    {
        var triggerId = order.TriggerId ?? "";
        if (triggerId.Length == 0) return GttResult.Refused(400, "trigger_id is required");
        if (await PrepareGttAsync(session, order, cancellationToken) is not { } prepared) return GttResult.Refused(502, LastPriceFailed);
        if (prepared.Refusal is { } refusal) return refusal;

        using var request = Request(session, HttpMethod.Put, "/gtt/triggers/" + Uri.EscapeDataString(triggerId));
        request.Content = prepared.Form;
        return await GttWriteResultAsync(request, triggerId, "Failed to modify GTT", cancellationToken);
    }

    public async Task<GttResult> CancelGttAsync(BrokerSession session, string triggerId, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(triggerId)) return GttResult.Refused(400, "trigger_id is required");
        using var request = Request(session, HttpMethod.Delete, "/gtt/triggers/" + Uri.EscapeDataString(triggerId));
        return await GttWriteResultAsync(request, triggerId, "Failed to cancel GTT", cancellationToken);
    }

    public async Task<JsonArray> GetGttBookAsync(BrokerSession session, bool includeHistory, CancellationToken cancellationToken)
    {
        using var request = Request(session, HttpMethod.Get, "/gtt/triggers");
        using var response = await SendAsync(request, cancellationToken);
        var status = (int)response.StatusCode;
        var body = await ReadJsonAsync(response, cancellationToken);
        if (body is null || Str(body["status"]) != "success")
            throw new BrokerException(body is null ? "Zerodha sent a reply EP Trade bot could not read." : Str(body["message"]) ?? "Failed to fetch GTT book", status == 200 ? 500 : status);

        var rows = new JsonArray();
        foreach (var gtt in (body["data"] as JsonArray ?? []).OfType<JsonObject>())
        {
            // Active only by default, so the book lists triggers that can still fire. With history the rest come through carrying Kite's own status.
            if (!includeHistory && !string.Equals(Str(gtt["status"]), "active", StringComparison.OrdinalIgnoreCase)) continue;
            var condition = gtt["condition"] as JsonObject;
            var exchange = Str(condition?["exchange"]) ?? "";
            var brokerSymbol = Str(condition?["tradingsymbol"]) ?? "";
            var record = brokerSymbol.Length > 0 ? await symbols.FindByBrokerSymbolAsync(BrokerName, brokerSymbol, exchange, cancellationToken) : null;
            var legs = new JsonArray();
            foreach (var leg in (gtt["orders"] as JsonArray ?? []).OfType<JsonObject>())
            {
                legs.Add(new JsonObject
                {
                    ["action"] = Str(leg["transaction_type"]) ?? "",
                    ["quantity"] = McxContracts.FromKite((long)Float(leg["quantity"]), brokerSymbol, exchange == "MCX" ? record?.LotSize ?? 0 : 1),
                    ["price"] = leg["price"]?.DeepClone() ?? 0,
                    ["pricetype"] = Str(leg["order_type"]) ?? "LIMIT",
                    ["product"] = Str(leg["product"]) ?? "",
                });
            }

            rows.Add(new JsonObject
            {
                ["trigger_id"] = Str(gtt["id"]) ?? "",
                ["trigger_type"] = Str(gtt["type"]) ?? "",
                ["status"] = Str(gtt["status"]) ?? "",
                ["symbol"] = record?.Symbol is { Length: > 0 } oa ? oa : brokerSymbol,
                ["exchange"] = exchange,
                ["trigger_prices"] = condition?["trigger_values"]?.DeepClone() ?? new JsonArray(),
                ["last_price"] = condition?["last_price"]?.DeepClone() ?? 0,
                ["legs"] = legs,
                ["created_at"] = Str(gtt["created_at"]) ?? "",
                ["updated_at"] = Str(gtt["updated_at"]) ?? "",
                ["expires_at"] = Str(gtt["expires_at"]) ?? "",
            });
        }

        return rows;
    }

    private const string LastPriceFailed = "Failed to fetch last_price from Zerodha quotes";

    private sealed record PreparedGtt(FormUrlEncodedContent? Form, GttResult? Refusal);

    /// <summary>
    /// The form Kite takes. Kite's GTT condition needs the last price, so it is read first; a MARKET request becomes a LIMIT a protection slab
    /// beyond the price (Kite takes only LIMIT in a GTT): around the last price for a SINGLE, around each trigger for an OCO.
    /// Null when the last price cannot be read.
    /// </summary>
    private async Task<PreparedGtt?> PrepareGttAsync(BrokerSession session, GttOrder order, CancellationToken cancellationToken)
    {
        var instrument = order.Instrument;
        var quote = await RawQuoteAsync(session, instrument.Symbol, instrument.Exchange, cancellationToken);
        var lastPrice = Float(quote["last_price"]);
        if (lastPrice == 0) return null;

        var price = (double)order.Price;
        var stopLoss = (double)order.StopLoss;
        var target = (double)order.Target;
        if (order.PriceType == "MARKET")
        {
            var type = MarketPriceProtection.InstrumentType(instrument.InstrumentType, instrument.Symbol);
            if (order.IsOco)
            {
                if (order.TriggerSl > 0) stopLoss = MarketPriceProtection.ProtectedPrice((double)order.TriggerSl, order.Action, type, instrument.TickSize);
                if (order.TriggerTg > 0) target = MarketPriceProtection.ProtectedPrice((double)order.TriggerTg, order.Action, type, instrument.TickSize);
            }
            else if (lastPrice > 0)
            {
                price = MarketPriceProtection.ProtectedPrice(lastPrice, order.Action, type, instrument.TickSize);
            }
        }

        int contracts;
        try
        {
            // A GTT becomes a real order when it fires, so it carries the same contract count on MCX that a placed order does.
            contracts = instrument.Exchange == "MCX" ? McxContracts.ToKite(order.Quantity, instrument.BrokerSymbol, instrument.LotSize) : order.Quantity;
        }
        catch (McxQuantityException ex)
        {
            return new PreparedGtt(null, GttResult.Refused(400, ex.Message));
        }

        JsonObject Leg(double limit) => new()
        {
            ["exchange"] = instrument.Exchange, ["tradingsymbol"] = instrument.BrokerSymbol, ["transaction_type"] = order.Action,
            ["quantity"] = contracts, ["order_type"] = "LIMIT", ["product"] = order.Product, ["price"] = limit,
        };

        var condition = new JsonObject
        {
            ["exchange"] = instrument.Exchange, ["tradingsymbol"] = instrument.BrokerSymbol,
            ["trigger_values"] = order.IsOco ? new JsonArray((double)order.TriggerSl, (double)order.TriggerTg) : new JsonArray((double)order.Trigger),
            ["last_price"] = lastPrice,
        };
        var orders = order.IsOco ? new JsonArray(Leg(stopLoss), Leg(target)) : new JsonArray(Leg(price));
        var form = new FormUrlEncodedContent(
        [
            new("type", order.IsOco ? "two-leg" : "single"),
            new("condition", condition.ToJsonString()),
            new("orders", orders.ToJsonString()),
        ]);
        return new PreparedGtt(form, null);
    }

    private async Task<GttResult> GttWriteResultAsync(HttpRequestMessage request, string triggerId, string failure, CancellationToken cancellationToken)
    {
        using var response = await SendAsync(request, cancellationToken);
        var status = (int)response.StatusCode;
        var body = await ReadJsonAsync(response, cancellationToken);
        if (body is null) return GttResult.Refused(status == 200 ? 500 : status, "Invalid response");
        if (Str(body["status"]) == "success") return GttResult.Success(body["data"] is JsonObject data && data.ContainsKey("trigger_id") ? Str(data["trigger_id"]) ?? "" : triggerId);
        return GttResult.Refused(status == 200 ? 500 : status, Str(body["message"]) ?? failure);
    }
}
