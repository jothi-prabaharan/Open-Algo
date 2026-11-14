using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using EPTradeBot.Core.Brokers;
using EPTradeBot.Core.Brokers.Gtt;
using Microsoft.Extensions.Logging;
using static EPTradeBot.Infrastructure.Brokers.JsonRead;

namespace EPTradeBot.Infrastructure.Brokers.HdfcSky;

/// <summary>
/// HDFC Sky (developer.hdfcsky.com). Port of broker/hdfcsky: api/order_api.py, api/funds.py, mapping/transform_data.py and mapping/order_data.py. The access token goes in Authorization with no prefix,
/// the api_key and the client id (read from the token's sub claim) in the query, and a user agent is mandatory. An instrument is addressed by its token. HDFC Sky refuses market-type orders over the
/// API, so a market order is sent as a limit order a protection slab beyond the last price, and a stop-market as a stop-limit beyond its trigger. The order book is two calls (pending and completed).
/// </summary>
[BrokerPlugin]
public sealed partial class HdfcSkyPlugin(IHttpClientFactory http, BrokerSessionFactory sessions, ISymbolMaster symbols, TimeProvider clock, ILogger<HdfcSkyPlugin> logger) : IBrokerOrders, IBrokerPortfolio, IBrokerFunds, IBrokerMultiQuotes
{
    private const string DefaultBaseUrl = "https://developer.hdfcsky.com";
    private const string UserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/123.0.0.0 Safari/537.36";
    private const int LtpBatch = 10;

    public string Name => "hdfcsky";
    public string DisplayName => "HDFC Sky";
    public BrokerCapability Capabilities => BrokerCapability.Orders | BrokerCapability.Portfolio | BrokerCapability.Funds | BrokerCapability.Quotes | BrokerCapability.Depth;
    public IReadOnlyList<string> RequiredKeys { get; } = ["ApiKey", "ApiSecret"];

    // ---- vocabulary ------------------------------------------------------------------------------------------

    private static string RestExchange(string exchange) => exchange switch { "NSE_INDEX" => "NSE", "BSE_INDEX" => "BSE", _ => exchange };

    private static string LtpExchange(string exchange) => exchange is "NSE_INDEX" or "BSE_INDEX" ? exchange : RestExchange(exchange);

    private static string OaExchange(string? restExchange) => (restExchange ?? "").ToUpperInvariant() switch
    {
        "NSE" => "NSE", "BSE" => "BSE", "NFO" => "NFO", "BFO" => "BFO", "CDS" => "CDS", "NCD" => "CDS", "MCX" => "MCX", var other => restExchange is null ? "" : restExchange,
    };

    private static string OrderType(string? priceType) => (priceType ?? "").ToUpperInvariant() switch { "MARKET" => "MARKET", "LIMIT" => "LIMIT", "SL" => "SL", "SL-M" => "SLM", _ => "MARKET" };

    private static string ReverseOrderType(string? type) => (type ?? "").ToUpperInvariant() switch { "MARKET" => "MARKET", "LIMIT" => "LIMIT", "SL" => "SL", "SLM" or "SL-M" => "SL-M", _ => type ?? "" };

    private static string Product(string? product) => (product ?? "").ToUpperInvariant() switch { "CNC" => "CNC", "NRML" => "NRML", "MIS" => "MIS", _ => "MIS" };

    private static string? ReverseProduct(string? product) => (product ?? "").ToUpperInvariant() switch { "CNC" => "CNC", "NRML" => "NRML", "MIS" => "MIS", "MTF" => "CNC", _ => null };

    private static readonly Dictionary<string, string> StatusMap = new()
    {
        ["COMPLETE"] = "complete", ["REJECTED"] = "rejected", ["MODIFY_REJECTED"] = "rejected", ["CANCEL_REJECTED"] = "rejected", ["BRACKET_ORDER_REJECTED"] = "rejected",
        ["CANCELLED"] = "cancelled", ["CANCEL_CONFIRMED"] = "cancelled", ["BATCH_CANCEL_CONFIRMED"] = "cancelled", ["AMO_CANCEL_CONFIRMED"] = "cancelled", ["BRACKET_ORDER_CANCELLED"] = "cancelled",
        ["SL_TRIGGER_CONFIRMED"] = "trigger pending", ["TRIGGER_PENDING"] = "trigger pending",
        ["ACCEPTED"] = "open", ["CONFIRMED"] = "open", ["PENDING"] = "open", ["MODIFY_ACCEPTED"] = "open", ["MODIFY_CONFIRMED"] = "open", ["MODIFY_PENDING"] = "open", ["CANCEL_ACCEPTED"] = "open",
        ["CANCEL_PENDING"] = "open", ["PARTIAL_TRADE"] = "open", ["AMO_REQ_RECEIVED"] = "open", ["AMO_REQ_CONFIRMED"] = "open", ["AMO_REQ_MODIFIED"] = "open", ["AMO_NEW_CONFIRMED"] = "open",
        ["AMO_MODIFY_CONFIRMED"] = "open", ["UNACCEPTED"] = "open", ["EXCHANGE_RESPONSE_PENDING"] = "open", ["RRM_PENDING_AT_EXCHANGE"] = "open", ["RMS_VALIDATION_COMPLETED"] = "open",
    };

    private static string MapStatus(string? status) => StatusMap.TryGetValue((status ?? "None").ToUpperInvariant(), out var mapped) ? mapped : (status ?? "None").ToLowerInvariant();

    /// <summary>The status as the order statistics read it: a raw (all upper-case) broker status is mapped, anything else is only lower-cased.</summary>
    private static string StatisticsStatus(string? status)
    {
        var text = status ?? "None";
        var cased = text.Any(char.IsLetter);
        return cased && text == text.ToUpperInvariant() ? MapStatus(text) : text.ToLowerInvariant();
    }

    private static double Flt(JsonNode? node, double fallback = 0.0)
    {
        if (BookFormatting.Number(node) is { } n) return n;
        return Str(node) is { } s && double.TryParse(s.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : fallback;
    }

    private static long Whole(JsonNode? node) => (long)Math.Truncate(Flt(node));

    private static JsonNode? Same(JsonNode? node) => node?.DeepClone();

    // ---- calls -----------------------------------------------------------------------------------------------

    /// <summary>The client id: the sub (or client_id) claim of the access token, else the one configured.</summary>
    private static string ClientId(BrokerSession session)
    {
        try
        {
            var parts = session.AuthToken.Split('.');
            if (parts.Length >= 2)
            {
                var segment = parts[1].Replace('-', '+').Replace('_', '/');
                segment = segment.PadRight(segment.Length + ((4 - (segment.Length % 4)) % 4), '=');
                if (JsonNode.Parse(Convert.FromBase64String(segment)) is JsonObject claims)
                {
                    var id = Truthy(claims["sub"]) ? Str(claims["sub"]) : Str(claims["client_id"]);
                    if (!string.IsNullOrEmpty(id)) return id;
                }
            }
        }
        catch (Exception ex) when (ex is FormatException or System.Text.Json.JsonException or ArgumentException)
        {
            // Not a token with readable claims: fall back to the configured client id.
        }

        return session.Key("ClientId") ?? session.ClientId ?? "";
    }

    private HttpClient Client() => http.CreateClient(Name);

    private async Task<(int Status, JsonNode? Body)> CallAsync(BrokerSession session, HttpMethod method, string path, IReadOnlyDictionary<string, string>? query, JsonNode? body, bool withClientId, CancellationToken cancellationToken)
    {
        var parts = new List<string> { "api_key=" + Uri.EscapeDataString(session.Key("ApiKey") ?? "") };
        if (withClientId && ClientId(session) is { Length: > 0 } cid) parts.Add("client_id=" + Uri.EscapeDataString(cid));
        foreach (var (k, v) in query ?? new Dictionary<string, string>()) parts.Add($"{Uri.EscapeDataString(k)}={Uri.EscapeDataString(v)}");
        using var request = new HttpRequestMessage(method, sessions.BaseUrl(Name, DefaultBaseUrl) + path + "?" + string.Join("&", parts));
        if (!string.IsNullOrEmpty(session.AuthToken)) request.Headers.TryAddWithoutValidation("Authorization", session.AuthToken);
        request.Headers.TryAddWithoutValidation("User-Agent", UserAgent);
        request.Headers.TryAddWithoutValidation("Accept", "application/json");
        if (body is not null) request.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
        try
        {
            using var response = await Client().SendAsync(request, cancellationToken);
            return ((int)response.StatusCode, await JsonRead.ReadAsync(response, cancellationToken));
        }
        catch (HttpRequestException ex)
        {
            throw new BrokerException($"{DisplayName} did not answer. Check your connection, then try again.", 502, ex);
        }
    }

    private static string Message(JsonNode? body, string fallback) => Str((body as JsonObject)?["message"]) is { Length: > 0 } m ? m : fallback;

    /// <summary>A read that must be answered: anything that is not a JSON object is a refusal a trader can read. An expired session answers with an error body, which is a refusal here, never an empty book.</summary>
    private async Task<JsonObject> ReadAsync(BrokerSession session, string path, IReadOnlyDictionary<string, string>? query, string what, CancellationToken cancellationToken)
    {
        var (status, body) = await CallAsync(session, HttpMethod.Get, path, query, null, true, cancellationToken);
        if (body is not JsonObject payload || status >= 400 || Str(payload["status"]) == "error" || Truthy(payload["error"]))
        {
            var reason = body is JsonObject o ? (Str(o["message"]) is { Length: > 0 } m ? m : Str(o["error"])) : null;
            throw new BrokerException($"{DisplayName} could not send the {what}: {reason ?? "no reason was given."}", 500);
        }

        return payload;
    }

    // ---- orders ----------------------------------------------------------------------------------------------

    private static long lastReference;

    /// <summary>A caller-side order number: the millisecond clock cut to nine digits, never repeating within the process.</summary>
    private static long UserOrderId()
    {
        long value, last;
        do
        {
            last = Interlocked.Read(ref lastReference);
            value = Math.Max(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), last + 1);
        }
        while (Interlocked.CompareExchange(ref lastReference, value, last) != last);
        return value % 1_000_000_000;
    }

    /// <summary>The order type and price an order is sent with. A market order is priced off the last price and a stop-market off its trigger, with the protection slab; with no usable price it goes as it was asked for.</summary>
    private async Task<(string Type, double Price)> ProtectAsync(BrokerSession session, SymbolRecord instrument, BrokerOrder order, string action, CancellationToken cancellationToken)
    {
        var fallback = (double)order.Price;
        var type = OrderType(order.PriceType);
        if (order.PriceType is not ("MARKET" or "SL-M")) return (type, fallback);
        var limitType = order.PriceType == "SL-M" ? "SL" : "LIMIT";
        try
        {
            double basePrice;
            if (order.PriceType == "SL-M") basePrice = (double)order.TriggerPrice;
            else basePrice = (await FetchLtpAsync(session, [(LtpExchange(instrument.Exchange), instrument.Token)], cancellationToken)).GetValueOrDefault((LtpExchange(instrument.Exchange), instrument.Token)).Ltp;
            if (basePrice <= 0)
            {
                logger.LogWarning("{Broker} has no reference price for {Exchange}:{Symbol}; the {Type} order goes as it is.", DisplayName, order.Exchange, order.Symbol, order.PriceType);
                return (type, fallback);
            }

            var tick = instrument.TickSize is { } t ? (double?)t : null;
            return (OrderType(limitType), MarketPriceProtection.ProtectedPrice(basePrice, action, MarketPriceProtection.InstrumentType(null, order.Symbol), tick));
        }
        catch (BrokerException ex)
        {
            logger.LogWarning(ex, "{Broker} could not protect the {Type} order for {Exchange}:{Symbol}; it goes as it is.", DisplayName, order.PriceType, order.Exchange, order.Symbol);
            return (type, fallback);
        }
    }

    public async Task<BrokerCallResult> PlaceAsync(BrokerSession session, BrokerOrder order, CancellationToken cancellationToken)
    {
        var info = await symbols.FindAsync(Name, order.Symbol, order.Exchange, cancellationToken);
        if (info is null) return BrokerCallResult.Refused(400, $"{order.Symbol} on {order.Exchange} is not in the {DisplayName} master contract. Check the symbol, or download the master contract again.");
        var action = order.Action.Equals("BUY", StringComparison.OrdinalIgnoreCase) ? "BUY" : "SELL";
        var (type, price) = await ProtectAsync(session, info, order, action, cancellationToken);

        var payload = new JsonObject
        {
            ["exchange"] = RestExchange(order.Exchange), ["instrument_token"] = info.Token, ["client_id"] = ClientId(session), ["order_type"] = type, ["order_side"] = action, ["product"] = Product(order.Product),
            ["quantity"] = order.Quantity, ["price"] = price, ["trigger_price"] = (double)order.TriggerPrice, ["disclosed_quantity"] = order.DisclosedQuantity, ["validity"] = "DAY", ["device"] = "WEB",
            ["execution_type"] = "REGULAR", ["amo"] = false, ["user_order_id"] = UserOrderId(),
        };
        var strategy = (order.Strategy ?? "").Trim();
        if (strategy.Length > 0) payload["tags"] = new JsonArray(strategy.Length > 32 ? strategy[..32] : strategy);

        (int Status, JsonNode? Body) reply;
        try
        {
            reply = await CallAsync(session, HttpMethod.Post, "/oapi/v1/orders", null, payload, false, cancellationToken);
        }
        catch (BrokerException ex) when (ex.HttpStatus == 502)
        {
            return new BrokerCallResult(false, 502, $"The order was sent to {DisplayName} but no reply came back, so it may have been placed. Check the order book on {DisplayName} before trying again.", MayHaveBeenSent: true);
        }

        var body = reply.Body as JsonObject;
        if (Str(body?["status"]) == "success" && Str((body?["data"] as JsonObject)?["oms_order_id"]) is { Length: > 0 } id) return BrokerCallResult.Success(id);
        return BrokerCallResult.Refused(reply.Status >= 400 ? reply.Status : 400, Message(body, "Failed to place order"));
    }

    public async Task<BrokerCallResult> ModifyAsync(BrokerSession session, BrokerOrderChange change, CancellationToken cancellationToken)
    {
        var info = await symbols.FindAsync(Name, change.Symbol, change.Exchange, cancellationToken);
        if (info is null) return BrokerCallResult.Refused(400, $"{change.Symbol} on {change.Exchange} is not in the {DisplayName} master contract. Check the symbol, or download the master contract again.");
        var payload = new JsonObject
        {
            ["exchange"] = RestExchange(change.Exchange), ["instrument_token"] = info.Token, ["client_id"] = ClientId(session), ["oms_order_id"] = change.OrderId, ["order_type"] = OrderType(change.PriceType),
            ["product"] = Product(change.Product), ["quantity"] = change.Quantity, ["price"] = (double)change.Price, ["trigger_price"] = (double)change.TriggerPrice, ["disclosed_quantity"] = change.DisclosedQuantity,
            ["validity"] = "DAY", ["execution_type"] = "REGULAR",
        };
        var (_, body) = await CallAsync(session, HttpMethod.Put, "/oapi/v1/orders", null, payload, false, cancellationToken);
        var reply = body as JsonObject;
        if (Str(reply?["status"]) == "success") return BrokerCallResult.Success(Str((reply?["data"] as JsonObject)?["oms_order_id"]) is { Length: > 0 } id ? id : change.OrderId);
        return BrokerCallResult.Refused(400, Message(reply, "Failed to modify order"));
    }

    public async Task<BrokerCallResult> CancelAsync(BrokerSession session, string orderId, CancellationToken cancellationToken)
    {
        var (_, body) = await CallAsync(session, HttpMethod.Delete, $"/oapi/v1/orders/{Uri.EscapeDataString(orderId)}", new Dictionary<string, string> { ["execution_type"] = "REGULAR" }, null, true, cancellationToken);
        var reply = body as JsonObject;
        if (Str(reply?["status"]) == "success") return BrokerCallResult.Success(orderId);
        return BrokerCallResult.Refused(400, Message(reply, "Failed to cancel order"));
    }

    // ---- symbols ---------------------------------------------------------------------------------------------

    /// <summary>The OpenAlgo symbol of a broker trading symbol, or the broker's own when the master contract does not list it.</summary>
    private async Task<string> OaSymbolAsync(string? brokerSymbol, string exchange, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(brokerSymbol)) return "";
        return (await symbols.FindByBrokerSymbolAsync(Name, brokerSymbol, exchange, cancellationToken))?.Symbol is { Length: > 0 } resolved ? resolved : brokerSymbol;
    }

    // ---- books -----------------------------------------------------------------------------------------------

    private static List<JsonObject> Rows(JsonObject body, string key) => body["data"] switch
    {
        JsonArray list => list.OfType<JsonObject>().ToList(),
        JsonObject data => (data[key] as JsonArray ?? []).OfType<JsonObject>().ToList(),
        _ => [],
    };

    public async Task<BrokerBook> GetOrderBookAsync(BrokerSession session, CancellationToken cancellationToken)
    {
        var raw = new List<JsonObject>();
        foreach (var half in new[] { "pending", "completed" })
        {
            try
            {
                var (_, body) = await CallAsync(session, HttpMethod.Get, "/oapi/v1/orders", new Dictionary<string, string> { ["type"] = half }, null, true, cancellationToken);
                if (body is not JsonObject payload)
                {
                    logger.LogWarning("{Broker} sent no readable {Half} order book; it is left out.", DisplayName, half);
                    continue;
                }

                if (Str(payload["status"]) == "error")
                {
                    logger.LogWarning("{Broker} {Half} order book error: {Message}", DisplayName, half, Str(payload["message"]));
                    continue;
                }

                if (payload["data"] is JsonObject data) raw.AddRange((data["orders"] as JsonArray ?? []).OfType<JsonObject>());
            }
            catch (BrokerException ex)
            {
                logger.LogWarning(ex, "{Broker} {Half} order book could not be read; it is left out.", DisplayName, half);
            }
        }

        int buy = 0, sell = 0, completed = 0, open = 0, rejected = 0;
        var rows = new JsonArray();
        foreach (var o in raw)
        {
            var exchange = OaExchange(Str(o["exchange"]));
            var symbol = await OaSymbolAsync(Str(o["trading_symbol"]), exchange, cancellationToken);
            var side = (Str(o["order_side"]) ?? (o.ContainsKey("order_side") ? "None" : "")).ToUpperInvariant();
            var rawStatus = o.ContainsKey("order_status") ? Str(o["order_status"]) ?? "None" : "";
            if (side == "BUY") buy++;
            else if (side == "SELL") sell++;
            var counted = StatisticsStatus(rawStatus);
            if (counted == "complete") completed++;
            else if (counted is "open" or "trigger pending") open++;
            else if (counted == "rejected") rejected++;
            JsonNode? product = ReverseProduct(Str(o["product"])) is { } p ? JsonValue.Create(p) : Same(o["product"]);
            var stamp = Truthy(o["order_entry_time"]) ? Same(o["order_entry_time"]) : Truthy(o["exchange_time"]) ? Same(o["exchange_time"]) : "";
            rows.Add(new JsonObject
            {
                ["symbol"] = symbol, ["exchange"] = exchange, ["action"] = side, ["quantity"] = Whole(o["quantity"]), ["price"] = Flt(o["price"]), ["trigger_price"] = Flt(o["trigger_price"]),
                ["pricetype"] = ReverseOrderType(Str(o["order_type"])), ["product"] = product ?? "", ["orderid"] = Str(o["oms_order_id"]) ?? "", ["order_status"] = MapStatus(rawStatus), ["timestamp"] = stamp,
            });
        }

        return new BrokerBook(rows, new JsonObject
        {
            ["total_buy_orders"] = buy, ["total_sell_orders"] = sell, ["total_completed_orders"] = completed, ["total_open_orders"] = open, ["total_rejected_orders"] = rejected,
        });
    }

    public async Task<JsonArray> GetTradeBookAsync(BrokerSession session, CancellationToken cancellationToken)
    {
        var result = new JsonArray();
        var (_, body) = await CallAsync(session, HttpMethod.Get, "/oapi/v1/trades", null, null, true, cancellationToken);
        if (body is not JsonObject payload) throw new BrokerException($"{DisplayName} could not send the trade book: no reason was given.", 500);
        if (Str(payload["status"]) == "error" || Truthy(payload["error"])) throw new BrokerException($"{DisplayName} could not send the trade book: {(Str(payload["message"]) is { Length: > 0 } m ? m : Str(payload["error"]))}", 500);
        foreach (var t in Rows(payload, "trades"))
        {
            var exchange = OaExchange(Str(t["exchange"]));
            var symbol = await OaSymbolAsync(Str(t["trading_symbol"]), exchange, cancellationToken);
            var quantity = Whole(t.ContainsKey("trade_quantity") ? t["trade_quantity"] : t["filled_quantity"]);
            var average = Flt(t.ContainsKey("trade_price") ? t["trade_price"] : t["order_price"]);
            JsonNode? product = ReverseProduct(Str(t["product"])) is { } p ? JsonValue.Create(p) : Same(t["product"]);
            result.Add(new JsonObject
            {
                ["symbol"] = symbol, ["exchange"] = exchange, ["product"] = product ?? "", ["action"] = (Str(t["order_side"]) ?? "").ToUpperInvariant(), ["quantity"] = quantity, ["average_price"] = average,
                ["trade_value"] = BookFormatting.Round2(quantity * average), ["orderid"] = Str(t["oms_order_id"]) ?? "",
                ["timestamp"] = Truthy(t["trade_time"]) ? Same(t["trade_time"]) : Truthy(t["exchange_time"]) ? Same(t["exchange_time"]) : "",
            });
        }

        return result;
    }

    public async Task<JsonArray> GetPositionsAsync(BrokerSession session, CancellationToken cancellationToken)
    {
        var (_, body) = await CallAsync(session, HttpMethod.Get, "/oapi/v1/positions", new Dictionary<string, string> { ["type"] = "historical" }, null, true, cancellationToken);
        if (body is not JsonObject payload) throw new BrokerException($"{DisplayName} could not send the positions: no reason was given.", 500);
        if (Str(payload["status"]) == "error" || Truthy(payload["error"])) throw new BrokerException($"{DisplayName} could not send the positions: {(Str(payload["message"]) is { Length: > 0 } m ? m : Str(payload["error"]))}", 500);
        var result = new JsonArray();
        foreach (var p in Rows(payload, "positions"))
        {
            var exchange = OaExchange(Str(p["exchange"]));
            var symbol = await OaSymbolAsync(Str(p["trading_symbol"]), exchange, cancellationToken);
            JsonNode? product = ReverseProduct(Str(p["product"])) is { } rp ? JsonValue.Create(rp) : Same(p["product"]);
            var net = Whole(p["net_quantity"]);
            var multiplier = Flt(p["multiplier"], 1.0);
            if (multiplier == 0) multiplier = 1.0;
            var ltp = Flt(p["ltp"]);
            var pnl = (Flt(p["sell_amount"]) - Flt(p["buy_amount"])) + (net * ltp * multiplier);
            var average = net > 0 ? Flt(p["average_buy_price"]) : net < 0 ? Flt(p["average_sell_price"]) : Flt(p["average_price"]);
            result.Add(new JsonObject
            {
                ["symbol"] = symbol, ["exchange"] = exchange, ["product"] = product ?? "", ["quantity"] = net, ["pnl"] = BookFormatting.Round2(pnl), ["average_price"] = BookFormatting.Fixed2(average), ["ltp"] = BookFormatting.Round2(ltp),
            });
        }

        return result;
    }

    public async Task<BrokerBook> GetHoldingsAsync(BrokerSession session, CancellationToken cancellationToken)
    {
        var (_, body) = await CallAsync(session, HttpMethod.Get, "/oapi/v1/holdings", null, null, true, cancellationToken);
        if (body is not JsonObject payload) throw new BrokerException($"{DisplayName} could not send the holdings: no reason was given.", 500);
        if (Str(payload["status"]) == "error" || Truthy(payload["error"])) throw new BrokerException($"{DisplayName} could not send the holdings: {(Str(payload["message"]) is { Length: > 0 } m ? m : Str(payload["error"]))}", 500);
        var holdings = new JsonArray();
        double value = 0, invested = 0;
        foreach (var h in Rows(payload, "holdings"))
        {
            var exchange = OaExchange(Str(h["exchange"]));
            var details = h["instrument_details"] as JsonObject;
            var brokerSymbol = Truthy(details?["trading_symbol"]) ? Str(details?["trading_symbol"]) : Str(h["trading_symbol"]);
            var symbol = await OaSymbolAsync(brokerSymbol, exchange, cancellationToken);
            var quantity = Whole(h["quantity"]);
            var average = Flt(h["buy_avg"]);
            var ltp = Flt(h["ltp"]);
            value += ltp * quantity;
            invested += average * quantity;
            holdings.Add(new JsonObject
            {
                ["symbol"] = symbol, ["exchange"] = exchange, ["quantity"] = quantity, ["product"] = "CNC", ["average_price"] = BookFormatting.Round2(average),
                ["pnl"] = BookFormatting.Round2((ltp - average) * quantity), ["pnlpercent"] = average != 0 ? BookFormatting.Round2((ltp - average) / average * 100) : 0.0,
            });
        }

        var total = value - invested;
        return new BrokerBook(holdings, new JsonObject
        {
            ["totalholdingvalue"] = value, ["totalinvvalue"] = invested, ["totalprofitandloss"] = total, ["totalpnlpercentage"] = invested != 0 ? total / invested * 100 : 0.0,
        });
    }

    // ---- funds -----------------------------------------------------------------------------------------------

    private static readonly Dictionary<string, string> FundsLabels = new()
    {
        ["availablemargin"] = "availablecash", ["marginused"] = "utiliseddebits", ["pledgebenefit"] = "collateral", ["realizedmtm"] = "m2mrealized", ["unrealizedmtm"] = "m2munrealized",
    };

    private static double Money(JsonNode? node) => double.TryParse((Str(node) ?? "").Replace(",", "").Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : 0.0;

    public async Task<JsonObject> GetFundsAsync(BrokerSession session, CancellationToken cancellationToken)
    {
        var payload = await ReadAsync(session, "/oapi/v1/funds/view", new Dictionary<string, string> { ["type"] = "all" }, "funds", cancellationToken);
        var data = payload["data"] as JsonObject ?? [];
        if (data["values"] is not JsonArray values && data["values"] is not null) throw new BrokerException($"{DisplayName} sent funds EP Trade bot could not read. Try again in a moment.", 502);
        var processed = new Dictionary<string, string>
        {
            ["availablecash"] = "0.00", ["collateral"] = "0.00", ["m2munrealized"] = "0.00", ["m2mrealized"] = "0.00", ["utiliseddebits"] = "0.00",
        };
        foreach (var row in data["values"] as JsonArray ?? [])
        {
            (JsonNode? Label, JsonNode? Value)? pair = row switch
            {
                JsonObject o => (o["0"], o["1"]),
                JsonArray { Count: >= 2 } a => (a[0], a[1]),
                _ => null,
            };
            if (pair is not { } p) continue;
            var label = string.Concat((Str(p.Label) ?? "").ToLowerInvariant().Where(c => !char.IsWhiteSpace(c)));
            if (FundsLabels.TryGetValue(label, out var key)) processed[key] = BookFormatting.Fixed2(Money(p.Value));
        }

        foreach (var (field, key) in new[] { ("realized_mtm", "m2mrealized"), ("unrealized_mtm", "m2munrealized") })
        {
            if (data[field] is { } named) processed[key] = BookFormatting.Fixed2(Money(named));
        }

        return new JsonObject { ["availablecash"] = processed["availablecash"], ["collateral"] = processed["collateral"], ["m2munrealized"] = processed["m2munrealized"], ["m2mrealized"] = processed["m2mrealized"], ["utiliseddebits"] = processed["utiliseddebits"] };
    }

    // ---- last prices -----------------------------------------------------------------------------------------

    /// <summary>The last price and previous close of each instrument from /fetch-ltp (PUT, ten at a time), retrying a rate limit; a call that fails gives nothing.</summary>
    private async Task<Dictionary<(string Exchange, string Token), (double Ltp, double PrevClose)>> FetchLtpAsync(BrokerSession session, List<(string Exchange, string Token)> instruments, CancellationToken cancellationToken)
    {
        var result = new Dictionary<(string, string), (double, double)>();
        if (instruments.Count == 0) return result;
        var body = new JsonObject { ["data"] = new JsonArray(instruments.Select(i => (JsonNode?)new JsonObject { ["exchange"] = i.Exchange, ["token"] = i.Token }).ToArray()) };
        (int Status, JsonNode? Body) reply = (0, null);
        try
        {
            for (var attempt = 0; attempt < 4; attempt++)
            {
                reply = await CallAsync(session, HttpMethod.Put, "/oapi/v1/fetch-ltp", null, body, false, cancellationToken);
                // A rate-limited batch is tried again rather than dropped: no price would read as a price of zero.
                if (reply.Status == 429 && attempt < 3)
                {
                    await Task.Delay(sessions.RetryBackoff(Name, TimeSpan.FromSeconds(0.5 * (1 << attempt))), cancellationToken);
                    continue;
                }

                break;
            }
        }
        catch (BrokerException)
        {
            return result;
        }

        if (reply.Status != 200 || reply.Body is not JsonObject payload) return result;
        foreach (var row in (payload["data"] as JsonArray ?? []).OfType<JsonObject>())
        {
            result[((Str(row["exchange"]) ?? "").ToUpperInvariant(), Str(row["token"]) ?? "")] = (Flt(row["ltp"]), Flt(row["prev_close"]));
        }

        return result;
    }
}
