using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using EPTradeBot.Core.Brokers;
using EPTradeBot.Core.Brokers.Gtt;
using static EPTradeBot.Infrastructure.Brokers.JsonRead;

namespace EPTradeBot.Infrastructure.Brokers.IiflCapital;

/// <summary>
/// IIFL Capital (api.iiflcapital.com/v1). Port of broker/iiflcapital: api/order_api.py, api/funds.py, mapping/transform_data.py and mapping/order_data.py. Bearer token. An order is a one-element list answered by
/// a result list; a stop-market is sent as a stop-limit priced a slab beyond its trigger and snapped to the tick (IIFL blocks the bare type). A read that is rate limited is tried again with the broker's own wait;
/// an order write is never sent again, because a rate-limit answer can arrive after the broker accepted it.
/// </summary>
[BrokerPlugin]
public sealed partial class IiflCapitalPlugin(IHttpClientFactory http, BrokerSessionFactory sessions, ISymbolMaster symbols) : IBrokerOrders, IBrokerPortfolio, IBrokerFunds, IBrokerMultiQuotes, IBrokerHistory
{
    private const string DefaultBaseUrl = "https://api.iiflcapital.com/v1";

    public string Name => "iiflcapital";
    public string DisplayName => "IIFL Capital";
    public BrokerCapability Capabilities => BrokerCapability.Orders | BrokerCapability.Portfolio | BrokerCapability.Funds | BrokerCapability.Margin | BrokerCapability.Quotes | BrokerCapability.Depth | BrokerCapability.History;
    public IReadOnlyList<string> RequiredKeys { get; } = ["ApiKey", "ApiSecret"];

    // ---- vocabulary ------------------------------------------------------------------------------------------

    private static string BrokerExchange(string? exchange) => (exchange ?? "").ToUpperInvariant() switch
    {
        "NSE" => "NSEEQ", "BSE" => "BSEEQ", "NFO" => "NSEFO", "BFO" => "BSEFO", "CDS" => "NSECURR", "BCD" => "BSECURR", "MCX" => "MCXCOMM", "NCDEX" => "NCDEXCOMM", var other => other,
    };

    private static string OaExchange(string? exchange) => (exchange ?? "").ToUpperInvariant() switch
    {
        "NSEEQ" => "NSE", "BSEEQ" => "BSE", "NSEFO" => "NFO", "BSEFO" => "BFO", "NSECURR" => "CDS", "BSECURR" => "BCD", "MCXCOMM" or "NSECOMM" or "NCDEXCOMM" => "MCX", var other => other,
    };

    private static string OrderType(string? priceType) => (priceType ?? "").ToUpperInvariant() switch { "MARKET" => "MARKET", "LIMIT" => "LIMIT", "SL" => "SL", "SL-M" => "SLM", _ => "MARKET" };

    private static string ReverseOrderType(string? type) => (type ?? "").ToUpperInvariant() switch { "MARKET" => "MARKET", "LIMIT" => "LIMIT", "SL" => "SL", "SLM" => "SL-M", _ => "MARKET" };

    private static string Product(string? product) => (product ?? "").ToUpperInvariant() switch { "MIS" => "INTRADAY", "CNC" => "DELIVERY", "NRML" => "NORMAL", _ => "INTRADAY" };

    private static string ReverseProduct(string? product) => (product ?? "").ToUpperInvariant() switch { "INTRADAY" => "MIS", "DELIVERY" => "CNC", "NORMAL" => "NRML", "BNPL" => "CNC", _ => "MIS" };

    private static string MapStatus(string? status) => (status ?? "").ToUpperInvariant() switch
    {
        "COMPLETE" or "COMPLETED" or "FILLED" or "SUCCESS" or "EXECUTED" => "complete",
        "REJECTED" or "FAIL" or "FAILED" => "rejected",
        "CANCELLED" or "CANCELED" => "cancelled",
        "TRIGGER_PENDING" => "trigger pending",
        _ => "open",
    };

    private static double Flt(JsonNode? node, double fallback = 0.0)
    {
        if (BookFormatting.Number(node) is { } n) return n;
        return Str(node) is { } s && double.TryParse(s.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : fallback;
    }

    /// <summary>float(value or 0): None, empty and zero all read as 0.</summary>
    private static double Num(JsonNode? node) => Truthy(node) ? Flt(node) : 0.0;

    private static JsonNode? Present(JsonObject row, params string[] keys)
    {
        foreach (var key in keys)
        {
            if (row[key] is { } v && !(v is JsonValue jv && jv.TryGetValue<string>(out var s) && s.Length == 0)) return v;
        }

        return null;
    }

    // ---- calls -----------------------------------------------------------------------------------------------

    private HttpClient Client() => http.CreateClient(Name);

    private static readonly string[] RateLimitHints = ["rate limit", "too many request", "try after some time"];

    private static bool RateLimited(int status, string? text) => status == 429 || RateLimitHints.Any(h => (text ?? "").Contains(h, StringComparison.OrdinalIgnoreCase));

    /// <summary>One call: the status, the body as JSON (a body that is not JSON becomes an error object carrying its text) and the wait the broker asked for, if any.</summary>
    private async Task<(int Status, JsonNode? Body, string Text, TimeSpan? RetryAfter)> SendAsync(BrokerSession session, HttpMethod method, string path, JsonNode? body, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(method, sessions.BaseUrl(Name, DefaultBaseUrl) + path);
        request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {session.AuthToken}");
        request.Headers.TryAddWithoutValidation("Accept", "application/json");
        if (body is not null) request.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
        else if (method != HttpMethod.Get) request.Content = new StringContent("", Encoding.UTF8, "application/json");
        try
        {
            using var response = await Client().SendAsync(request, cancellationToken);
            var text = await response.Content.ReadAsStringAsync(cancellationToken);
            JsonNode? parsed;
            try
            {
                parsed = string.IsNullOrWhiteSpace(text) ? null : JsonNode.Parse(text);
            }
            catch (System.Text.Json.JsonException)
            {
                parsed = null;
            }

            parsed ??= new JsonObject { ["status"] = "error", ["message"] = text };
            TimeSpan? wait = null;
            if (response.Headers.TryGetValues("Retry-After", out var values) && double.TryParse(values.FirstOrDefault(), NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds)) wait = TimeSpan.FromSeconds(Math.Max(seconds, 0.05));
            return ((int)response.StatusCode, parsed, text, wait);
        }
        catch (HttpRequestException ex)
        {
            throw new BrokerException($"{DisplayName} did not answer. Check your connection, then try again.", 502, ex);
        }
    }

    /// <summary>A read, tried again up to three times when the broker says it is rate limited (its own wait, else one, two and four seconds).</summary>
    private async Task<(int Status, JsonNode? Body, string Text)> ReadAsync(BrokerSession session, string path, CancellationToken cancellationToken)
    {
        for (var attempt = 0; ; attempt++)
        {
            var (status, body, text, wait) = await SendAsync(session, HttpMethod.Get, path, null, cancellationToken);
            var message = (body as JsonObject) is { } o ? Str(o["message"]) : null;
            if (RateLimited(status, message) && attempt < 3)
            {
                await Task.Delay(sessions.RetryBackoff(Name, wait ?? TimeSpan.FromSeconds(1 << attempt)), cancellationToken);
                continue;
            }

            return (status, body, text);
        }
    }

    // ---- rows ------------------------------------------------------------------------------------------------

    private static readonly string[] RowFields =
    [
        "brokerOrderId", "exchangeOrderId", "orderId", "instrumentId", "token", "exchangeInstrumentID", "tradingSymbol", "symbol", "formattedInstrumentName", "nseTradingSymbol", "bseTradingSymbol",
        "netQuantity", "filledQuantity", "pendingQuantity", "cancelledQuantity", "dpQuantity", "totalQuantity", "transactionType", "tradedPrice", "averageTradedPrice",
    ];

    private static readonly string[] ListKeys = ["orders", "trades", "positions", "holdings", "data", "positionList"];

    private static bool LooksLikeRow(JsonNode? item) => item is JsonObject o && RowFields.Any(f => o[f] is { } v && !(v is JsonValue jv && jv.TryGetValue<string>(out var s) && s.Length == 0));

    /// <summary>The rows of a book answer, leaving out status wrappers (an empty book is sometimes a one-element list holding only a status).</summary>
    private static List<JsonObject> Rows(JsonNode? payload)
    {
        if (payload is JsonArray list) return list.Where(LooksLikeRow).Cast<JsonObject>().ToList();
        if (payload is not JsonObject o) return [];
        if (o["result"] is JsonArray resultList) return resultList.Where(LooksLikeRow).Cast<JsonObject>().ToList();
        if (o["result"] is JsonObject result)
        {
            foreach (var key in ListKeys)
            {
                if (result[key] is JsonArray inner) return inner.Where(LooksLikeRow).Cast<JsonObject>().ToList();
            }

            return LooksLikeRow(result) ? [result] : [];
        }

        foreach (var key in new[] { "data", "orders", "trades", "positions", "holdings" })
        {
            if (o[key] is JsonArray inner) return inner.Where(LooksLikeRow).Cast<JsonObject>().ToList();
        }

        return [];
    }

    /// <summary>The same walk without the wrapper filter, which only decides whether a book answer counts as an answer at all.</summary>
    private static bool HasLooseRows(JsonNode? payload)
    {
        if (payload is JsonArray list) return list.Count > 0;
        if (payload is not JsonObject o) return false;
        if (o["result"] is JsonArray resultList) return resultList.Count > 0;
        if (o["result"] is JsonObject result)
        {
            foreach (var key in ListKeys)
            {
                if (result[key] is JsonArray inner) return inner.Count > 0;
            }

            return true;
        }

        foreach (var key in new[] { "data", "orders", "trades", "positions", "holdings" })
        {
            if (o[key] is JsonArray inner) return inner.Count > 0;
        }

        return false;
    }

    private static bool Ok(JsonObject payload)
    {
        if ((Str(payload["status"]) ?? "").ToLowerInvariant() is "success" or "ok") return true;
        if (payload["result"] is JsonObject result && (Str(result["status"]) ?? "").ToLowerInvariant() is "success" or "ok") return true;
        if (payload["result"] is JsonArray { Count: > 0 } list && list[0] is JsonObject first && (Str(first["status"]) ?? "").ToLowerInvariant() is "success" or "ok") return true;
        return false;
    }

    private static string Message(JsonNode? payload, string fallback)
    {
        if (payload is not JsonObject o) return fallback;
        foreach (var key in new[] { "message", "error", "description" })
        {
            if (o[key] is { } v && Str(v) is { Length: > 0 } text) return text;
        }

        var inner = o["result"] switch { JsonArray { Count: > 0 } a when a[0] is JsonObject f => f, JsonObject r => r, _ => null };
        if (inner is not null)
        {
            foreach (var key in new[] { "message", "error", "description" })
            {
                if (inner[key] is { } v && Str(v) is { Length: > 0 } text) return text;
            }
        }

        return fallback;
    }

    private static JsonObject FirstResult(JsonNode? payload) => (payload as JsonObject)?["result"] switch
    {
        JsonArray { Count: > 0 } a => a[0] as JsonObject ?? [],
        JsonObject r => r,
        _ => [],
    };

    // ---- orders ----------------------------------------------------------------------------------------------

    private static readonly Regex SafeOrderId = new("^[A-Za-z0-9_-]+$", RegexOptions.Compiled);

    private static string PyFloat(double value)
    {
        var text = value.ToString("R", CultureInfo.InvariantCulture);
        return text.Contains('.', StringComparison.Ordinal) || text.Contains('E', StringComparison.Ordinal) ? text : text + ".0";
    }

    private static int TickDecimals(double tick)
    {
        var text = PyFloat(tick);
        var dot = text.IndexOf('.', StringComparison.Ordinal);
        return dot < 0 ? 0 : text.Length - dot - 1;
    }

    private static double SnapToTick(double value, double tick, bool floor)
    {
        var ratio = BookFormatting.RoundPy(value / tick, 6);
        var k = floor ? Math.Floor(ratio) : Math.Ceiling(ratio);
        return BookFormatting.RoundPy(k * tick, TickDecimals(tick));
    }

    /// <summary>The limit an SL-M is sent with: the trigger moved a slab percentage in the fill direction (below for a sell, above for a buy), at least a tick away and snapped to the tick; refused when the tick is unknown.</summary>
    private double ProtectedStopPrice(SymbolRecord instrument, string action, double trigger)
    {
        var tick = instrument.TickSize is { } t ? (double)t : 0.0;
        if (!double.IsFinite(tick) || tick <= 0) throw new BrokerException($"{DisplayName} needs the tick size of {instrument.Symbol} on {instrument.Exchange} to place a stop-market order, and the master contract has none. Download the master contract again.", 400);
        var pct = MarketPriceProtection.Percentage(trigger, MarketPriceProtection.InstrumentType(null, instrument.Symbol)) / 100.0;
        if (action.Equals("SELL", StringComparison.OrdinalIgnoreCase))
        {
            var raw = Math.Min(trigger * (1 - pct), trigger - tick);
            var limit = SnapToTick(raw, tick, true);
            if (limit <= 0) throw new BrokerException($"The trigger {PyFloat(trigger)} for {instrument.Symbol} on {instrument.Exchange} is too low to work out a stop-market limit price. Use a stop-limit order instead.", 400);
            return limit;
        }

        return SnapToTick(Math.Max(trigger * (1 + pct), trigger + tick), tick, false);
    }

    public async Task<BrokerCallResult> PlaceAsync(BrokerSession session, BrokerOrder order, CancellationToken cancellationToken)
    {
        var info = await symbols.FindAsync(Name, order.Symbol, order.Exchange, cancellationToken);
        if (info is null || string.IsNullOrEmpty(info.Token)) return BrokerCallResult.Refused(400, $"{order.Symbol} on {order.Exchange} is not in the {DisplayName} master contract. Check the symbol, or download the master contract again.");

        var orderType = OrderType(order.PriceType);
        var payload = new JsonObject
        {
            ["instrumentId"] = info.Token, ["exchange"] = BrokerExchange(order.Exchange), ["transactionType"] = order.Action.ToUpperInvariant(), ["quantity"] = order.Quantity.ToString(CultureInfo.InvariantCulture),
            ["orderComplexity"] = "REGULAR", ["product"] = Product(order.Product), ["orderType"] = orderType, ["validity"] = "DAY", ["apiOrderSource"] = "openalgo",
        };
        if (orderType is "LIMIT" or "SL") payload["price"] = (double)order.Price;
        if (orderType is "SL" or "SLM")
        {
            var trigger = (double)order.TriggerPrice;
            payload["slTriggerPrice"] = trigger;
            if (orderType == "SLM")
            {
                try
                {
                    payload["price"] = ProtectedStopPrice(info, order.Action, trigger);
                }
                catch (BrokerException ex)
                {
                    return BrokerCallResult.Refused(ex.HttpStatus, ex.Message);
                }

                payload["orderType"] = "SL";
            }
        }

        if (order.DisclosedQuantity > 0) payload["disclosedQuantity"] = order.DisclosedQuantity.ToString(CultureInfo.InvariantCulture);
        if (!string.IsNullOrEmpty(order.Strategy)) payload["orderTag"] = order.Strategy.Length > 50 ? order.Strategy[..50] : order.Strategy;

        (int Status, JsonNode? Body, string Text, TimeSpan? RetryAfter) reply;
        try
        {
            reply = await SendAsync(session, HttpMethod.Post, "/orders", new JsonArray(payload), cancellationToken);
        }
        catch (BrokerException ex) when (ex.HttpStatus == 502)
        {
            return new BrokerCallResult(false, 502, $"The order was sent to {DisplayName} but no reply came back, so it may have been placed. Check the order book on {DisplayName} before trying again.", MayHaveBeenSent: true);
        }

        if (RateLimited(reply.Status, Str((reply.Body as JsonObject)?["message"])))
            return new BrokerCallResult(false, 502, $"{DisplayName} did not confirm this order and asked for it to be tried again later. EP Trade bot did not send it again, because the first one may already have reached {DisplayName}. Check the order book before sending it again.", MayHaveBeenSent: true);

        var result = FirstResult(reply.Body);
        var id = Str(result["brokerOrderId"]);
        var resultStatus = (Str(result["status"]) ?? "").ToLowerInvariant();
        if (reply.Status == 200 && reply.Body is JsonObject body && Ok(body) && resultStatus is "success" or "ok" && !string.IsNullOrEmpty(id) && Truthy(result["brokerOrderId"])) return BrokerCallResult.Success(id);
        return BrokerCallResult.Refused(reply.Status != 200 ? reply.Status : 400, Message(reply.Body, "Failed to place order"));
    }

    public async Task<BrokerCallResult> ModifyAsync(BrokerSession session, BrokerOrderChange change, CancellationToken cancellationToken)
    {
        if (!SafeOrderId.IsMatch(change.OrderId ?? "")) return BrokerCallResult.Refused(400, $"Invalid orderid: '{change.OrderId}'");
        var info = await symbols.FindAsync(Name, change.Symbol, change.Exchange, cancellationToken);
        var payload = new JsonObject { ["quantity"] = change.Quantity.ToString(CultureInfo.InvariantCulture) };
        var orderType = OrderType(change.PriceType);
        payload["orderType"] = orderType;
        if (orderType is "LIMIT" or "SL") payload["price"] = (double)change.Price;
        if (orderType is "SL" or "SLM")
        {
            var trigger = (double)change.TriggerPrice;
            payload["slTriggerPrice"] = trigger;
            if (orderType == "SLM")
            {
                if (info is null) return BrokerCallResult.Refused(400, $"{change.Symbol} on {change.Exchange} is not in the {DisplayName} master contract. Check the symbol, or download the master contract again.");
                try
                {
                    payload["price"] = ProtectedStopPrice(info, change.Action, trigger);
                }
                catch (BrokerException ex)
                {
                    return BrokerCallResult.Refused(ex.HttpStatus, ex.Message);
                }

                payload["orderType"] = "SL";
            }
        }

        if (change.DisclosedQuantity > 0) payload["disclosedQuantity"] = change.DisclosedQuantity.ToString(CultureInfo.InvariantCulture);

        var reply = await SendAsync(session, HttpMethod.Put, $"/orders/{change.OrderId}", payload, cancellationToken);
        if (reply.Status == 200 && reply.Body is JsonObject body && Ok(body)) return BrokerCallResult.Success(change.OrderId!);
        return BrokerCallResult.Refused(reply.Status != 200 ? reply.Status : 400, Message(reply.Body, "Failed to modify order"));
    }

    public async Task<BrokerCallResult> CancelAsync(BrokerSession session, string orderId, CancellationToken cancellationToken)
    {
        if (!SafeOrderId.IsMatch(orderId ?? "")) return BrokerCallResult.Refused(400, $"Invalid orderid: '{orderId}'");
        var reply = await SendAsync(session, HttpMethod.Delete, $"/orders/{orderId}", null, cancellationToken);
        if (reply.Status == 200 && reply.Body is JsonObject body && Ok(body)) return BrokerCallResult.Success(orderId!);
        return BrokerCallResult.Refused(reply.Status != 200 ? reply.Status : 400, Message(reply.Body, "Failed to cancel order"));
    }

    // ---- books -----------------------------------------------------------------------------------------------

    private async Task<string> SymbolAsync(JsonObject row, string exchange, CancellationToken cancellationToken)
    {
        var symbol = Str(Present(row, "tradingSymbol", "symbol", "formattedInstrumentName")) ?? "";
        var token = Str(Present(row, "instrumentId", "token", "exchangeInstrumentID"));
        if (!string.IsNullOrEmpty(token) && !string.IsNullOrEmpty(exchange) && (await symbols.FindByTokenAsync(Name, token, exchange, cancellationToken))?.Symbol is { Length: > 0 } found) return found;
        return symbol;
    }

    private async Task<(JsonObject Payload, List<JsonObject> Rows)> BookAsync(BrokerSession session, string path, string what, CancellationToken cancellationToken)
    {
        var (status, body, _) = await ReadAsync(session, path, cancellationToken);
        if (status == 200 && (body is JsonArray || HasLooseRows(body) || (body is JsonObject o && Ok(o)))) return (body as JsonObject ?? [], Rows(body));
        throw new BrokerException(Message(body, $"Failed to fetch {what}"), 500);
    }

    public async Task<BrokerBook> GetOrderBookAsync(BrokerSession session, CancellationToken cancellationToken)
    {
        var (_, rows) = await BookAsync(session, "/orders", "order book", cancellationToken);
        int buy = 0, sell = 0, completed = 0, open = 0, rejected = 0;
        var result = new JsonArray();
        foreach (var r in rows)
        {
            var exchange = OaExchange(Str(r["exchange"]) ?? "");
            var action = (Str(Present(r, "transactionType", "action")) ?? "").ToUpperInvariant();
            var status = MapStatus(Str(Present(r, "orderStatus", "status")));
            if (action == "BUY") buy++;
            else if (action == "SELL") sell++;
            if (status == "complete") completed++;
            else if (status == "rejected") rejected++;
            else if (status is "open" or "trigger pending") open++;

            double quantity;
            if (Present(r, "quantity", "orderQuantity") is { } q) quantity = Math.Truncate(Num(q));
            else quantity = Math.Truncate(Num(r["filledQuantity"]) + Num(r["pendingQuantity"]) + Num(r["cancelledQuantity"]));
            result.Add(new JsonObject
            {
                ["symbol"] = await SymbolAsync(r, exchange, cancellationToken), ["exchange"] = exchange, ["action"] = (Str(r["transactionType"]) ?? "").ToUpperInvariant(), ["quantity"] = (long)quantity,
                ["price"] = Num(r["price"]), ["trigger_price"] = Present(r, "slTriggerPrice", "triggerPrice") is { } t ? Num(t) : 0.0, ["pricetype"] = ReverseOrderType(Str(r["orderType"]) ?? "MARKET"),
                ["product"] = ReverseProduct(Str(r["product"]) ?? "INTRADAY"), ["orderid"] = Str(Present(r, "brokerOrderId", "exchangeOrderId", "orderId")) ?? "",
                ["order_status"] = MapStatus(Str(r["orderStatus"]) ?? ""), ["rejection_reason"] = Truthy(r["rejectionReason"]) ? Str(r["rejectionReason"]) ?? "" : "",
                ["timestamp"] = Present(r, "exchangeTimestamp", "exchangeUpdateTime", "brokerUpdateTime")?.DeepClone() ?? "",
            });
        }

        return new BrokerBook(result, new JsonObject { ["total_buy_orders"] = buy, ["total_sell_orders"] = sell, ["total_completed_orders"] = completed, ["total_open_orders"] = open, ["total_rejected_orders"] = rejected });
    }

    public async Task<JsonArray> GetTradeBookAsync(BrokerSession session, CancellationToken cancellationToken)
    {
        var (_, rows) = await BookAsync(session, "/trades", "trade book", cancellationToken);
        var result = new JsonArray();
        foreach (var r in rows)
        {
            var exchange = OaExchange(Str(r["exchange"]) ?? "");
            var quantity = (long)Math.Truncate(Present(r, "filledQuantity", "quantity", "filledQty") is { } q ? Num(q) : 0.0);
            var average = Present(r, "tradedPrice", "averageTradedPrice", "price") is { } p ? Num(p) : 0.0;
            result.Add(new JsonObject
            {
                ["symbol"] = await SymbolAsync(r, exchange, cancellationToken), ["exchange"] = exchange, ["product"] = ReverseProduct(Str(r["product"]) ?? "INTRADAY"), ["action"] = (Str(r["transactionType"]) ?? "").ToUpperInvariant(),
                ["quantity"] = quantity, ["average_price"] = average, ["trade_value"] = quantity * average, ["orderid"] = Str(Present(r, "brokerOrderId", "exchangeOrderId", "orderId")) ?? "",
                ["timestamp"] = Present(r, "fillTimestamp", "exchangeTimestamp", "exchangeUpdateTime", "brokerUpdateTime")?.DeepClone() ?? "",
            });
        }

        return result;
    }

    private async Task<List<JsonObject>> PlainBookAsync(BrokerSession session, string path, string what, CancellationToken cancellationToken)
    {
        var (_, body, _) = await ReadAsync(session, path, cancellationToken);
        if (body is JsonObject o && (Str(o["status"]) ?? "").Equals("error", StringComparison.OrdinalIgnoreCase)) throw new BrokerException($"{DisplayName} could not send the {what}: {Message(o, "no reason was given.")}", 500);
        return Rows(body);
    }

    public async Task<JsonArray> GetPositionsAsync(BrokerSession session, CancellationToken cancellationToken)
    {
        var result = new JsonArray();
        foreach (var r in await PlainBookAsync(session, "/positions", "positions", cancellationToken))
        {
            var exchange = OaExchange(Str(r["exchange"]) ?? "");
            var quantity = (long)Math.Truncate(Num(r.ContainsKey("netQuantity") ? r["netQuantity"] : r["quantity"]));
            var average = Num(r.ContainsKey("netAveragePrice") ? r["netAveragePrice"] : r["averagePrice"]);
            var ltp = Present(r, "ltp", "lastPrice", "previousDayClose") is { } l ? Num(l) : 0.0;
            var pnl = Num(r["realizedPnl"]) + (quantity != 0 && ltp > 0 ? (ltp - average) * quantity : 0.0);
            result.Add(new JsonObject
            {
                ["symbol"] = await SymbolAsync(r, exchange, cancellationToken), ["exchange"] = exchange, ["product"] = ReverseProduct(Str(r["product"]) ?? "NORMAL"), ["quantity"] = quantity,
                ["average_price"] = BookFormatting.Fixed2(average), ["ltp"] = BookFormatting.Round2(ltp), ["pnl"] = BookFormatting.Round2(pnl),
            });
        }

        return result;
    }

    private static double HoldingQuantity(JsonObject row)
    {
        foreach (var key in new[] { "dpQuantity", "dpQty", "availableQuantity" })
        {
            var q = Num(row[key]);
            if (q > 0) return q;
        }

        foreach (var key in new[] { "totalQuantity", "totalQty", "quantity", "holdingQuantity" })
        {
            var q = Num(row[key]);
            if (q > 0) return q;
        }

        return Num(row["t1Quantity"]);
    }

    public async Task<BrokerBook> GetHoldingsAsync(BrokerSession session, CancellationToken cancellationToken)
    {
        var rows = await PlainBookAsync(session, "/holdings", "holdings", cancellationToken);
        double value = 0, invested = 0;
        var holdings = new JsonArray();
        foreach (var r in rows)
        {
            var quantity = HoldingQuantity(r);
            var average = r.ContainsKey("averageTradedPrice") ? Num(r["averageTradedPrice"]) : Num(r["averagePrice"]);
            var ltp = LastPrice(r, average);
            value += quantity * ltp;
            invested += quantity * average;

            var whole = (long)Math.Truncate(quantity);
            if (whole <= 0) continue;
            string symbol, exchange;
            if (Truthy(r["nseTradingSymbol"])) (symbol, exchange) = (Str(r["nseTradingSymbol"]) ?? "", "NSE");
            else if (Truthy(r["bseTradingSymbol"])) (symbol, exchange) = (Str(r["bseTradingSymbol"]) ?? "", "BSE");
            else (symbol, exchange) = (Str(Present(r, "tradingSymbol", "formattedInstrumentName", "symbol")) ?? "", "NSE");
            var pnl = whole * (ltp - average);
            holdings.Add(new JsonObject
            {
                ["symbol"] = symbol, ["exchange"] = exchange, ["product"] = ReverseProduct(Str(r["product"]) ?? "DELIVERY"), ["quantity"] = whole, ["average_price"] = BookFormatting.Round2(average),
                ["ltp"] = BookFormatting.Round2(ltp), ["pnl"] = BookFormatting.Round2(pnl), ["pnlpercent"] = whole > 0 && average > 0 ? BookFormatting.Round2(pnl / (whole * average) * 100) : 0.0,
            });
        }

        var total = value - invested;
        return new BrokerBook(holdings, new JsonObject
        {
            ["totalholdingvalue"] = BookFormatting.Round2(value), ["totalinvvalue"] = BookFormatting.Round2(invested), ["totalprofitandloss"] = BookFormatting.Round2(total),
            ["totalpnlpercentage"] = BookFormatting.Round2(invested > 0 ? total / invested * 100 : 0.0),
        });
    }

    /// <summary>row.get("ltp", row.get("previousDayClose", average)) as the book mapping reads it: a missing price is the average, an empty one is zero, and text that is not a number is the average.</summary>
    private static double LastPrice(JsonObject row, double average)
    {
        if (!row.ContainsKey("ltp") && !row.ContainsKey("previousDayClose")) return average;
        var node = row.ContainsKey("ltp") ? row["ltp"] : row["previousDayClose"];
        if (!Truthy(node)) return 0.0;
        if (BookFormatting.Number(node) is { } n) return n;
        return Str(node) is { } text && double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : average;
    }

    // ---- funds -----------------------------------------------------------------------------------------------

    private static readonly string[] LimitKeys = ["tradingLimit", "openingCashLimit", "intradayPayin", "collateralMargin", "utilizedMargin", "creditForSell", "adhocMargin", "utilizedSpanMargin", "utilizedExposureMargin"];

    private static double Limit(JsonNode? node)
    {
        if (node is null) return 0.0;
        return Str(node) is "" or "-" ? 0.0 : Flt(node);
    }

    private async Task<JsonObject> FetchLimitsAsync(BrokerSession session, string path, CancellationToken cancellationToken)
    {
        for (var attempt = 0; ; attempt++)
        {
            (int Status, JsonNode? Body, string Text, TimeSpan? RetryAfter) reply;
            try
            {
                reply = await SendAsync(session, HttpMethod.Get, path, null, cancellationToken);
            }
            catch (BrokerException)
            {
                return [];
            }

            if (RateLimited(reply.Status, reply.Text) && attempt < 3)
            {
                await Task.Delay(sessions.RetryBackoff(Name, reply.RetryAfter ?? TimeSpan.FromSeconds(1 << attempt)), cancellationToken);
                continue;
            }

            if (reply.Status != 200 || reply.Body is not JsonObject payload) return [];
            var result = payload.ContainsKey("result") ? payload["result"] : payload;
            return result switch { JsonObject r => r, JsonArray { Count: > 0 } a when a[0] is JsonObject f => f, _ => [] };
        }
    }

    private static bool HasLimitsData(JsonObject limits) => LimitKeys.Any(k => limits[k] is { } n && Str(n) is not ("" or "-"));

    private static bool HasNonzeroLimits(JsonObject limits) => LimitKeys.Any(k => Math.Abs(Limit(limits[k])) > 0);

    private static JsonObject FormatLimits(JsonObject l)
    {
        var available = l.ContainsKey("tradingLimit") ? Limit(l["tradingLimit"]) : Limit(l["openingCashLimit"]);
        return new JsonObject
        {
            ["availablecash"] = BookFormatting.Fixed2(available), ["collateral"] = BookFormatting.Fixed2(Limit(l["collateralMargin"])), ["m2munrealized"] = "0.00", ["m2mrealized"] = "0.00",
            ["utiliseddebits"] = BookFormatting.Fixed2(Limit(l["utilizedMargin"])), ["openingcashlimit"] = BookFormatting.Fixed2(Limit(l["openingCashLimit"])), ["creditforsell"] = BookFormatting.Fixed2(Limit(l["creditForSell"])),
            ["adhocmargin"] = BookFormatting.Fixed2(Limit(l["adhocMargin"])), ["utilizedspanmargin"] = BookFormatting.Fixed2(Limit(l["utilizedSpanMargin"])), ["utilizedexposuremargin"] = BookFormatting.Fixed2(Limit(l["utilizedExposureMargin"])),
        };
    }

    private static double SumField(IEnumerable<JsonObject> rows, string field, string? fallbackField = null)
    {
        double total = 0;
        foreach (var row in rows)
        {
            var node = row[field];
            if ((node is null || Str(node) is "" or "-") && fallbackField is not null) node = row[fallbackField];
            total += Limit(node);
        }

        return total;
    }

    public async Task<JsonObject> GetFundsAsync(BrokerSession session, CancellationToken cancellationToken)
    {
        var pooled = await FetchLimitsAsync(session, "/limits", cancellationToken);
        var equity = await FetchLimitsAsync(session, "/limits/equity", cancellationToken);
        var fno = await FetchLimitsAsync(session, "/limits/fno", cancellationToken);
        var hasPooled = HasLimitsData(pooled);
        var hasAnySegment = HasLimitsData(equity) || HasLimitsData(fno);
        var segmentsNonzero = HasNonzeroLimits(equity) || HasNonzeroLimits(fno);

        // Pooled limits when they carry real values (segment answers can mirror them, so summing would count twice).
        if (HasNonzeroLimits(pooled)) return FormatLimits(pooled);
        if (hasAnySegment && (!hasPooled || segmentsNonzero))
        {
            JsonObject[] segments = [equity, fno];
            return FormatLimits(new JsonObject
            {
                ["tradingLimit"] = SumField(segments, "tradingLimit", "openingCashLimit"), ["openingCashLimit"] = SumField(segments, "openingCashLimit"), ["collateralMargin"] = SumField(segments, "collateralMargin"),
                ["utilizedMargin"] = SumField(segments, "utilizedMargin"), ["creditForSell"] = SumField(segments, "creditForSell"), ["adhocMargin"] = SumField(segments, "adhocMargin"),
                ["utilizedSpanMargin"] = SumField(segments, "utilizedSpanMargin"), ["utilizedExposureMargin"] = SumField(segments, "utilizedExposureMargin"),
            });
        }

        if (hasPooled) return FormatLimits(pooled);
        throw new BrokerException($"{DisplayName} did not send your funds. Check that you are signed in to {DisplayName}, then try again.", 502);
    }
}
