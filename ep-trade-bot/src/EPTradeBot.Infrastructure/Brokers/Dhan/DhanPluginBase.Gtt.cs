using System.Globalization;
using System.Text.Json.Nodes;
using EPTradeBot.Core.Brokers;
using EPTradeBot.Core.Brokers.Dhan;
using EPTradeBot.Core.Brokers.Gtt;
using static EPTradeBot.Infrastructure.Brokers.JsonRead;

namespace EPTradeBot.Infrastructure.Brokers.Dhan;

/// <summary>
/// Dhan Forever Orders (broker/dhan/api/gtt_api.py and mapping/gtt_data.py). A SINGLE is one leg; an OCO is two legs of one order, stop-loss and
/// target, and Dhan changes one leg per call. A SINGLE can be stored under any leg name, so a modify reads the order's book entry first.
/// </summary>
public abstract partial class DhanPluginBase
{
    private const string ForeverPath = "/v2/forever/orders";

    private static readonly HashSet<string> ActiveStatuses = ["TRANSIT", "PENDING", "CONFIRM"];

    /// <summary>The client id Dhan wants on a Forever Order: the one saved with the key, else the account's.</summary>
    private static string? GttClientId(BrokerSession session)
    {
        if (session.Key("ApiKey") is { } key && key.Contains(":::", StringComparison.Ordinal)) return key.Split(":::", 2)[0];
        return ClientId(session);
    }

    private HttpRequestMessage ForeverRequest(BrokerSession session, HttpMethod method, string path, string? clientId, JsonObject? body)
    {
        var request = new HttpRequestMessage(method, sessions.BaseUrl(Name, DefaultBaseUrl) + path);
        request.Headers.TryAddWithoutValidation("access-token", session.AuthToken);
        request.Headers.TryAddWithoutValidation("Accept", "application/json");
        if (clientId is { Length: > 0 }) request.Headers.TryAddWithoutValidation("client-id", clientId);
        if (body is not null || method == HttpMethod.Delete)
        {
            // A cancel carries no body but still declares JSON, as OpenAlgo sends it.
            request.Content = new StringContent(body?.ToJsonString() ?? "", System.Text.Encoding.UTF8);
            request.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
        }

        return request;
    }

    private async Task<(int Status, JsonNode? Body)> SendForeverAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        using var response = await SendAsync(request, cancellationToken);
        return ((int)response.StatusCode, await ReadAsync(response, cancellationToken));
    }

    private static string? ReasonOf(JsonNode? body) => body is JsonObject o ? (Str(o["errorMessage"]) is { Length: > 0 } e ? e : Str(o["message"]) is { Length: > 0 } m ? m : null) : null;

    private const string NoForeverClientId = "Dhan needs the account's client id for Forever Orders. Save the key as client id, three colons, then the key, or set the client id on the account.";

    protected async Task<GttResult> PlaceForeverAsync(BrokerSession session, GttOrder order, CancellationToken cancellationToken)
    {
        if (GttClientId(session) is not { Length: > 0 } clientId) return GttResult.Refused(401, NoForeverClientId);
        var single = !order.IsOco;
        var body = new JsonObject
        {
            ["dhanClientId"] = clientId, ["orderFlag"] = order.TriggerType, ["transactionType"] = order.Action.ToUpperInvariant(),
            ["exchangeSegment"] = DhanRules.Segment(order.Instrument.Exchange), ["productType"] = DhanRules.Product(order.Product),
            ["orderType"] = order.PriceType, ["validity"] = "DAY", ["securityId"] = order.Instrument.Token, ["quantity"] = order.Quantity,
            ["price"] = single ? (double)order.Price : (double)order.StopLoss,
            // An OCO always has its stoploss trigger, which is what Trigger reads, so one expression serves both.
            ["triggerPrice"] = (double)order.Trigger,
        };
        if (!single)
        {
            body["price1"] = (double)order.Target;
            body["triggerPrice1"] = (double)order.TriggerTg;
            body["quantity1"] = order.Quantity;
        }

        if (!string.IsNullOrEmpty(order.Strategy)) body["correlationId"] = order.Strategy.Length > 30 ? order.Strategy[..30] : order.Strategy;

        using var request = ForeverRequest(session, HttpMethod.Post, ForeverPath, clientId, body);
        var (status, reply) = await SendForeverAsync(request, cancellationToken);
        if (status is 200 or 201 && reply is JsonObject o && Str(o["orderId"]) is { Length: > 0 } id) return GttResult.Success(id);
        return GttResult.Refused(status is 200 or 201 ? 500 : status, ReasonOf(reply) ?? "Failed to place GTT");
    }

    protected async Task<GttResult> ModifyForeverAsync(BrokerSession session, GttOrder order, CancellationToken cancellationToken)
    {
        var triggerId = order.TriggerId ?? "";
        if (triggerId.Length == 0) return GttResult.Refused(400, "trigger_id is required");
        if (GttClientId(session) is not { Length: > 0 } clientId) return GttResult.Refused(401, NoForeverClientId);

        var legNames = order.IsOco ? ["STOP_LOSS_LEG", "TARGET_LEG"] : new List<string> { await StoredLegAsync(session, triggerId, cancellationToken) };
        // Dhan refuses a LIMIT order priced at zero, which the original MARKET trigger may have left behind on a form that defaults to LIMIT.
        var priceType = !order.IsOco && order.PriceType == "LIMIT" && order.Price == 0 ? "MARKET" : order.PriceType;

        var lastId = triggerId;
        foreach (var leg in legNames)
        {
            var (price, trigger) = order.IsOco
                ? leg == "TARGET_LEG" ? ((double)order.Target, (double)order.TriggerTg) : ((double)order.StopLoss, (double)order.TriggerSl)
                : ((double)order.Price, (double)order.Trigger);
            var body = new JsonObject
            {
                ["dhanClientId"] = clientId, ["orderId"] = triggerId, ["orderFlag"] = order.TriggerType, ["orderType"] = priceType, ["legName"] = leg,
                ["quantity"] = order.Quantity, ["price"] = price, ["triggerPrice"] = trigger, ["validity"] = "DAY",
            };
            using var request = ForeverRequest(session, HttpMethod.Put, $"{ForeverPath}/{Uri.EscapeDataString(triggerId)}", clientId, body);
            var (status, reply) = await SendForeverAsync(request, cancellationToken);
            if (status != 200 || reply is not JsonObject o || Str(o["orderId"]) is not { Length: > 0 } id)
            {
                // A refusal that arrives with an OK status is a refusal here; OpenAlgo reports it as a success.
                return GttResult.Refused(status == 200 ? 500 : status, ReasonOf(reply) ?? $"Failed to modify {leg}");
            }

            lastId = id;
        }

        return GttResult.Success(lastId);
    }

    /// <summary>The leg Dhan stored a SINGLE under, from the order's own book entry; the entry leg when the book cannot say.</summary>
    private async Task<string> StoredLegAsync(BrokerSession session, string triggerId, CancellationToken cancellationToken)
    {
        try
        {
            using var request = ForeverRequest(session, HttpMethod.Get, ForeverPath, null, null);
            var (status, reply) = await SendForeverAsync(request, cancellationToken);
            if (status == 200 && reply is JsonArray rows)
            {
                foreach (var row in rows.OfType<JsonObject>().Where(r => Str(r["orderId"]) == triggerId))
                    return Str(row["legName"]) is { Length: > 0 } name ? name.ToUpperInvariant() : "ENTRY_LEG";
            }
        }
        catch (BrokerException)
        {
            // The book is only a hint; without it the entry leg is tried.
        }

        return "ENTRY_LEG";
    }

    protected async Task<GttResult> CancelForeverAsync(BrokerSession session, string triggerId, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(triggerId)) return GttResult.Refused(400, "trigger_id is required");
        using var request = ForeverRequest(session, HttpMethod.Delete, $"{ForeverPath}/{Uri.EscapeDataString(triggerId)}", null, null);
        var (status, reply) = await SendForeverAsync(request, cancellationToken);
        if (status == 200 && reply is JsonObject o && Str(o["orderId"]) is { Length: > 0 } id) return GttResult.Success(id);
        return GttResult.Refused(status == 200 ? 500 : status, ReasonOf(reply) ?? "Failed to cancel GTT");
    }

    protected async Task<JsonArray> ForeverBookAsync(BrokerSession session, CancellationToken cancellationToken)
    {
        using var request = ForeverRequest(session, HttpMethod.Get, ForeverPath, null, null);
        var (status, reply) = await SendForeverAsync(request, cancellationToken);
        if (reply is not JsonArray rows)
            throw new BrokerException(ReasonOf(reply) ?? "Failed to fetch GTT book", status == 200 ? 500 : status);

        // One entry per order: Dhan lists a leg per row, so legs are grouped by order id in the order they first appear.
        var groups = new List<(string Id, List<JsonObject> Legs)>();
        foreach (var row in rows.OfType<JsonObject>())
        {
            var id = Str(row["orderId"]) ?? "";
            if (id.Length == 0 || !ActiveStatuses.Contains((Str(row["orderStatus"]) ?? "").ToUpperInvariant())) continue;
            var group = groups.FindIndex(g => g.Id == id);
            if (group < 0) groups.Add((id, [row]));
            else groups[group].Legs.Add(row);
        }

        var book = new JsonArray();
        foreach (var (id, legs) in groups)
        {
            var first = legs[0];
            var exchange = DhanRules.Exchange(Str(first["exchangeSegment"]) ?? "");
            var brokerSymbol = Str(first["tradingSymbol"]) ?? "";
            var record = brokerSymbol.Length > 0 && exchange.Length > 0 ? await symbols.FindByBrokerSymbolAsync(Name, brokerSymbol, exchange, cancellationToken) : null;
            var sorted = legs.OrderBy(l => Float(l["triggerPrice"])).ToList();

            var outLegs = new JsonArray();
            foreach (var leg in sorted)
            {
                var price = Float(leg["price"]);
                outLegs.Add(new JsonObject
                {
                    ["action"] = (Str(leg["transactionType"]) ?? "").ToUpperInvariant(), ["quantity"] = leg["quantity"]?.DeepClone() ?? 0,
                    ["price"] = leg["price"]?.DeepClone() ?? 0, ["pricetype"] = price == 0 ? "MARKET" : "LIMIT",
                    ["product"] = (Str(leg["productType"]) ?? "") switch { "CNC" => "CNC", "MARGIN" => "NRML", "INTRADAY" => "MIS", _ => "CNC" },
                    ["leg_name"] = Str(leg["legName"]) ?? "",
                });
            }

            var rawStatus = (Str(first["orderStatus"]) ?? "").ToUpperInvariant();
            book.Add(new JsonObject
            {
                ["trigger_id"] = id,
                ["trigger_type"] = (Str(first["orderType"]) ?? "").ToUpperInvariant() == "OCO" ? "two-leg" : "single",
                ["status"] = rawStatus is "TRANSIT" or "PENDING" or "CONFIRM" ? "active" : rawStatus.ToLowerInvariant(),
                ["symbol"] = record?.Symbol is { Length: > 0 } s ? s : brokerSymbol,
                ["exchange"] = exchange,
                ["trigger_prices"] = new JsonArray(sorted.Select(l => (JsonNode?)Float(l["triggerPrice"])).ToArray()),
                ["last_price"] = 0,
                ["legs"] = outLegs,
                ["created_at"] = Str(first["createTime"]) ?? "",
                ["updated_at"] = Str(first["updateTime"]) ?? "",
                ["expires_at"] = "",
            });
        }

        return book;
    }
}
