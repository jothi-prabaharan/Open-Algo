using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using EPTradeBot.Core.Brokers;
using static EPTradeBot.Infrastructure.Brokers.JsonRead;

namespace EPTradeBot.Infrastructure.Brokers.AliceBlue;

/// <summary>
/// AliceBlue (a3.aliceblueonline.com open-api): port of broker/aliceblue api/order_api.py, api/funds.py, api/rate_limiter.py, mapping/transform_data.py and mapping/order_data.py. The user session is the bearer token. Orders are not limited
/// by AliceBlue; every other call shares a quota of 1800 a quarter hour, of which 50 are left unspent. A place goes as a one-item list.
/// </summary>
[BrokerPlugin]
public sealed partial class AliceBluePlugin(IHttpClientFactory http, BrokerSessionFactory sessions, ISymbolMaster symbols, TimeProvider clock, IAliceBlueSockets? sockets = null) : IBrokerOrders, IBrokerPortfolio, IBrokerFunds
{
    private const string DefaultBaseUrl = "https://a3.aliceblueonline.com";
    private const string Display = "AliceBlue";
    private const int Window = 15 * 60;
    private const int Budget = 1800 - 50;
    private static readonly Lock QuotaGate = new();
    private static readonly Dictionary<string, Queue<DateTime>> Quota = [];

    public string Name => "aliceblue";
    public string DisplayName => Display;
    public BrokerCapability Capabilities => BrokerCapability.Orders | BrokerCapability.Portfolio | BrokerCapability.Funds | BrokerCapability.History | BrokerCapability.Quotes | BrokerCapability.Depth;
    public IReadOnlyList<string> RequiredKeys { get; } = ["ApiKey", "ApiSecret"];

    private HttpClient Client() => http.CreateClient(Name);

    /// <summary>One more request of the quota. A request the window has no room for is refused with the time to wait, where OpenAlgo holds the caller for up to a quarter of an hour.</summary>
    private void TakeQuota(BrokerSession session)
    {
        lock (QuotaGate)
        {
            // One window for each signed-in session: there is one session at a time, and a new sign-in starts a new allowance as AliceBlue's does.
            if (!Quota.TryGetValue(session.AuthToken, out var times)) Quota[session.AuthToken] = times = new Queue<DateTime>();
            var now = clock.GetUtcNow().UtcDateTime;
            while (times.Count > 0 && times.Peek() <= now.AddSeconds(-Window)) times.Dequeue();
            if (times.Count < Budget)
            {
                times.Enqueue(now);
                return;
            }

            var wait = (times.Peek().AddSeconds(Window) - now).TotalSeconds;
            var when = wait >= 120 ? $"about {(int)Math.Ceiling(wait / 60)} minutes" : $"about {Math.Max(1, (int)Math.Ceiling(wait))} seconds";
            throw new BrokerException($"{Display} allows 1800 market data and account requests every 15 minutes, and that allowance is used up for now. This request was not sent. Try again in {when}.", 429);
        }
    }

    /// <summary>One call; the status and the body as read (null when it was not JSON). A read takes a place in the quota, an order does not.</summary>
    private async Task<(int Status, JsonNode? Body)> SendAsync(BrokerSession session, HttpMethod method, string path, JsonNode? body, bool read, CancellationToken cancellationToken)
    {
        if (read) TakeQuota(session);
        using var request = new HttpRequestMessage(method, sessions.BaseUrl(Name, DefaultBaseUrl) + path);
        request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {session.AuthToken}");
        if (body is not null) request.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
        try
        {
            using var response = await Client().SendAsync(request, cancellationToken);
            return ((int)response.StatusCode, await ReadAsync(response, cancellationToken));
        }
        catch (HttpRequestException ex)
        {
            throw new BrokerException($"{Display} did not answer. Check your connection, then try again.", 502, ex);
        }
    }

    private static bool Ok(JsonNode? body) => body is JsonObject o && Str(o["status"]) == "Ok";

    /// <summary>AliceBlue's message with its codes in words, or the fallback when it sent none.</summary>
    private static string Reason(JsonNode? body, string fallback) => body is JsonObject o && Truthy(o["message"]) ? AliceBlueErrorCodes.Describe(Str(o["message"]) ?? fallback) : fallback;

    // ---- vocabulary ------------------------------------------------------------------------------------------

    private static string Product(string? product) => product switch { "CNC" => "LONGTERM", "NRML" => "NRML", "MIS" => "INTRADAY", _ => "INTRADAY" };

    private static string ShownProduct(string? product) => product switch
    {
        "LONGTERM" => "CNC", "NRML" => "NRML", "INTRADAY" => "MIS", "MTF" => "CNC", "CNC" => "CNC", "MIS" => "MIS", "DELIVERY" => "CNC", _ => "MIS",
    };

    private static string OrderType(string? type) => type switch { "MARKET" => "MARKET", "LIMIT" => "LIMIT", "SL" => "SL", "SL-M" => "SLM", _ => "MARKET" };

    private static string Text(decimal value) => value.ToString(CultureInfo.InvariantCulture);

    /// <summary>Python's str(float): a whole number carries ".0".</summary>
    private static string PyFloat(decimal value)
    {
        var text = Text(value);
        return text.Contains('.', StringComparison.Ordinal) ? text : text + ".0";
    }

    // ---- orders ----------------------------------------------------------------------------------------------

    public async Task<BrokerCallResult> PlaceAsync(BrokerSession session, BrokerOrder order, CancellationToken cancellationToken)
    {
        var info = await symbols.FindAsync(Name, order.Symbol, order.Exchange, cancellationToken);
        if (info is null) return BrokerCallResult.Refused(400, $"{order.Symbol} on {order.Exchange} is not in the {Display} master contract. Check the symbol, or download the master contract again.");
        var item = new JsonObject
        {
            ["exchange"] = order.Exchange, ["instrumentId"] = Instrument(info.Token), ["transactionType"] = order.Action.ToUpperInvariant(), ["quantity"] = order.Quantity, ["product"] = Product(order.Product),
            ["orderComplexity"] = "REGULAR", ["orderType"] = OrderType(order.PriceType), ["validity"] = "DAY", ["price"] = PyFloat(order.Price), ["slLegPrice"] = "", ["targetLegPrice"] = "",
            ["slTriggerPrice"] = PyFloat(order.TriggerPrice), ["disclosedQuantity"] = order.DisclosedQuantity.ToString(CultureInfo.InvariantCulture), ["marketProtectionPercent"] = "", ["deviceId"] = "",
            ["trailingSlAmount"] = "", ["apiOrderSource"] = "", ["algoId"] = "", ["orderTag"] = "openalgo",
        };

        (int Status, JsonNode? Body) reply;
        try
        {
            reply = await SendAsync(session, HttpMethod.Post, "/open-api/od/v1/orders/placeorder", new JsonArray(item), read: false, cancellationToken);
        }
        catch (BrokerException ex) when (ex.HttpStatus == 502)
        {
            return new BrokerCallResult(false, 502, $"The order was sent to {Display} but no reply came back, so it may have been placed. Check the order book on {Display} before trying again.", MayHaveBeenSent: true);
        }

        if (reply.Body is not JsonObject answer)
            return new BrokerCallResult(false, 502, $"{Display} could not confirm the order, so it may have been placed. Check the order book on {Display} before trying again.", MayHaveBeenSent: true);
        if (reply.Status >= 400 || !Ok(answer)) return BrokerCallResult.Refused(reply.Status >= 400 ? reply.Status : 400, Reason(answer, "Failed to place order"));
        var first = (answer["result"] as JsonArray)?.OfType<JsonObject>().FirstOrDefault();
        if (first is not null)
        {
            var itemStatus = Str(first["status"]) ?? "";
            var id = Str(first["brokerOrderId"]);
            if (itemStatus.Length > 0 && itemStatus != "Ok" && string.IsNullOrEmpty(id)) return BrokerCallResult.Refused(400, Reason(first, "Failed to place order"));
            if (id is { Length: > 0 }) return BrokerCallResult.Success(id);
        }

        return new BrokerCallResult(false, 502, $"{Display} accepted the order but sent no order number, so it cannot be followed. Check the order book on {Display} before trying again.", MayHaveBeenSent: true);
    }

    /// <summary>The instrument id as a whole number: 777.0 is 777.</summary>
    private static string Instrument(string token) => double.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? ((long)Math.Truncate(d)).ToString(CultureInfo.InvariantCulture) : token;

    public async Task<BrokerCallResult> ModifyAsync(BrokerSession session, BrokerOrderChange change, CancellationToken cancellationToken)
    {
        var body = new JsonObject
        {
            ["brokerOrderId"] = change.OrderId, ["quantity"] = change.Quantity, ["orderType"] = OrderType(change.PriceType), ["slTriggerPrice"] = PyFloat(change.TriggerPrice), ["price"] = PyFloat(change.Price), ["slLegPrice"] = "",
            ["trailingSlAmount"] = "", ["targetLegPrice"] = "", ["validity"] = "DAY", ["disclosedQuantity"] = change.DisclosedQuantity.ToString(CultureInfo.InvariantCulture), ["marketProtection"] = "", ["deviceId"] = "",
        };
        var reply = await SendAsync(session, HttpMethod.Post, "/open-api/od/v1/orders/modify", body, read: false, cancellationToken);
        if (reply.Status < 400 && Ok(reply.Body)) return BrokerCallResult.Success(ChangedId(reply.Body, change.OrderId));
        return BrokerCallResult.Refused(reply.Status >= 400 ? reply.Status : 400, Reason(reply.Body, "Failed to modify order"));
    }

    public async Task<BrokerCallResult> CancelAsync(BrokerSession session, string orderId, CancellationToken cancellationToken)
    {
        var reply = await SendAsync(session, HttpMethod.Post, "/open-api/od/v1/orders/cancel", new JsonObject { ["brokerOrderId"] = orderId }, read: false, cancellationToken);
        if (reply.Status < 400 && Ok(reply.Body)) return BrokerCallResult.Success(ChangedId(reply.Body, orderId));
        return BrokerCallResult.Refused(reply.Status >= 400 ? reply.Status : 400, Reason(reply.Body, "Failed to cancel order"));
    }

    /// <summary>The order number AliceBlue answers with, or the one that was asked about when it names none.</summary>
    private static string ChangedId(JsonNode? body, string asked) =>
        body is JsonObject o && o["result"] is JsonArray { Count: > 0 } list && list[0] is JsonObject first && first.ContainsKey("brokerOrderId") && Str(first["brokerOrderId"]) is { Length: > 0 } id ? id : asked;

    // ---- books -----------------------------------------------------------------------------------------------

    /// <summary>The rows of a book. An answer that says there is nothing is an empty book; one that says the read failed is an error, where OpenAlgo shows it as an empty book.</summary>
    private async Task<List<JsonObject>> ReadBookAsync(BrokerSession session, string path, string what, string[] noneWords, CancellationToken cancellationToken)
    {
        var (_, body) = await SendAsync(session, HttpMethod.Get, path, null, read: true, cancellationToken);
        if (Ok(body)) return (body!["result"] as JsonArray)?.OfType<JsonObject>().ToList() ?? [];
        var message = body is JsonObject o && Truthy(o["message"]) ? AliceBlueErrorCodes.Describe(Str(o["message"]) ?? "") : "";
        if (message.Length > 0 && (noneWords.Any(w => message.Contains(w, StringComparison.OrdinalIgnoreCase)) || message.Contains("not found", StringComparison.OrdinalIgnoreCase))) return [];
        throw new BrokerException($"{Display} could not send the {what}: {(message.Length > 0 ? message : "no reason was given.")}", 500);
    }

    private async Task<string?> SymbolOrNullAsync(string? brokerSymbol, string exchange, CancellationToken cancellationToken) =>
        !string.IsNullOrEmpty(brokerSymbol) && !string.IsNullOrEmpty(exchange) ? (await symbols.FindByBrokerSymbolAsync(Name, brokerSymbol, exchange, cancellationToken))?.Symbol : null;

    /// <summary>dict.get(key, other) chained: the first key the row has, even when its value is empty or null.</summary>
    private static JsonNode? First(JsonObject r, params string[] keys)
    {
        foreach (var key in keys)
        {
            if (r.ContainsKey(key)) return r[key];
        }

        return null;
    }

    private static string Shown(JsonNode? node, string fallback = "0") => node is null ? fallback : Str(node) ?? fallback;

    public async Task<BrokerBook> GetOrderBookAsync(BrokerSession session, CancellationToken cancellationToken)
    {
        int buy = 0, sell = 0, completed = 0, open = 0, rejected = 0;
        var result = new JsonArray();
        foreach (var r in await ReadBookAsync(session, "/open-api/od/v1/orders/book", "order book", ["no order"], cancellationToken))
        {
            var exchange = Str(r["exchange"]) ?? "";
            var raw = Str(First(r, "formattedInstrumentName", "tradingSymbol")) ?? "";
            var side = Str(r["transactionType"]) ?? "";
            var action = side == "BUY" ? "BUY" : side == "SELL" ? "SELL" : "UNKNOWN";
            if (action == "BUY") buy++;
            else if (action == "SELL") sell++;
            var status = (Str(r["orderStatus"]) ?? "").ToLowerInvariant();
            if (status == "complete") completed++;
            else if (status == "open") open++;
            else if (status == "rejected") rejected++;
            var type = Str(r["orderType"]) ?? "";
            result.Add(new JsonObject
            {
                ["symbol"] = raw.Length > 0 ? await SymbolOrNullAsync(raw, exchange, cancellationToken) : raw, ["exchange"] = exchange, ["action"] = action, ["quantity"] = Shown(r["quantity"]), ["price"] = Shown(r["price"]),
                ["trigger_price"] = Shown(r["slTriggerPrice"]), ["pricetype"] = type switch { "MARKET" => "MARKET", "LIMIT" => "LIMIT", "SL" => "SL", "SLM" => "SL-M", _ => "UNKNOWN" },
                ["product"] = ShownProduct(Str(r["product"])), ["orderid"] = Str(r["brokerOrderId"]) ?? "", ["order_status"] = status, ["timestamp"] = Str(r["orderTime"]) ?? "",
            });
        }

        return new BrokerBook(result, new JsonObject { ["total_buy_orders"] = buy, ["total_sell_orders"] = sell, ["total_completed_orders"] = completed, ["total_open_orders"] = open, ["total_rejected_orders"] = rejected });
    }

    /// <summary>Python's date.today() stands in for a time of day alone: today's date at the exchange, then the time.</summary>
    private string Stamp(string raw)
    {
        if (raw.Length == 0) return "";
        if (DateTime.TryParseExact(raw, "dd-MM-yyyy HH:mm:ss", CultureInfo.InvariantCulture, DateTimeStyles.None, out var full)) return full.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
        if (DateTime.TryParseExact(raw, "HH:mm:ss", CultureInfo.InvariantCulture, DateTimeStyles.None, out _)) return $"{clock.GetUtcNow().AddHours(5.5).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)} {raw}";
        return raw;
    }

    public async Task<JsonArray> GetTradeBookAsync(BrokerSession session, CancellationToken cancellationToken)
    {
        var result = new JsonArray();
        foreach (var r in await ReadBookAsync(session, "/open-api/od/v1/orders/trades", "trade book", ["no trade"], cancellationToken))
        {
            var exchange = Str(r["exchange"]) ?? "";
            var raw = Str(First(r, "formattedInstrumentName", "tradingSymbol")) ?? "";
            var quantity = Int(r["filledQuantity"]);
            var price = Float(r["tradedPrice"]);
            var side = Str(r["transactionType"]) ?? "";
            var stamp = Str(First(r, "fillTimestamp", "orderTime")) ?? "";
            result.Add(new JsonObject
            {
                ["symbol"] = raw.Length > 0 ? await SymbolOrNullAsync(raw, exchange, cancellationToken) ?? raw : raw, ["exchange"] = exchange, ["product"] = ShownProduct(Str(r["product"])),
                ["action"] = side == "BUY" ? "BUY" : side == "SELL" ? "SELL" : side, ["quantity"] = quantity, ["average_price"] = price, ["trade_value"] = quantity * price, ["orderid"] = Str(r["brokerOrderId"]) ?? "", ["timestamp"] = Stamp(stamp),
            });
        }

        return result;
    }

    public async Task<JsonArray> GetPositionsAsync(BrokerSession session, CancellationToken cancellationToken)
    {
        var result = new JsonArray();
        foreach (var r in await ReadBookAsync(session, "/open-api/od/v1/positions", "positions", ["no position"], cancellationToken))
        {
            var exchange = Str(r["exchange"]) ?? "";
            var raw = Str(First(r, "tradingSymbol", "formattedInstrumentName")) ?? "";
            var net = Float(r["netQuantity"]);
            var buyAverage = Float(First(r, "dayBuyPrice", "netAveragePrice"));
            var sellAverage = Float(r["daySellPrice"]);
            var average = net > 0 ? buyAverage : net < 0 ? sellAverage : 0;
            var ltp = Float(First(r, "ltp", "previousDayClose"));
            double pnl = 0, percentage = 0;
            if (net != 0 && average > 0 && ltp > 0)
            {
                pnl = net > 0 ? (ltp - average) * net : (average - ltp) * Math.Abs(net);
                percentage = pnl / (average * Math.Abs(net)) * 100;
            }

            var unrealised = Float(First(r, "unrealisedPnl", "unrealisedProfitLoss"));
            if (unrealised != 0)
            {
                pnl = unrealised;
                if (average > 0 && net != 0) percentage = pnl / (average * Math.Abs(net)) * 100;
            }

            var m2m = r.ContainsKey("mtm") || r.ContainsKey("markToMarket") ? Float(First(r, "mtm", "markToMarket")) : 0;
            result.Add(new JsonObject
            {
                ["symbol"] = raw.Length > 0 ? await SymbolOrNullAsync(raw, exchange, cancellationToken) ?? raw : raw, ["exchange"] = exchange, ["product"] = ShownProduct(Str(r["product"])), ["quantity"] = (long)Math.Truncate(net),
                ["average_price"] = BookFormatting.RoundPy(average, 2), ["ltp"] = ltp, ["pnl"] = BookFormatting.RoundPy(pnl, 2), ["pnlpercentage"] = BookFormatting.RoundPy(percentage, 2), ["m2m"] = BookFormatting.RoundPy(m2m, 2),
                ["buyqty"] = Int(First(r, "buyQuantity", "dayBuyQuantity")), ["sellqty"] = Int(First(r, "sellQuantity", "daySellQuantity")),
                ["netvalue"] = average > 0 ? BookFormatting.RoundPy(average * Math.Abs(net), 2) : 0, ["realised"] = BookFormatting.RoundPy(Float(First(r, "realizedPnl", "realisedPnl")), 2), ["unrealised"] = BookFormatting.RoundPy(unrealised, 2),
            });
        }

        return result;
    }

    public async Task<BrokerBook> GetHoldingsAsync(BrokerSession session, CancellationToken cancellationToken)
    {
        var holdings = new JsonArray();
        double value = 0, invested = 0;
        foreach (var h in await ReadBookAsync(session, "/open-api/od/v1/holdings/CNC", "holdings", ["no holding"], cancellationToken))
        {
            var nse = Str(h["nseTradingSymbol"]) ?? "";
            var bse = Str(h["bseTradingSymbol"]) ?? "";
            var exchange = nse.Length > 0 ? "NSE" : bse.Length > 0 ? "BSE" : "NSE";
            var held = (long)Math.Truncate(Float(First(h, "dpQuantity", "totalQuantity")));
            var unsettled = (long)Math.Truncate(Float(h["t1Quantity"]));
            var quantity = held > 0 ? held : unsettled;
            var ltp = Float(h["ltp"]);
            var price = Float(First(h, "averageTradedPrice", "investedPrice"));
            value += ltp * quantity;
            invested += price * quantity;
            var symbol = exchange == "NSE" ? nse : bse;
            if (symbol.Length == 0) continue;
            holdings.Add(new JsonObject
            {
                ["symbol"] = await SymbolOrNullAsync(symbol, exchange, cancellationToken) ?? symbol, ["exchange"] = exchange, ["quantity"] = quantity, ["product"] = "CNC", ["average_price"] = BookFormatting.RoundPy(price, 2),
                ["ltp"] = BookFormatting.RoundPy(ltp, 2), ["pnl"] = quantity != 0 ? BookFormatting.RoundPy((ltp - price) * quantity, 2) : 0.0, ["pnlpercent"] = price != 0 ? BookFormatting.RoundPy((ltp - price) / price * 100, 2) : 0.0,
            });
        }

        var profit = value - invested;
        return new BrokerBook(holdings, new JsonObject { ["totalholdingvalue"] = value, ["totalinvvalue"] = invested, ["totalprofitandloss"] = profit, ["totalpnlpercentage"] = invested != 0 ? profit / invested * 100 : 0.0 });
    }

    // ---- funds -----------------------------------------------------------------------------------------------

    private static string Money(JsonObject d, string name)
    {
        if (!d.ContainsKey(name)) return "0.00";
        return Number(d[name]) is { } n ? BookFormatting.Fixed2(n) : throw new BrokerException($"{Display} could not send the funds: a figure was not a number.", 500);
    }

    private static double? Number(JsonNode? node) => BookFormatting.Number(node) is { } n ? n : Str(node) is { } s && double.TryParse(s.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : null;

    public async Task<JsonObject> GetFundsAsync(BrokerSession session, CancellationToken cancellationToken)
    {
        var (status, body) = await SendAsync(session, HttpMethod.Get, "/open-api/od/v1/limits/", null, read: true, cancellationToken);
        if (status >= 400 || !Ok(body)) throw new BrokerException($"{Display} could not send the funds: {Reason(body, "no reason was given.")}", 500);
        var item = (body!["result"] as JsonArray)?.OfType<JsonObject>().FirstOrDefault();
        if (item is null) return new JsonObject { ["availablecash"] = "0.00", ["collateral"] = "0.00", ["m2munrealized"] = "0.00", ["m2mrealized"] = "0.00", ["utiliseddebits"] = "0.00" };
        var cash = Money(item, "tradingLimit");
        var collateral = Money(item, "collateralMargin");
        var used = Money(item, "utilizedMargin");
        return new JsonObject { ["availablecash"] = cash, ["collateral"] = collateral, ["m2munrealized"] = "0.00", ["m2mrealized"] = BookFormatting.Fixed2(await RealisedAsync(session, cancellationToken)), ["utiliseddebits"] = used };
    }

    /// <summary>The realised profit and loss of every position: AliceBlue's positions carry no last price, so there is no unrealised figure. A positions read that fails leaves it at zero.</summary>
    private async Task<double> RealisedAsync(BrokerSession session, CancellationToken cancellationToken)
    {
        try
        {
            var (status, body) = await SendAsync(session, HttpMethod.Get, "/open-api/od/v1/positions", null, read: true, cancellationToken);
            if (status >= 400 || !Ok(body)) return 0;
            return (body!["result"] as JsonArray)?.OfType<JsonObject>().Sum(p => Truthy(p["realizedPnl"]) ? Float(p["realizedPnl"]) : 0) ?? 0;
        }
        catch (BrokerException)
        {
            return 0;
        }
    }
}
