using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using EPTradeBot.Core.Brokers;
using EPTradeBot.Core.Brokers.Gtt;
using static EPTradeBot.Infrastructure.Brokers.JsonRead;

namespace EPTradeBot.Infrastructure.Brokers.IndMoney;

/// <summary>
/// IndMoney (INDstocks, api.indstocks.com): port of broker/indmoney api/order_api.py, api/funds.py, api/rate_limiter.py and mapping/order_data.py, mapping/transform_data.py. The access token is the whole Authorization header. A reply is
/// {status: success, data}, {status: failure, error: {msg}} or {status: error, message}. Calls are paced to four fifths of IndMoney's documented rates for one shared clock (orders 10 a second, data and quotes 5, everything else 15), a 429 on
/// anything but an order write is retried after the wait IndMoney names (else one, two and four seconds, at most thirty), and an order write is never retried: it is not idempotent, and a 429 may have been accepted before the throttle.
/// A stop order goes out as a smart TRIGGER order on the NSE only; a market order goes out as a market order.
/// </summary>
[BrokerPlugin]
public sealed partial class IndMoneyPlugin(IHttpClientFactory http, BrokerSessionFactory sessions, ISymbolMaster symbols, TimeProvider clock) : IBrokerOrders, IBrokerPortfolio, IBrokerFunds
{
    private const string DefaultBaseUrl = "https://api.indstocks.com";
    private const string Display = "IndMoney";

    public string Name => "indmoney";
    public string DisplayName => "IndMoney";
    public BrokerCapability Capabilities => BrokerCapability.Orders | BrokerCapability.Portfolio | BrokerCapability.Funds | BrokerCapability.Quotes | BrokerCapability.Depth | BrokerCapability.History;
    public IReadOnlyList<string> RequiredKeys { get; } = ["ApiKey"];

    private HttpClient Client() => http.CreateClient(Name);

    private DateTimeOffset Now => clock.GetUtcNow();

    // ---- pacing ----------------------------------------------------------------------------------------------

    private static readonly Dictionary<string, double> Rates = new() { ["order"] = 10 * 0.8, ["data"] = 5 * 0.8, ["quote"] = 5 * 0.8, ["non_trading"] = 15 * 0.8 };
    private static readonly HashSet<string> OrderWritePaths = ["/order", "/order/modify", "/order/cancel", "/smart/order", "/smart/order/modify", "/smart/order/cancel"];
    private static readonly Lock PacingGate = new();
    private static readonly Dictionary<string, long> NextFree = [];

    private static string Classify(string path, HttpMethod method)
    {
        path = path.TrimEnd('/');
        if (path.StartsWith("/market/quotes", StringComparison.Ordinal)) return "quote";
        if (path.StartsWith("/market/historical", StringComparison.Ordinal) || path.StartsWith("/market/instruments", StringComparison.Ordinal)) return "data";
        if (method == HttpMethod.Post && OrderWritePaths.Contains(path)) return "order";
        if (path.StartsWith("/margin", StringComparison.Ordinal)) return "data";
        return "non_trading";
    }

    /// <summary>Takes the next slot of the bucket, shared by every caller of the process, and waits for it. A gap set for the broker replaces the documented one (the tests set it to nothing).</summary>
    private async Task PaceAsync(string bucket, CancellationToken cancellationToken)
    {
        var interval = sessions.HistoryGap(Name, TimeSpan.FromSeconds(1 / Rates[bucket]));
        long wait;
        lock (PacingGate)
        {
            var now = Stopwatch.GetTimestamp();
            var start = Math.Max(now, NextFree.GetValueOrDefault(bucket));
            wait = start - now;
            NextFree[bucket] = start + (long)(interval.TotalSeconds * Stopwatch.Frequency);
        }

        if (wait > 0) await Task.Delay(TimeSpan.FromSeconds((double)wait / Stopwatch.Frequency), cancellationToken);
    }

    /// <summary>One paced call. The status and the body as read (null when it was not JSON, or empty).</summary>
    private async Task<(int Status, JsonNode? Body, string Text)> SendAsync(BrokerSession session, HttpMethod method, string path, IEnumerable<KeyValuePair<string, string>>? query, JsonNode? body, CancellationToken cancellationToken)
    {
        var bucket = Classify(path, method);
        var retries = bucket == "order" ? 0 : 3;
        var url = sessions.BaseUrl(Name, DefaultBaseUrl) + path;
        if (query is not null)
        {
            var parts = string.Join('&', query.Select(q => $"{Uri.EscapeDataString(q.Key)}={Uri.EscapeDataString(q.Value).Replace("%2C", ",", StringComparison.Ordinal)}"));
            if (parts.Length > 0) url += "?" + parts;
        }

        for (var attempt = 0; ; attempt++)
        {
            await PaceAsync(bucket, cancellationToken);
            using var request = new HttpRequestMessage(method, url);
            request.Headers.TryAddWithoutValidation("Authorization", session.AuthToken);
            request.Headers.TryAddWithoutValidation("Accept", "application/json");
            request.Content = new StringContent(body?.ToJsonString() ?? "", Encoding.UTF8, "application/json");
            try
            {
                using var response = await Client().SendAsync(request, cancellationToken);
                var status = (int)response.StatusCode;
                if (status == 429 && attempt < retries)
                {
                    var after = response.Headers.TryGetValues("Retry-After", out var values) && double.TryParse(values.First(), NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds)
                        ? Math.Min(Math.Max(seconds, 0.05), 30) : Math.Min(Math.Pow(2, attempt), 30);
                    await Task.Delay(sessions.RetryBackoff(Name, TimeSpan.FromSeconds(after)), cancellationToken);
                    continue;
                }

                var text = await response.Content.ReadAsStringAsync(cancellationToken);
                JsonNode? parsed = null;
                try
                {
                    parsed = JsonNode.Parse(text);
                }
                catch (System.Text.Json.JsonException)
                {
                }

                return (status, parsed, text);
            }
            catch (HttpRequestException ex)
            {
                throw new BrokerException($"{Display} did not answer. Check your connection, then try again.", 502, ex);
            }
        }
    }

    /// <summary>The reason in a reply: the failure's error message, else the message, else null.</summary>
    private static string? Reason(JsonNode? body)
    {
        if (body is not JsonObject o) return null;
        if (Str(o["status"]) == "failure" && o["error"] is JsonObject e && Truthy(e["msg"])) return Str(e["msg"]);
        if (Truthy(o["message"])) return Str(o["message"]);
        return o["error"] is JsonObject inner && Truthy(inner["msg"]) ? Str(inner["msg"]) : null;
    }

    private static bool IsSuccess(JsonNode? body) => body is JsonObject o && Str(o["status"]) == "success";

    /// <summary>The data of a read, or what went wrong: a status that is not 200 or 201, a reply that is not JSON, or a reply that says error or failure.</summary>
    private async Task<(JsonNode? Data, string? Error)> ReadAsync(BrokerSession session, string path, IEnumerable<KeyValuePair<string, string>>? query, CancellationToken cancellationToken)
    {
        var (status, body, text) = await SendAsync(session, HttpMethod.Get, path, query, null, cancellationToken);
        if (status is not (200 or 201)) return (null, Reason(body) ?? $"HTTP {status}");
        if (string.IsNullOrWhiteSpace(text) || body is null) return (null, "no reason was given");
        if (body is JsonObject o)
        {
            if (o["success"] is JsonValue s && s.TryGetValue<bool>(out var flag) && !flag) return (null, Reason(o) ?? "no reason was given");
            if (Str(o["status"]) is "error" or "failure") return (null, Reason(o) ?? "no reason was given");
            if (Str(o["status"]) == "success" && o.ContainsKey("data")) return (o["data"], null);
        }

        return (body, null);
    }

    // ---- vocabulary ------------------------------------------------------------------------------------------

    private static string Product(string? product) => product switch { "CNC" => "CNC", "NRML" => "MARGIN", "MIS" => "INTRADAY", _ => "INTRADAY" };

    private static string Segment(string exchange) => exchange switch { "NSE" or "BSE" => "EQUITY", "NFO" or "BFO" or "CDS" or "BCD" or "MCX" => "DERIVATIVE", _ => "EQUITY" };

    private static string ApiExchange(string exchange) => exchange switch { "NSE" => "NSE", "BSE" => "BSE", "NFO" => "NSE", "BFO" => "BSE", "CDS" => "NSE", "BCD" => "BSE", "MCX" => "MCX", _ => "NSE" };

    private static string SegmentOfOrder(string orderId) => orderId.StartsWith("DRV-", StringComparison.Ordinal) ? "DERIVATIVE" : "EQUITY";

    private static readonly HashSet<string> SmartTypes = ["TRIGGER", "OCO", "GTT_LIMIT", "GTT_MARKET"];

    // ---- orders ----------------------------------------------------------------------------------------------

    private static string? ExtractOrderId(JsonNode? body)
    {
        if (body is not JsonObject o || o["data"] is not JsonObject data) return null;
        if (Truthy(data["order_id"])) return Str(data["order_id"]);
        if (data["order_data"] is JsonArray entries)
            foreach (var entry in entries.OfType<JsonObject>())
                if (Truthy(entry["order_id"])) return Str(entry["order_id"]);
        return null;
    }

    private static JsonValue Num(double value) => JsonValue.Create(value);

    public async Task<BrokerCallResult> PlaceAsync(BrokerSession session, BrokerOrder order, CancellationToken cancellationToken)
    {
        var info = await symbols.FindAsync(Name, order.Symbol, order.Exchange, cancellationToken);
        if (info is null) return BrokerCallResult.Refused(400, $"{order.Symbol} on {order.Exchange} is not in the {Display} master contract. Check the symbol, or download the master contract again.");
        var exchange = order.Exchange.ToUpperInvariant();
        var apiExchange = ApiExchange(exchange);
        if (exchange is not ("NSE" or "BSE" or "NFO" or "BFO" or "CDS" or "BCD") || apiExchange is not ("NSE" or "BSE"))
            return BrokerCallResult.Refused(400, $"{Display} takes orders on the NSE and BSE segments only; {order.Exchange} cannot be traded here.");
        var algo = apiExchange == "BSE" ? "9999999999999999" : "99999";
        var action = order.Action.ToUpperInvariant();
        JsonObject payload;
        string endpoint;
        if (order.PriceType is "SL" or "SL-M")
        {
            var trigger = (double)order.TriggerPrice;
            if (trigger <= 0) return BrokerCallResult.Refused(400, $"A trigger price is required for a {order.PriceType} order on {order.Symbol}.");
            if (apiExchange != "NSE") return BrokerCallResult.Refused(400, $"{Display} takes stop orders on the NSE only; {order.Exchange} was asked for {order.Symbol}.");
            var limit = order.PriceType == "SL" && (double)order.Price > 0
                ? (double)order.Price
                : info.TickSize is { } tick and > 0 ? MarketPriceProtection.ProtectedPrice(trigger, action, MarketPriceProtection.InstrumentType(info.InstrumentType, order.Symbol), tick) : trigger;
            endpoint = "/smart/order";
            payload = new JsonObject
            {
                ["txn_type"] = action, ["exchange"] = apiExchange, ["segment"] = Segment(exchange), ["product"] = Product(order.Product), ["order_type"] = "TRIGGER", ["validity"] = "DAY", ["security_id"] = info.Token,
                ["qty"] = order.Quantity, ["algo_id"] = algo, ["trigger_price"] = Num(trigger), ["trigger_limit_price"] = Num(limit),
            };
        }
        else
        {
            var type = order.PriceType == "LIMIT" ? "LIMIT" : "MARKET";
            endpoint = "/order";
            payload = new JsonObject
            {
                ["txn_type"] = action, ["exchange"] = apiExchange, ["segment"] = Segment(exchange), ["product"] = Product(order.Product), ["order_type"] = type, ["validity"] = "DAY", ["security_id"] = info.Token,
                ["qty"] = order.Quantity, ["is_amo"] = false, ["algo_id"] = algo,
            };
            if (type == "LIMIT") payload["limit_price"] = Num((double)order.Price);
        }

        (int Status, JsonNode? Body, string Text) reply;
        try
        {
            reply = await SendAsync(session, HttpMethod.Post, endpoint, null, payload, cancellationToken);
        }
        catch (BrokerException ex) when (ex.HttpStatus == 502)
        {
            return new BrokerCallResult(false, 502, $"The order was sent to {Display} but no reply came back, so it may have been placed. Check the order book on {Display} before trying again.", MayHaveBeenSent: true);
        }

        if (reply.Body is not JsonObject answer) return new BrokerCallResult(false, 502, $"{Display} could not confirm the order, so it may have been placed. Check the order book on {Display} before trying again.", MayHaveBeenSent: true);
        if (reply.Status is not (200 or 201)) return BrokerCallResult.Refused(reply.Status, Reason(answer) ?? $"{Display} refused the order.");
        if (IsSuccess(answer))
        {
            var id = ExtractOrderId(answer);
            if (!string.IsNullOrEmpty(id)) return BrokerCallResult.Success(id);
            return new BrokerCallResult(false, 502, $"{Display} accepted the order but sent no order number, so it cannot be followed. Check the order book on {Display} before trying again.", MayHaveBeenSent: true);
        }

        // IndMoney sometimes places an order and then fails to read its own answer; the order may be live.
        if ((Reason(answer) ?? "").Contains("no order number in rs response", StringComparison.OrdinalIgnoreCase))
            return new BrokerCallResult(false, 502, $"{Display} placed the order but sent no order number, so it cannot be followed. Check the order book on {Display} before trying again.", MayHaveBeenSent: true);
        return BrokerCallResult.Refused(400, Reason(answer) ?? $"{Display} refused the order.");
    }

    /// <summary>Whether an order is in the smart order book: a GTT- number is, and any other is decided by the type the order book gives it (a failed read keeps the regular endpoint).</summary>
    private async Task<bool> IsSmartAsync(BrokerSession session, string orderId, CancellationToken cancellationToken)
    {
        if (orderId.StartsWith("GTT-", StringComparison.Ordinal)) return true;
        var (data, error) = await ReadAsync(session, "/order-book", null, cancellationToken);
        if (error is not null || data is not JsonArray rows) return false;
        foreach (var row in rows.OfType<JsonObject>())
            if ((Str(row["id"]) ?? "") == orderId) return SmartTypes.Contains((Str(row["order_type"]) ?? "").ToUpperInvariant());
        return false;
    }

    public async Task<BrokerCallResult> ModifyAsync(BrokerSession session, BrokerOrderChange change, CancellationToken cancellationToken)
    {
        var smart = await IsSmartAsync(session, change.OrderId, cancellationToken);
        var payload = new JsonObject { ["segment"] = SegmentOfOrder(change.OrderId), ["order_id"] = change.OrderId, ["qty"] = change.Quantity, ["limit_price"] = Num((double)change.Price) };
        if (smart)
        {
            payload["algo_id"] = "99999";
            if (change.TriggerPrice != 0)
            {
                payload["trigger_price"] = Num((double)change.TriggerPrice);
                payload["trigger_limit_price"] = payload["limit_price"]!.DeepClone();
                payload.Remove("limit_price");
            }
        }

        var (status, body, _) = await SendAsync(session, HttpMethod.Post, smart ? "/smart/order/modify" : "/order/modify", null, payload, cancellationToken);
        if (body is JsonObject && status == 200 && IsSuccess(body)) return BrokerCallResult.Success(change.OrderId);
        return BrokerCallResult.Refused(status is >= 400 ? status : 400, Reason(body) ?? "Failed to modify order");
    }

    public async Task<BrokerCallResult> CancelAsync(BrokerSession session, string orderId, CancellationToken cancellationToken)
    {
        var smart = await IsSmartAsync(session, orderId, cancellationToken);
        var payload = new JsonObject { ["segment"] = SegmentOfOrder(orderId), ["order_id"] = orderId };
        var (status, body, _) = await SendAsync(session, HttpMethod.Post, smart ? "/smart/order/cancel" : "/order/cancel", null, payload, cancellationToken);
        if (body is JsonObject && status == 200 && IsSuccess(body)) return BrokerCallResult.Success(orderId);
        return BrokerCallResult.Refused(status is >= 400 ? status : 400, Reason(body) ?? "Failed to cancel order");
    }
}
