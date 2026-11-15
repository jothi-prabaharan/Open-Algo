using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using EPTradeBot.Core.Brokers;
using static EPTradeBot.Infrastructure.Brokers.JsonRead;

namespace EPTradeBot.Infrastructure.Brokers.Definedge;

/// <summary>
/// Definedge Securities (integrate.definedgesecurities.com/dart/v1): port of broker/definedge api/order_api.py, api/funds.py, api/rate_limiter.py, mapping/transform_data.py and mapping/order_data.py. The session is the API session
/// key, the user token and the API token joined by three colons, and the session key is the Authorization header. Calls are a tenth of a second apart, and a 429 is retried after a second, two and four (or as long as a Retry-After says).
/// An order carries the exchange's generic algo id (99999 on the NSE segments, 9999999999999999 on the BSE ones).
/// </summary>
[BrokerPlugin]
public sealed partial class DefinedgePlugin(IHttpClientFactory http, BrokerSessionFactory sessions, ISymbolMaster symbols, TimeProvider clock) : IBrokerOrders, IBrokerPortfolio, IBrokerFunds, IBrokerMultiQuotes, IBrokerMargin, IBrokerLogin, IBrokerHistory
{
    private const string DefaultBaseUrl = "https://integrate.definedgesecurities.com/dart/v1";
    private const string Display = "Definedge";
    private static readonly Lock PaceGate = new();
    private static DateTime lastCall = DateTime.MinValue;

    public string Name => "definedge";
    public string DisplayName => Display;
    public BrokerCapability Capabilities => BrokerCapability.Orders | BrokerCapability.Portfolio | BrokerCapability.Funds | BrokerCapability.Quotes | BrokerCapability.Depth | BrokerCapability.Margin | BrokerCapability.History;
    public IReadOnlyList<string> RequiredKeys { get; } = ["ApiKey", "ApiSecret"];

    private HttpClient Client() => http.CreateClient(Name);

    private static string SessionKey(BrokerSession session)
    {
        var parts = session.AuthToken.Split(":::");
        if (parts.Length != 3) throw new BrokerException($"Your {Display} sign-in is not complete. Sign in to {Display} again.", 401);
        return parts[0];
    }

    /// <summary>Calls are a tenth of a second apart, across every request, on this machine.</summary>
    private async Task PaceAsync(CancellationToken cancellationToken)
    {
        TimeSpan wait;
        var gap = sessions.HistoryGap(Name, TimeSpan.FromMilliseconds(100));
        lock (PaceGate)
        {
            var now = DateTime.UtcNow;
            wait = lastCall + gap - now;
            if (wait < TimeSpan.Zero) wait = TimeSpan.Zero;
            lastCall = now + wait;
        }

        if (wait > TimeSpan.Zero) await Task.Delay(wait, cancellationToken);
    }

    /// <summary>One call, paced, a 429 retried up to three times; the status and the body as read (null when it was not JSON).</summary>
    private Task<(int Status, JsonNode? Body)> SendAsync(BrokerSession session, HttpMethod method, string path, JsonObject? body, CancellationToken cancellationToken) =>
        SendUrlAsync(session, method, sessions.BaseUrl(Name, DefaultBaseUrl) + path, body, cancellationToken);

    /// <summary>The same call to a full address (the data host's history is on another host).</summary>
    private async Task<(int Status, JsonNode? Body, string Text)> SendRawAsync(BrokerSession session, string url, CancellationToken cancellationToken)
    {
        var key = SessionKey(session);
        for (var attempt = 0; ; attempt++)
        {
            await PaceAsync(cancellationToken);
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.TryAddWithoutValidation("Authorization", key);
            HttpResponseMessage response;
            try
            {
                response = await Client().SendAsync(request, cancellationToken);
            }
            catch (HttpRequestException ex)
            {
                throw new BrokerException($"{Display} did not answer. Check your connection, then try again.", 502, ex);
            }

            using (response)
            {
                if ((int)response.StatusCode == 429 && attempt < 3)
                {
                    var wait = response.Headers.RetryAfter?.Delta ?? TimeSpan.FromSeconds(Math.Pow(2, attempt));
                    await Task.Delay(sessions.RetryBackoff(Name, wait), cancellationToken);
                    continue;
                }

                var text = await response.Content.ReadAsStringAsync(cancellationToken);
                JsonNode? parsed = null;
                try
                {
                    parsed = string.IsNullOrWhiteSpace(text) ? null : JsonNode.Parse(text);
                }
                catch (System.Text.Json.JsonException)
                {
                }

                return ((int)response.StatusCode, parsed, text);
            }
        }
    }

    private async Task<(int Status, JsonNode? Body)> SendUrlAsync(BrokerSession session, HttpMethod method, string url, JsonObject? body, CancellationToken cancellationToken)
    {
        var key = SessionKey(session);
        for (var attempt = 0; ; attempt++)
        {
            await PaceAsync(cancellationToken);
            using var request = new HttpRequestMessage(method, url);
            request.Headers.TryAddWithoutValidation("Authorization", key);
            if (body is not null) request.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
            HttpResponseMessage response;
            try
            {
                response = await Client().SendAsync(request, cancellationToken);
            }
            catch (HttpRequestException ex)
            {
                throw new BrokerException($"{Display} did not answer. Check your connection, then try again.", 502, ex);
            }

            using (response)
            {
                if ((int)response.StatusCode == 429 && attempt < 3)
                {
                    var wait = response.Headers.RetryAfter?.Delta ?? TimeSpan.FromSeconds(Math.Pow(2, attempt));
                    await Task.Delay(sessions.RetryBackoff(Name, wait), cancellationToken);
                    continue;
                }

                return ((int)response.StatusCode, await ReadAsync(response, cancellationToken));
            }
        }
    }

    private static bool Succeeded(JsonNode? body) => body is JsonObject o && (Str(o["stat"]) == "Ok" || Str(o["status"]) == "SUCCESS");

    private static string Reason(JsonNode? body, string fallback) => body is JsonObject o && Truthy(o["emsg"]) ? Str(o["emsg"]) ?? fallback : body is JsonObject p && Truthy(p["message"]) ? Str(p["message"]) ?? fallback : fallback;

    // ---- vocabulary ------------------------------------------------------------------------------------------

    private static string Product(string? product) => product switch { "MIS" => "INTRADAY", "CNC" => "CNC", "NRML" => "NORMAL", _ => "NORMAL" };

    private static string ModifyProduct(string? product) => product switch { "MIS" => "INTRADAY", "CNC" => "CNC", "NRML" => "NORMAL", _ => "CNC" };

    private static string PriceType(string? type) => type switch { "MARKET" => "MARKET", "LIMIT" => "LIMIT", "SL" => "SL-LIMIT", "SL-M" => "SL-MARKET", _ => "LIMIT" };

    private static string AlgoId(string exchange) => exchange is "BSE" or "BFO" or "BCD" ? "9999999999999999" : "99999";

    private static string Text(decimal value) => value.ToString(CultureInfo.InvariantCulture);

    private static string PyFloat(decimal value)
    {
        var text = Text(value);
        return text.Contains('.', StringComparison.Ordinal) ? text : text + ".0";
    }

    private static readonly string[] Derivatives = ["NFO", "MCX", "BFO", "CDS"];

    /// <summary>The product as OpenAlgo shows it: INTRADAY is MIS, NORMAL is CNC in cash and NRML in derivatives, and anything else stays.</summary>
    private static string? Shown(string? product, string exchange) =>
        product == "INTRADAY" ? "MIS" : product == "NORMAL" ? exchange is "NSE" or "BSE" ? "CNC" : Derivatives.Contains(exchange) ? "NRML" : product : product;

    // ---- orders ----------------------------------------------------------------------------------------------

    public async Task<BrokerCallResult> PlaceAsync(BrokerSession session, BrokerOrder order, CancellationToken cancellationToken)
    {
        var info = await symbols.FindAsync(Name, order.Symbol, order.Exchange, cancellationToken);
        if (info is null) return BrokerCallResult.Refused(400, $"{order.Symbol} on {order.Exchange} is not in the {Display} master contract. Check the symbol, or download the master contract again.");
        var type = PriceType(order.PriceType);
        var body = new JsonObject
        {
            ["tradingsymbol"] = info.BrokerSymbol, ["exchange"] = order.Exchange, ["quantity"] = order.Quantity.ToString(CultureInfo.InvariantCulture), ["price"] = type is "MARKET" or "SL-MARKET" ? "0" : Text(order.Price),
            ["price_type"] = type, ["product_type"] = Product(order.Product), ["order_type"] = order.Action.ToUpperInvariant(), ["algo_id"] = AlgoId(order.Exchange),
        };
        if (order.TriggerPrice != 0 && order.PriceType is "SL" or "SL-M") body["trigger_price"] = Text(order.TriggerPrice);
        if (order.DisclosedQuantity != 0) body["disclosed_quantity"] = order.DisclosedQuantity.ToString(CultureInfo.InvariantCulture);

        (int Status, JsonNode? Body) reply;
        try
        {
            reply = await SendAsync(session, HttpMethod.Post, "/placeorder", body, cancellationToken);
        }
        catch (BrokerException ex) when (ex.HttpStatus == 502)
        {
            return new BrokerCallResult(false, 502, $"The order was sent to {Display} but no reply came back, so it may have been placed. Check the order book on {Display} before trying again.", MayHaveBeenSent: true);
        }

        if (reply.Body is not JsonObject answer)
            return new BrokerCallResult(false, 502, $"{Display} could not confirm the order, so it may have been placed. Check the order book on {Display} before trying again.", MayHaveBeenSent: true);
        if (Succeeded(answer))
        {
            var id = Truthy(answer["norenordno"]) ? Str(answer["norenordno"]) : Str(answer["order_id"]);
            if (id is { Length: > 0 }) return BrokerCallResult.Success(id);
            return new BrokerCallResult(false, 502, $"{Display} accepted the order but sent no order number, so it cannot be followed. Check the order book on {Display} before trying again.", MayHaveBeenSent: true);
        }

        return BrokerCallResult.Refused(reply.Status is >= 400 ? reply.Status : 400, Reason(answer, "Failed to place order"));
    }

    public async Task<BrokerCallResult> ModifyAsync(BrokerSession session, BrokerOrderChange change, CancellationToken cancellationToken)
    {
        var info = await symbols.FindAsync(Name, change.Symbol, change.Exchange, cancellationToken);
        var symbol = change.Symbol.Contains('-', StringComparison.Ordinal) ? change.Symbol : info?.BrokerSymbol ?? change.Symbol;
        var body = new JsonObject
        {
            ["order_id"] = change.OrderId, ["tradingsymbol"] = symbol, ["exchange"] = change.Exchange, ["quantity"] = change.Quantity.ToString(CultureInfo.InvariantCulture), ["price"] = PyFloat(change.Price),
            ["price_type"] = PriceType(change.PriceType), ["product_type"] = ModifyProduct(change.Product), ["order_type"] = change.Action.ToUpperInvariant(),
        };
        if (change.TriggerPrice != 0 && change.PriceType is "SL" or "SL-M") body["trigger_price"] = Text(change.TriggerPrice);
        if (change.DisclosedQuantity != 0) body["disclosed_quantity"] = change.DisclosedQuantity.ToString(CultureInfo.InvariantCulture);
        body["validity"] = "DAY";

        var reply = await SendAsync(session, HttpMethod.Post, "/modify", body, cancellationToken);
        if (Succeeded(reply.Body))
        {
            var d = (JsonObject)reply.Body!;
            return BrokerCallResult.Success(Truthy(d["order_id"]) ? Str(d["order_id"])! : Truthy(d["norenordno"]) ? Str(d["norenordno"])! : change.OrderId);
        }

        return BrokerCallResult.Refused(reply.Status is >= 400 ? reply.Status : 400, Reason(reply.Body, "Failed to modify order"));
    }

    public async Task<BrokerCallResult> CancelAsync(BrokerSession session, string orderId, CancellationToken cancellationToken)
    {
        var reply = await SendAsync(session, HttpMethod.Get, $"/cancel/{orderId}", null, cancellationToken);
        if (reply.Body is JsonObject d && Str(d["status"]) == "SUCCESS") return BrokerCallResult.Success(Truthy(d["order_id"]) ? Str(d["order_id"])! : orderId);
        return BrokerCallResult.Refused(reply.Status is >= 400 ? reply.Status : 400, reply.Body is JsonObject m && Truthy(m["message"]) ? Str(m["message"])! : "Failed to cancel order");
    }

    // ---- books -----------------------------------------------------------------------------------------------

    private async Task<List<JsonObject>> ReadBookAsync(BrokerSession session, string path, string listKey, string what, CancellationToken cancellationToken)
    {
        var (_, body) = await SendAsync(session, HttpMethod.Get, path, null, cancellationToken);
        if (body is not JsonObject o || !Succeeded(o)) throw new BrokerException($"{Display} could not send the {what}: {Reason(body, "no reason was given.")}", 500);
        return (o[listKey] as JsonArray)?.OfType<JsonObject>().ToList() ?? [];
    }

    private async Task<string?> SymbolOrNullAsync(string? brokerSymbol, string exchange, CancellationToken cancellationToken) =>
        !string.IsNullOrEmpty(brokerSymbol) && !string.IsNullOrEmpty(exchange) ? (await symbols.FindByBrokerSymbolAsync(Name, brokerSymbol, exchange, cancellationToken))?.Symbol : null;

    private static JsonNode Raw(JsonObject row, string key, JsonNode fallback) => row[key]?.DeepClone() ?? fallback;

    private static string Status(string raw) => raw switch
    {
        "COMPLETE" or "EXECUTED" => "complete", "REJECTED" => "rejected", "OPEN" or "NEW" or "REPLACED" or "PENDING" or "TRIGGER PENDING" or "TRIGGER_PENDING" => "open", "CANCELED" or "CANCELLED" => "cancelled", _ => raw.ToLowerInvariant(),
    };

    public async Task<BrokerBook> GetOrderBookAsync(BrokerSession session, CancellationToken cancellationToken)
    {
        int buy = 0, sell = 0, completed = 0, open = 0, rejected = 0;
        var result = new JsonArray();
        foreach (var r in await ReadBookAsync(session, "/orders", "orders", "order book", cancellationToken))
        {
            var exchange = Str(r["exchange"]) ?? "";
            var symbol = Str(r["tradingsymbol"]) ?? "";
            var shown = await SymbolOrNullAsync(symbol, exchange, cancellationToken);
            var product = Raw(r, "product_type", "");
            if (shown is not null && Str(r["product_type"]) is { } raw && Shown(raw, exchange) is { } mapped) product = mapped;
            var side = (Str(r["order_type"]) ?? "").ToUpperInvariant();
            if (side == "BUY") buy++;
            else if (side == "SELL") sell++;
            var status = (Str(r["order_status"]) ?? "").ToUpperInvariant();
            if (status is "COMPLETE" or "EXECUTED") completed++;
            else if (status is "OPEN" or "NEW" or "REPLACED" or "PENDING" or "TRIGGER PENDING" or "TRIGGER_PENDING") open++;
            else if (status is "REJECTED" or "CANCELED" or "CANCELLED") rejected++;

            var priceType = Str(r["price_type"]) ?? "";
            result.Add(new JsonObject
            {
                ["symbol"] = shown ?? symbol, ["exchange"] = exchange, ["action"] = Raw(r, "order_type", ""), ["quantity"] = Raw(r, "quantity", 0), ["price"] = Raw(r, "price", 0.0), ["trigger_price"] = Raw(r, "trigger_price", 0.0),
                ["pricetype"] = priceType switch { "SL-LIMIT" => "SL", "SL-MARKET" => "SL-M", _ => priceType }, ["product"] = product, ["orderid"] = Raw(r, "order_id", ""), ["order_status"] = Status(status),
                ["timestamp"] = r.ContainsKey("exchange_time") ? Raw(r, "exchange_time", "") : Raw(r, "order_entry_time", ""),
            });
        }

        return new BrokerBook(result, new JsonObject { ["total_buy_orders"] = buy, ["total_sell_orders"] = sell, ["total_completed_orders"] = completed, ["total_open_orders"] = open, ["total_rejected_orders"] = rejected });
    }

    public async Task<JsonArray> GetTradeBookAsync(BrokerSession session, CancellationToken cancellationToken)
    {
        var result = new JsonArray();
        foreach (var r in await ReadBookAsync(session, "/trades", "trades", "trade book", cancellationToken))
        {
            var exchange = Str(r["exchange"]) ?? "";
            var symbol = Str(r["tradingsymbol"]) ?? "";
            var quantity = Truthy(r["filled_qty"]) || r.ContainsKey("filled_qty") ? Int(r["filled_qty"]) : Int(r["quantity"]);
            var fill = Float(r["fill_price"]);
            if (fill == 0) fill = r.ContainsKey("average_traded_price") ? Float(r["average_traded_price"]) : Float(r["price"]);
            result.Add(new JsonObject
            {
                ["symbol"] = await SymbolOrNullAsync(symbol, exchange, cancellationToken) ?? symbol, ["exchange"] = exchange, ["product"] = Shown(Str(r["product_type"]), exchange), ["action"] = (Str(r["order_type"]) ?? "").ToUpperInvariant(),
                ["quantity"] = quantity, ["average_price"] = BookFormatting.Round2(fill), ["trade_value"] = BookFormatting.Round2(quantity * fill), ["orderid"] = Raw(r, "order_id", ""),
                ["timestamp"] = r.ContainsKey("fill_time") ? Raw(r, "fill_time", "") : r.ContainsKey("exchange_time") ? Raw(r, "exchange_time", "") : "",
            });
        }

        return result;
    }

    public async Task<JsonArray> GetPositionsAsync(BrokerSession session, CancellationToken cancellationToken)
    {
        var result = new JsonArray();
        foreach (var r in await ReadBookAsync(session, "/positions", "positions", "positions", cancellationToken))
        {
            var exchange = Str(r["exchange"]) ?? "";
            var symbol = Str(r["tradingsymbol"]) ?? "";
            var net = Int(r["net_quantity"]);
            result.Add(new JsonObject
            {
                ["symbol"] = await SymbolOrNullAsync(symbol, exchange, cancellationToken) ?? symbol, ["exchange"] = exchange, ["product"] = Shown(Str(r["product_type"]), exchange), ["quantity"] = net,
                ["average_price"] = Raw(r, "net_averageprice", 0.0), ["ltp"] = Raw(r, "lastPrice", 0.0), ["pnl"] = net == 0 ? Raw(r, "realized_pnl", 0.0) : Raw(r, "unrealized_pnl", 0.0),
            });
        }

        return result;
    }

    public async Task<BrokerBook> GetHoldingsAsync(BrokerSession session, CancellationToken cancellationToken)
    {
        var holdings = new JsonArray();
        double invested = 0;
        foreach (var h in await ReadBookAsync(session, "/holdings", "data", "holdings", cancellationToken))
        {
            var first = (h["tradingsymbol"] as JsonArray)?.OfType<JsonObject>().FirstOrDefault();
            var exchange = Str(first?["exchange"]) ?? "";
            var symbol = Str(first?["tradingsymbol"]) ?? "";
            var shown = await SymbolOrNullAsync(symbol, exchange, cancellationToken) ?? symbol;
            var quantity = Float(h["dp_qty"]) + Float(h["t1_qty"]);
            if (quantity == 0) continue;
            invested += quantity * Float(h["avg_buy_price"]);
            holdings.Add(new JsonObject { ["symbol"] = shown, ["exchange"] = exchange, ["quantity"] = (long)Math.Truncate(quantity), ["product"] = "CNC", ["pnl"] = 0.0, ["pnlpercent"] = 0.0 });
        }

        return new BrokerBook(holdings, new JsonObject { ["totalholdingvalue"] = BookFormatting.Round2(invested), ["totalinvvalue"] = BookFormatting.Round2(invested), ["totalprofitandloss"] = 0.0, ["totalpnlpercentage"] = 0.0 });
    }

    // ---- funds -----------------------------------------------------------------------------------------------

    private static string Money(JsonNode? node)
    {
        var text = Str(node);
        return text is not null && double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? BookFormatting.Fixed2(d) : BookFormatting.Number(node) is { } n ? BookFormatting.Fixed2(n) : "0.00";
    }

    private static double Sum(JsonObject d, params string[] names) => names.Sum(n => Float(d[n]));

    public async Task<JsonObject> GetFundsAsync(BrokerSession session, CancellationToken cancellationToken)
    {
        var (_, body) = await SendAsync(session, HttpMethod.Get, "/limits", null, cancellationToken);
        if (body is not JsonObject d || !(Str(d["status"]) is "SUCCESS" or "200" || d.ContainsKey("cash"))) throw new BrokerException($"{Display} could not send the funds: {Reason(body, "no reason was given.")}", 500);
        var unrealized = Float(d["currentUnrealizedMtom"]);
        if (unrealized == 0)
        {
            unrealized = Sum(d, "currentUnrealizedMTOMDerivativeIntraday", "currentUnrealizedMTOMDerivativeMargin", "currentUnrealizedMTOMEquityIntraday", "currentUnrealizedMTOMEquityMargin",
                "currentUnrealizedMTOMCommodityIntraday", "currentUnrealizedMTOMCommodityMargin");
        }

        var realized = Float(d["currentRealizedPNL"]);
        if (realized == 0)
        {
            realized = Float(d["currentRealizedPNLDerivativeIntraday"]) + Float(d["currentRealizedPNLDerivativeMargin"]) + Float(d["currentRealizedPNLEquityIntraday"]) + Float(d["currentRealizedPNLEquityMargin"])
                + Float(d["currentRealizedPNLCommodityIntraday"]) + Float(d["currentRealizedPNLCommodityMargin"]);
        }

        return new JsonObject
        {
            ["availablecash"] = Money(d["cash"]), ["collateral"] = Money(d["brokerCollateralAmount"]), ["m2munrealized"] = BookFormatting.Fixed2(unrealized), ["m2mrealized"] = BookFormatting.Fixed2(realized), ["utiliseddebits"] = Money(d["marginUsed"]),
        };
    }
}
