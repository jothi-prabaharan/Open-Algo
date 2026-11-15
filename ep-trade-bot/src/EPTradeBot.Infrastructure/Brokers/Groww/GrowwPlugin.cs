using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using EPTradeBot.Core.Brokers;
using static EPTradeBot.Infrastructure.Brokers.JsonRead;

namespace EPTradeBot.Infrastructure.Brokers.Groww;

/// <summary>
/// Groww (api.groww.in, broker/groww): orders, books, positions, holdings and funds. The session token is a bearer token and every call carries X-API-VERSION 1.0. Prices are in rupees. A call answers
/// {"status": "SUCCESS", "payload": ...} or {"status": "FAILURE", "error": {"code", "message"}}. Calls are paced by kind (orders, live data, reads, sign-in) and a throttled one (429) is tried again up to
/// four times in all, after the wait Groww asks for. Orders and positions are read once for each segment (CASH, then FNO) and the order list and trades in pages. F&amp;O is exchange NSE or BSE with segment FNO.
/// </summary>
[BrokerPlugin]
public sealed partial class GrowwPlugin(IHttpClientFactory http, BrokerSessionFactory sessions, ISymbolMaster symbols, TimeProvider clock) : IBrokerOrders, IBrokerPortfolio, IBrokerFunds, IBrokerMargin, IBrokerLogin
{
    private const string DefaultBaseUrl = "https://api.groww.in";
    private const string Display = "Groww";
    private const int MaxRetries = 3;

    public string Name => "groww";
    public string DisplayName => "Groww";
    public BrokerCapability Capabilities => BrokerCapability.Orders | BrokerCapability.Portfolio | BrokerCapability.Funds | BrokerCapability.Margin;
    public IReadOnlyList<string> RequiredKeys { get; } = ["ApiKey", "ApiSecret"];

    private HttpClient Client() => http.CreateClient(Name);

    private DateTimeOffset Now => clock.GetUtcNow();

    // ---- pacing and sending ----------------------------------------------------------------------------------

    /// <summary>Seconds between two calls of one kind: 60 over the per-minute limit Groww gives it (history one a second).</summary>
    private static readonly Dictionary<string, double> Interval = new() { ["order"] = 60.0 / 250, ["live"] = 60.0 / 300, ["non_trading"] = 60.0 / 500, ["auth"] = 60.0 / 30, ["history"] = 1.0 };

    private static readonly Dictionary<string, long> NextSlot = [];
    private static readonly Lock Gate = new();

    private async Task PaceAsync(string category, CancellationToken cancellationToken)
    {
        var interval = sessions.Pacing(Name, TimeSpan.FromSeconds(Interval[category])).TotalSeconds;
        double wait;
        lock (Gate)
        {
            // The slot after the last one booked: a burst queues, one call after another.
            var now = Stopwatch.GetTimestamp();
            var start = Math.Max(now, NextSlot.GetValueOrDefault(category));
            wait = (start - now) / (double)Stopwatch.Frequency;
            NextSlot[category] = start + (long)(interval * Stopwatch.Frequency);
        }

        if (wait > 0) await Task.Delay(TimeSpan.FromSeconds(wait), cancellationToken);
    }

    /// <summary>How long to wait before trying a throttled call again: what Groww asked for (seconds or a date), else 1, 2 and 4 seconds.</summary>
    private TimeSpan RetryDelay(HttpResponseMessage response, int attempt)
    {
        var delay = TimeSpan.FromSeconds(Math.Pow(2, attempt));
        if (response.Headers.TryGetValues("Retry-After", out var values) && values.FirstOrDefault() is { Length: > 0 } text)
        {
            if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds)) delay = TimeSpan.FromSeconds(Math.Max(seconds, 0.05));
            else if (DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out var at)) delay = TimeSpan.FromSeconds(Math.Max((at - Now).TotalSeconds, 0.05));
        }

        return sessions.RetryBackoff(Name, delay);
    }

    private static string Query(IEnumerable<KeyValuePair<string, string>>? query) =>
        query is null ? "" : "?" + string.Join("&", query.Select(q => $"{Uri.EscapeDataString(q.Key)}={Uri.EscapeDataString(q.Value)}"));

    /// <summary>One paced call, tried again while Groww answers 429. The status, the body as read (null when it was not JSON) and the text.</summary>
    private async Task<(int Status, JsonNode? Body, string Text)> SendAsync(BrokerSession session, HttpMethod method, string path, string category, IEnumerable<KeyValuePair<string, string>>? query, JsonNode? body, CancellationToken cancellationToken)
    {
        var url = sessions.BaseUrl(Name, DefaultBaseUrl) + path + Query(query);
        for (var attempt = 0; ; attempt++)
        {
            await PaceAsync(category, cancellationToken);
            using var request = new HttpRequestMessage(method, url);
            request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {session.AuthToken}");
            request.Headers.TryAddWithoutValidation("Accept", "application/json");
            request.Headers.TryAddWithoutValidation("X-API-VERSION", "1.0");
            if (body is not null) request.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
            try
            {
                using var response = await Client().SendAsync(request, cancellationToken);
                var status = (int)response.StatusCode;
                if (status == 429 && attempt < MaxRetries)
                {
                    await Task.Delay(RetryDelay(response, attempt), cancellationToken);
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

    /// <summary>The reason in a failed answer: the error's message, else the message, else <paramref name="fallback"/> (Groww's documented failure is {"status": "FAILURE", "error": {"code", "message"}}).</summary>
    internal static string ReasonOf(JsonNode? body, string fallback)
    {
        if (body is JsonObject o)
        {
            if (o["error"] is JsonObject error && Truthy(error["message"])) return Str(error["message"]) ?? fallback;
            if (Truthy(o["message"])) return Str(o["message"]) ?? fallback;
        }

        return fallback;
    }

    private static bool Succeeded(int status, JsonNode? body) => status == 200 && body is JsonObject o && Str(o["status"]) == "SUCCESS";

    private static JsonObject Payload(JsonNode? body) => (body as JsonObject)?["payload"] as JsonObject ?? [];

    // ---- vocabulary ------------------------------------------------------------------------------------------

    private static string Unsupported(string exchange) => $"{Display}'s trading API does not support the {exchange} exchange. Orders can be placed on NSE, BSE, NFO and BFO only.";

    private static string ExchangeOf(string exchange) => exchange.ToUpperInvariant() switch { "NSE" or "NFO" => "NSE", "BSE" or "BFO" => "BSE", _ => throw new ArgumentException(Unsupported(exchange)) };

    private static string SegmentOf(string exchange) => exchange.ToUpperInvariant() switch { "NSE" or "BSE" => "CASH", "NFO" or "BFO" => "FNO", _ => throw new ArgumentException(Unsupported(exchange)) };

    private static string OrderTypeOf(string pricetype) => pricetype.ToUpperInvariant() switch
    {
        "MARKET" => "MARKET", "LIMIT" => "LIMIT", "SL" => "SL", "SL-M" => "SL_M",
        _ => throw new ArgumentException($"{Display} does not support the {pricetype} price type. Use MARKET, LIMIT, SL or SL-M."),
    };

    private static string ProductOf(string product) => product.ToUpperInvariant() switch
    {
        "CNC" => "CNC", "NRML" => "NRML", "MIS" => "MIS",
        _ => throw new ArgumentException($"{Display} does not support the {product} product. Use CNC, MIS or NRML."),
    };

    private static string SideOf(string action) => action.ToUpperInvariant() switch
    {
        "BUY" => "BUY", "SELL" => "SELL",
        _ => throw new ArgumentException($"Unknown action {action}. Use BUY or SELL."),
    };

    /// <summary>OpenAlgo's exchange for a Groww exchange and segment: F&amp;O is NFO on NSE and BFO on BSE, anything else as sent.</summary>
    internal static string OpenAlgoExchange(string? exchange, string? segment) =>
        segment == "FNO" ? exchange switch { "NSE" => "NFO", "BSE" => "BFO", _ => exchange ?? "" } : exchange ?? "";

    private string ReferenceId()
    {
        // The day and eight characters of a unique id: Groww wants 8 to 20 letters, digits and at most two hyphens, and refuses one it has seen.
        var ist = TimeZoneInfo.ConvertTime(Now, TimeZoneInfo.FindSystemTimeZoneById("Asia/Kolkata"));
        return $"{ist:yyyyMMdd}-{Guid.NewGuid():N}"[..17];
    }

    // ---- orders ----------------------------------------------------------------------------------------------

    public async Task<BrokerCallResult> PlaceAsync(BrokerSession session, BrokerOrder order, CancellationToken cancellationToken)
    {
        JsonObject payload;
        try
        {
            // An exchange Groww cannot trade is the reason to give, before any symbol lookup.
            _ = ExchangeOf(order.Exchange);
            var info = await symbols.FindAsync(Name, order.Symbol, order.Exchange, cancellationToken);
            if (info is null || string.IsNullOrEmpty(info.BrokerSymbol))
                return BrokerCallResult.Refused(400, $"{order.Symbol} is not in the {order.Exchange} master contract. Check the symbol, or download the master contract again.");
            var product = ProductOf(order.Product);
            var exchange = ExchangeOf(order.Exchange);
            var segment = SegmentOf(order.Exchange);
            var orderType = OrderTypeOf(order.PriceType);
            var side = SideOf(order.Action);
            var pricetype = order.PriceType.ToUpperInvariant();
            payload = new JsonObject
            {
                ["trading_symbol"] = info.BrokerSymbol, ["quantity"] = order.Quantity, ["validity"] = "DAY", ["exchange"] = exchange, ["segment"] = segment, ["product"] = product, ["order_type"] = orderType, ["transaction_type"] = side,
                ["order_reference_id"] = ReferenceId(),
            };
            if (pricetype is "LIMIT" or "SL") payload["price"] = (double)order.Price;
            if (pricetype is "SL" or "SL-M") payload["trigger_price"] = (double)order.TriggerPrice;
            if (order.Quantity <= 0) return BrokerCallResult.Refused(400, $"Invalid quantity format: {order.Quantity}. Must be a positive integer.");
        }
        catch (ArgumentException ex)
        {
            return BrokerCallResult.Refused(400, ex.Message);
        }

        (int Status, JsonNode? Body, string Text) reply;
        try
        {
            reply = await SendAsync(session, HttpMethod.Post, "/v1/order/create", "order", null, payload, cancellationToken);
        }
        catch (BrokerException ex) when (ex.HttpStatus == 502)
        {
            return new BrokerCallResult(false, 500, $"Could not reach {Display} to place the order. Check the order book before retrying.", MayHaveBeenSent: true);
        }

        if (Succeeded(reply.Status, reply.Body))
        {
            var data = Payload(reply.Body);
            var id = Truthy(data["groww_order_id"]) ? Str(data["groww_order_id"]) : null;
            var status = (Str(data["order_status"]) ?? "").ToUpperInvariant();
            // Accepted by the API but failed straight away (a stop-loss trigger outside the allowed range): Groww's remark says why.
            if (status is "FAILED" or "REJECTED") return BrokerCallResult.Refused(400, Truthy(data["remark"]) ? Str(data["remark"])! : $"{Display} rejected the order");
            if (string.IsNullOrEmpty(id)) return new BrokerCallResult(false, 502, $"{Display} did not return an order ID. Check the order book before placing the order again.", MayHaveBeenSent: true);
            return BrokerCallResult.Success(id);
        }

        if (reply.Status == 200 && reply.Body is not JsonObject)
            return new BrokerCallResult(false, 502, $"{Display} sent an answer that could not be read, so the order may have been placed. Check the order book on {Display} before trying again.", MayHaveBeenSent: true);
        var message = ReasonOf(reply.Body, $"{Display} did not accept the order (HTTP {reply.Status})");
        return BrokerCallResult.Refused(reply.Status >= 400 ? reply.Status : 400, message);
    }

    public async Task<BrokerCallResult> ModifyAsync(BrokerSession session, BrokerOrderChange change, CancellationToken cancellationToken)
    {
        JsonObject payload;
        try
        {
            if (string.IsNullOrEmpty(change.OrderId)) return BrokerCallResult.Refused(400, "Order ID (orderid) is required for order modification");
            var orderType = OrderTypeOf(change.PriceType);
            payload = new JsonObject { ["groww_order_id"] = change.OrderId, ["order_type"] = orderType, ["segment"] = SegmentOf(string.IsNullOrEmpty(change.Exchange) ? "NSE" : change.Exchange) };
            if (change.Quantity <= 0) return BrokerCallResult.Refused(400, $"Invalid quantity format: {change.Quantity}. Must be a positive integer.");
            payload["quantity"] = change.Quantity;
            if (orderType is "LIMIT" or "SL") payload["price"] = (double)change.Price;
            if (orderType is "SL" or "SL_M") payload["trigger_price"] = (double)change.TriggerPrice;
        }
        catch (ArgumentException ex)
        {
            return BrokerCallResult.Refused(400, ex.Message);
        }

        try
        {
            var (status, body, _) = await SendAsync(session, HttpMethod.Post, "/v1/order/modify", "order", null, payload, cancellationToken);
            if (Succeeded(status, body)) return BrokerCallResult.Success(change.OrderId);
            if (status == 200 && body is not JsonObject)
                return new BrokerCallResult(false, 502, $"{Display} sent an answer that could not be read, so the change may have been made. Check the order book on {Display} before trying again.", MayHaveBeenSent: true);
            return BrokerCallResult.Refused(status >= 400 ? status : 400, ReasonOf(body, $"{Display} could not modify order {change.OrderId}"));
        }
        catch (BrokerException ex) when (ex.HttpStatus == 502)
        {
            return new BrokerCallResult(false, 500, $"Could not reach {Display} to modify the order. Check the order book before retrying.", MayHaveBeenSent: true);
        }
    }

    public async Task<BrokerCallResult> CancelAsync(BrokerSession session, string orderId, CancellationToken cancellationToken)
    {
        try
        {
            var message = $"{Display} could not cancel order {orderId}";
            var status = 400;
            foreach (var segment in await SegmentsOfAsync(session, orderId, cancellationToken))
            {
                var (code, body, _) = await SendAsync(session, HttpMethod.Post, "/v1/order/cancel", "order", null, new JsonObject { ["segment"] = segment, ["groww_order_id"] = orderId }, cancellationToken);
                if (Succeeded(code, body)) return BrokerCallResult.Success(orderId);
                if (code == 200 && body is not JsonObject)
                    return new BrokerCallResult(false, 502, $"{Display} sent an answer that could not be read, so the order may have been cancelled. Check the order book on {Display}.", MayHaveBeenSent: true);
                message = ReasonOf(body, message);
                status = code >= 400 ? code : 400;
            }

            return BrokerCallResult.Refused(status, message);
        }
        catch (BrokerException ex) when (ex.HttpStatus == 502)
        {
            return new BrokerCallResult(false, 500, $"Could not reach {Display} to cancel the order. Check the order book before retrying.", MayHaveBeenSent: true);
        }
    }

    /// <summary>The segments to try for a cancel or a read of one order's trades: the order's own, read from the order book, else CASH then FNO (a cancel sent to the wrong segment is refused, so the second try is harmless).</summary>
    private async Task<IReadOnlyList<string>> SegmentsOfAsync(BrokerSession session, string orderId, CancellationToken cancellationToken)
    {
        try
        {
            var book = await ReadOrderListAsync(session, cancellationToken);
            if (book.Failure is null || book.Orders.Count > 0)
                foreach (var order in book.Orders)
                    if (Str(order["groww_order_id"]) == orderId)
                    {
                        var segment = Str(order["segment"]);
                        if (segment is "CASH" or "FNO") return [segment];
                        break;
                    }
        }
        catch (BrokerException ex) when (ex.HttpStatus == 502)
        {
        }

        return ["CASH", "FNO"];
    }
}
