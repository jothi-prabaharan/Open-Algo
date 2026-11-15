using System.Globalization;
using System.Text.Json.Nodes;
using EPTradeBot.Core.Brokers;
using static EPTradeBot.Infrastructure.Brokers.JsonRead;

namespace EPTradeBot.Infrastructure.Brokers.Tradejini;

/// <summary>
/// Tradejini (api.tradejini.com/v2): port of broker/tradejini api/order_api.py, api/funds.py and mapping/order_data.py, mapping/transform_data.py. Every call carries Bearer &lt;app API key&gt;:&lt;access token&gt;; order writes are form-encoded and every
/// answer is an envelope {s: ok | no-data | error, d, msg}. An instrument is its symbol id (brsymbol of the master), and a book row carries it as a sym object.
/// </summary>
[BrokerPlugin]
public sealed partial class TradejiniPlugin(IHttpClientFactory http, BrokerSessionFactory sessions, ISymbolMaster symbols, TimeProvider clock, ITradejiniSockets? sockets = null) : IBrokerOrders, IBrokerPortfolio, IBrokerFunds
{
    private const string DefaultBaseUrl = "https://api.tradejini.com/v2";
    private const string Display = "Tradejini";

    public string Name => "tradejini";
    public string DisplayName => "Tradejini";
    public BrokerCapability Capabilities => BrokerCapability.Orders | BrokerCapability.Portfolio | BrokerCapability.Funds | BrokerCapability.History | BrokerCapability.Quotes | BrokerCapability.Depth;
    public IReadOnlyList<string> RequiredKeys { get; } = ["ApiKey"];

    private HttpClient Client() => http.CreateClient(Name);

    /// <summary>The app API key: the key, else the secret older installs kept it in; a value still reading YOUR_ is not set.</summary>
    internal static string? ApiKey(BrokerSession session)
    {
        foreach (var name in new[] { "ApiKey", "ApiSecret" })
            if (session.Key(name)?.Trim() is { Length: > 0 } value && !value.StartsWith("YOUR_", StringComparison.OrdinalIgnoreCase)) return value;
        return null;
    }

    private DateTimeOffset Now => clock.GetUtcNow();

    private const string KeyMissing = "The Tradejini API key is not set on this server. Add the API key of your app from the Tradejini developer portal to the server's settings, then try again.";

    /// <summary>One call: the status and the body as read (null when it was not JSON). An error status still carries its body.</summary>
    private async Task<(int Status, JsonNode? Body, string Text)> SendAsync(BrokerSession session, HttpMethod method, string path, IEnumerable<KeyValuePair<string, string>>? query, IEnumerable<KeyValuePair<string, string>>? form, CancellationToken cancellationToken, string contentType = "application/x-www-form-urlencoded", bool accept = false)
    {
        var key = ApiKey(session) ?? throw new BrokerException(KeyMissing, 409);
        var url = sessions.BaseUrl(Name, DefaultBaseUrl) + path;
        if (query is not null)
        {
            var parts = string.Join('&', query.Select(q => $"{Uri.EscapeDataString(q.Key)}={Uri.EscapeDataString(q.Value)}"));
            if (parts.Length > 0) url += "?" + parts;
        }

        using var request = new HttpRequestMessage(method, url);
        request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {key}:{session.AuthToken}");
        if (accept) request.Headers.TryAddWithoutValidation("Accept", "application/json");
        // The broker is sent a content type on every call, a body or not, and the type depends on the call.
        request.Content = form is not null ? new FormUrlEncodedContent(form) : new StringContent("", System.Text.Encoding.UTF8, contentType);
        try
        {
            using var response = await Client().SendAsync(request, cancellationToken);
            var text = await response.Content.ReadAsStringAsync(cancellationToken);
            JsonNode? body = null;
            try
            {
                body = JsonNode.Parse(text);
            }
            catch (System.Text.Json.JsonException)
            {
            }

            return ((int)response.StatusCode, body, text);
        }
        catch (HttpRequestException ex)
        {
            throw new BrokerException($"{Display} did not answer. Check your connection, then try again.", 502, ex);
        }
    }

    /// <summary>The reason in an envelope: the message at the top, else the one inside d.</summary>
    private static string? Reason(JsonNode? body)
    {
        if (body is not JsonObject o) return null;
        if (Truthy(o["msg"])) return Str(o["msg"]);
        return o["d"] is JsonObject d && Truthy(d["msg"]) ? Str(d["msg"]) : null;
    }

    private static bool IsOk(JsonNode? body) => body is JsonObject o && Str(o["s"]) == "ok";

    // ---- vocabulary ------------------------------------------------------------------------------------------

    private static string OrderType(string? type) => type switch { "MARKET" => "market", "LIMIT" => "limit", "SL" => "stoplimit", "SL-M" => "stopmarket", _ => "market" };

    private static string Product(string? product) => product switch { "CNC" => "delivery", "NRML" => "normal", "MIS" => "intraday", _ => "intraday" };

    /// <summary>Python's str(float): a whole number keeps its .0.</summary>
    internal static string PyFloat(double value)
    {
        var text = value.ToString("R", CultureInfo.InvariantCulture);
        return text.Contains('.', StringComparison.Ordinal) || text.Contains('E', StringComparison.Ordinal) ? text : text + ".0";
    }

    private static string PyFloat(decimal value) => PyFloat((double)value);

    // ---- orders ----------------------------------------------------------------------------------------------

    private static string Whole(int value) => value.ToString(CultureInfo.InvariantCulture);

    public async Task<BrokerCallResult> PlaceAsync(BrokerSession session, BrokerOrder order, CancellationToken cancellationToken)
    {
        var info = await symbols.FindAsync(Name, order.Symbol, order.Exchange, cancellationToken);
        if (info is null) return BrokerCallResult.Refused(400, $"{order.Symbol} on {order.Exchange} is not in the {Display} master contract. Check the symbol, or download the master contract again.");
        var type = OrderType(order.PriceType);
        var form = new List<KeyValuePair<string, string>>
        {
            new("symId", info.BrokerSymbol), new("qty", Whole(order.Quantity)), new("side", order.Action.Equals("BUY", StringComparison.Ordinal) ? "buy" : "sell"), new("type", type), new("product", Product(order.Product)),
            new("limitPrice", PyFloat(order.Price)), new("trigPrice", PyFloat(order.TriggerPrice)), new("validity", "day"),
        };
        if (order.DisclosedQuantity != 0) form.Add(new("discQty", Whole(order.DisclosedQuantity)));
        if (type is "market" or "stopmarket") form.Add(new("mktProt", "2"));

        (int Status, JsonNode? Body, string Text) reply;
        try
        {
            reply = await SendAsync(session, HttpMethod.Post, "/api/oms/place-order", null, form, cancellationToken);
        }
        catch (BrokerException ex) when (ex.HttpStatus == 502)
        {
            return new BrokerCallResult(false, 502, $"The order was sent to {Display} but no reply came back, so it may have been placed. Check the order book on {Display} before trying again.", MayHaveBeenSent: true);
        }

        if (reply.Status >= 400) return BrokerCallResult.Refused(reply.Status, Reason(reply.Body) ?? $"{Display} refused the order.");
        if (reply.Body is not JsonObject answer) return new BrokerCallResult(false, 502, $"{Display} could not confirm the order, so it may have been placed. Check the order book on {Display} before trying again.", MayHaveBeenSent: true);
        if (!IsOk(answer)) return BrokerCallResult.Refused(400, Reason(answer) ?? $"{Display} refused the order.");
        var id = answer["d"] is JsonObject data && Truthy(data["orderId"]) ? Str(data["orderId"]) : null;
        if (!string.IsNullOrEmpty(id)) return BrokerCallResult.Success(id);
        return new BrokerCallResult(false, 502, $"{Display} accepted the order but sent no order number, so it cannot be followed. Check the order book on {Display} before trying again.", MayHaveBeenSent: true);
    }

    public async Task<BrokerCallResult> ModifyAsync(BrokerSession session, BrokerOrderChange change, CancellationToken cancellationToken)
    {
        var info = await symbols.FindAsync(Name, change.Symbol, change.Exchange, cancellationToken);
        if (info is null) return BrokerCallResult.Refused(400, $"{change.Symbol} on {change.Exchange} is not in the {Display} master contract. Check the symbol, or download the master contract again.");
        var form = new List<KeyValuePair<string, string>>
        {
            new("symId", info.BrokerSymbol), new("orderId", change.OrderId), new("qty", Whole(change.Quantity)), new("type", OrderType(change.PriceType)), new("validity", "day"), new("side", change.Action.ToLowerInvariant()),
        };
        if (change.PriceType is "LIMIT" or "SL") form.Add(new("limitPrice", PyFloat(change.Price)));
        if (change.PriceType is "SL" or "SL-M") form.Add(new("trigPrice", PyFloat(change.TriggerPrice)));
        if (change.DisclosedQuantity != 0) form.Add(new("discQty", Whole(change.DisclosedQuantity)));
        var (status, body, _) = await SendAsync(session, HttpMethod.Put, "/api/oms/modify-order", null, form, cancellationToken);
        if (status < 400 && IsOk(body))
            return BrokerCallResult.Success(body is JsonObject o && o["d"] is JsonObject d && Truthy(d["orderId"]) ? Str(d["orderId"])! : change.OrderId);
        return BrokerCallResult.Refused(status >= 400 ? status : 400, Reason(body) ?? "Failed to modify order");
    }

    public async Task<BrokerCallResult> CancelAsync(BrokerSession session, string orderId, CancellationToken cancellationToken)
    {
        var (status, body, _) = await SendAsync(session, HttpMethod.Delete, "/api/oms/cancel-order", [new("orderId", orderId)], null, cancellationToken);
        if (status < 400 && IsOk(body))
            return BrokerCallResult.Success(body is JsonObject o && o["d"] is JsonObject d && Truthy(d["orderId"]) ? Str(d["orderId"])! : orderId);
        return BrokerCallResult.Refused(status >= 400 ? status : 400, Reason(body) ?? "Failed to cancel order");
    }

    // ---- books -----------------------------------------------------------------------------------------------

    /// <summary>The rows of a book. No data is an empty book; anything else that is not an ok answer is an error naming the call, where OpenAlgo shows it as an empty book.</summary>
    private async Task<(JsonNode? Data, bool Empty)> ReadBookAsync(BrokerSession session, string path, string what, CancellationToken cancellationToken, string contentType = "application/json")
    {
        var (status, body, _) = await SendAsync(session, HttpMethod.Get, path, [new("symDetails", "true")], null, cancellationToken, contentType);
        if (body is JsonObject o && status < 400)
        {
            if (Str(o["s"]) == "no-data") return (null, true);
            if (Str(o["s"]) == "ok") return (o["d"], false);
        }

        throw new BrokerException($"{Display} could not send the {what}: {Reason(body) ?? "no reason was given."}", status >= 400 ? status : 500);
    }

    private static string Field(JsonNode? sym, params string[] names)
    {
        if (sym is not JsonObject o) return "";
        foreach (var name in names)
            if (o[name] is { } v && Str(v) is { Length: > 0 } text) return text;
        return "";
    }

    private static string SymId(JsonNode? sym) => Field(sym, "id", "symId");

    private static string SymExchange(JsonNode? sym) => Field(sym, "exchange", "exch");

    private static string SymBase(JsonNode? sym) => Field(sym, "symbol", "sym");

    private static string SymTrading(JsonNode? sym) => Field(sym, "tradSymbol", "trdSym", "dispSymbol", "dispSym");

    private async Task<string?> Lookup(string brokerSymbol, string exchange, CancellationToken cancellationToken) =>
        brokerSymbol.Length > 0 && exchange.Length > 0 ? (await symbols.FindByBrokerSymbolAsync(Name, brokerSymbol, exchange, cancellationToken))?.Symbol : null;

    private static string ReverseProduct(string? product, string fallback) => (product ?? "").ToLowerInvariant() switch
    {
        "delivery" => "CNC", "normal" or "margin" => "NRML", "intraday" => "MIS", "cover" or "coverorder" => "CO", "bracket" or "bracketorder" => "BO", _ => fallback,
    };

    private static string ReversePriceType(string? type) => (type ?? "").ToLowerInvariant() switch
    {
        "market" => "MARKET", "limit" => "LIMIT", "stoplimit" => "SL", "stopmarket" => "SL-M", var other => (type ?? "").ToUpperInvariant(),
    };

    /// <summary>float(x or 0): absent, null, zero and an empty string are zero; anything else has to read as a number.</summary>
    private static double Flt(JsonNode? node, string what)
    {
        if (!Truthy(node)) return 0;
        if (BookFormatting.Number(node) is { } n) return n;
        if (Str(node) is { } s && double.TryParse(s.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var d)) return d;
        throw new BrokerException($"{Display} sent {what} that could not be read as a number.", 500);
    }

    /// <summary>int(x or 0).</summary>
    private static long Whole(JsonNode? node, string what)
    {
        if (!Truthy(node)) return 0;
        if (node is JsonValue v && v.TryGetValue<long>(out var l)) return l;
        if (node is JsonValue w && w.TryGetValue<int>(out var i)) return i;
        if (BookFormatting.Number(node) is { } n) return (long)Math.Truncate(n);
        if (Str(node) is { } s && long.TryParse(s.Trim(), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var parsed)) return parsed;
        throw new BrokerException($"{Display} sent {what} that could not be read as a whole number.", 500);
    }

    private static string StatusOf(string raw)
    {
        var lower = raw.ToLowerInvariant();
        return lower switch
        {
            "completed" or "traded" or "filled" or "complete" => "complete",
            "open" or "pending" or "modified" => "open",
            "trigger pending" => "trigger pending",
            "rejected" => "rejected",
            "cancelled" or "canceled" => "cancelled",
            _ => lower,
        };
    }

    public async Task<BrokerBook> GetOrderBookAsync(BrokerSession session, CancellationToken cancellationToken)
    {
        var (data, empty) = await ReadBookAsync(session, "/api/oms/orders", "order book", cancellationToken, "application/x-www-form-urlencoded");
        var rows = new JsonArray();
        int buy = 0, sell = 0, completed = 0, open = 0, rejected = 0;
        foreach (var r in (data as JsonArray)?.OfType<JsonObject>() ?? [])
        {
            var sym = r["sym"] as JsonObject;
            var exchange = SymExchange(sym);
            var symbolId = SymId(sym) is { Length: > 0 } sid ? sid : Str(r["symId"]) ?? "";
            var symbol = await Lookup(symbolId, exchange, cancellationToken) ?? (SymTrading(sym) is { Length: > 0 } t ? t : SymBase(sym));
            var action = Str(r["side"]) == "buy" ? "BUY" : "SELL";
            var status = StatusOf(Str(r["status"]) ?? "");
            var product = ReverseProduct(Str(r["product"]), "MIS");
            if (action == "BUY") buy++;
            else sell++;
            if (status == "complete") completed++;
            else if (status == "rejected") rejected++;
            else if (status == "open") open++;
            rows.Add(new JsonObject
            {
                ["action"] = action, ["exchange"] = exchange, ["order_status"] = status, ["orderid"] = Str(r["orderId"]) ?? "", ["price"] = Flt(r["limitPrice"], "a limit price"),
                ["pricetype"] = ReversePriceType(Str(r["type"])).ToUpperInvariant(), ["product"] = product.ToUpperInvariant(), ["quantity"] = Whole(r["qty"], "a quantity"), ["symbol"] = symbol,
                ["timestamp"] = Str(r["orderTime"]) ?? "", ["trigger_price"] = Flt(r["trigPrice"], "a trigger price"),
            });
        }

        _ = empty;
        return new BrokerBook(rows, new JsonObject
        {
            ["total_buy_orders"] = buy, ["total_sell_orders"] = sell, ["total_completed_orders"] = completed, ["total_open_orders"] = open, ["total_rejected_orders"] = rejected,
        });
    }

    public async Task<JsonArray> GetTradeBookAsync(BrokerSession session, CancellationToken cancellationToken)
    {
        var (data, _) = await ReadBookAsync(session, "/api/oms/trades", "trade book", cancellationToken);
        var result = new JsonArray();
        foreach (var t in (data as JsonArray)?.OfType<JsonObject>() ?? [])
        {
            var sym = t["sym"] as JsonObject;
            var exchange = SymExchange(sym);
            var symbolId = SymId(sym) is { Length: > 0 } sid ? sid : Str(t["symId"]) ?? "";
            var symbol = await Lookup(symbolId, exchange, cancellationToken) ?? (SymBase(sym) is { Length: > 0 } b ? b : SymTrading(sym));
            result.Add(new JsonObject
            {
                ["action"] = (Str(t["side"]) ?? "").ToLowerInvariant() == "buy" ? "BUY" : "SELL", ["average_price"] = Flt(t["fillPrice"], "a fill price"), ["exchange"] = exchange.ToUpperInvariant(), ["orderid"] = Str(t["orderId"]) ?? "",
                ["product"] = ReverseProduct(Str(t["product"]), "NRML"), ["quantity"] = Whole(t["fillQty"], "a fill quantity"), ["symbol"] = symbol, ["timestamp"] = Str(t["time"]) ?? "", ["trade_value"] = Flt(t["fillValue"], "a fill value"),
            });
        }

        return result;
    }

    public async Task<JsonArray> GetPositionsAsync(BrokerSession session, CancellationToken cancellationToken)
    {
        var (data, _) = await ReadBookAsync(session, "/api/oms/positions", "position book", cancellationToken);
        var result = new JsonArray();
        foreach (var p in (data as JsonArray)?.OfType<JsonObject>() ?? [])
        {
            var sym = p["sym"] as JsonObject;
            var exchange = SymExchange(sym);
            var baseSymbol = SymBase(sym);
            var symbol = (SymId(sym) is { Length: > 0 } id ? await Lookup(id, exchange, cancellationToken) : null) ?? await Lookup(baseSymbol, exchange, cancellationToken) ?? baseSymbol;
            var average = BookFormatting.RoundPy(Flt(p["netAvgPrice"], "an average price"), 2);
            result.Add(new JsonObject
            {
                ["symbol"] = symbol, ["exchange"] = exchange, ["product"] = ReverseProduct(Str(p["product"]), "MIS"), ["quantity"] = p["netQty"]?.DeepClone() ?? 0, ["average_price"] = PyFloat(average),
            });
        }

        return result;
    }

    private static (double Quantity, double Average, double Ltp, double Realized) HoldingValues(JsonObject holding)
    {
        var sym = holding["sym"] as JsonObject;
        var quantity = Flt(Truthy(holding["qty"]) ? holding["qty"] : holding["saleableQty"], "a quantity");
        var average = Flt(holding["avgPrice"], "an average price");
        var lastRaw = sym is not null && Truthy(sym["lastPrice"]) ? sym["lastPrice"] : sym is not null && Truthy(sym["ltp"]) ? sym["ltp"] : null;
        var ltp = lastRaw is null ? average : Flt(lastRaw, "a last price");
        if (ltp == 0) ltp = average;
        return (quantity, average, ltp, Flt(holding["realizedPnl"], "a realised profit"));
    }

    public async Task<BrokerBook> GetHoldingsAsync(BrokerSession session, CancellationToken cancellationToken)
    {
        var (status, body, _) = await SendAsync(session, HttpMethod.Get, "/api/oms/holdings", [new("symDetails", "true")], null, cancellationToken, "application/x-www-form-urlencoded");
        if (status >= 400 || body is not JsonObject answer) throw new BrokerException($"{Display} could not send the holdings: {Reason(body) ?? "no reason was given."}", status >= 400 ? status : 500);
        var state = Str(answer["s"]) ?? Str(answer["stat"]);
        JsonArray list;
        if (state == "no-data" && (Str(answer["msg"]) ?? "").Contains("No Data Available", StringComparison.Ordinal)) list = [];
        else if (state is "Ok" or "ok")
        {
            var d = answer["d"] ?? answer["data"];
            if (d is JsonValue v && v.TryGetValue<string>(out var text) && text.Contains("No Holdings", StringComparison.Ordinal)) list = [];
            else if (d is JsonObject o) list = o["holdings"] as JsonArray ?? [];
            else throw new BrokerException($"{Display} sent holdings that could not be read.", 500);
        }
        else
        {
            throw new BrokerException($"{Display} could not send the holdings: {Reason(answer) ?? "no reason was given."}", 500);
        }

        var rows = new JsonArray();
        double totalValue = 0, totalInvestment = 0, totalPnl = 0;
        foreach (var h in list.OfType<JsonObject>())
        {
            // The totals above the table take every holding whose figures read, a holding with no instrument included.
            try
            {
                var (q, a, l, realized) = HoldingValues(h);
                totalInvestment += q * a;
                totalValue += q * l;
                totalPnl += (q * l - q * a) + realized;
            }
            catch (BrokerException)
            {
                continue;
            }

            var sym = h["sym"] as JsonObject;
            var tradeSymbol = SymTrading(sym) is { Length: > 0 } t ? t : SymBase(sym);
            var exchange = SymExchange(sym);
            if (sym is null || sym.Count == 0 || tradeSymbol.Length == 0) continue;
            var symbolId = SymId(sym) is { Length: > 0 } sid ? sid : Str(h["symId"]) ?? "";
            var openalgo = await Lookup(symbolId, exchange, cancellationToken);
            var (quantity, avg, ltp, realizedPnl) = HoldingValues(h);
            var investment = quantity * avg;
            var current = ltp > 0 ? quantity * ltp : investment;
            var pnl = (current - investment) + realizedPnl;
            var percent = investment > 0 ? (pnl / investment) * 100 : 0.0;
            var product = (Str(h["product"]) ?? "").ToUpperInvariant() is "MIS" or "INTRADAY" ? "MIS" : "CNC";
            rows.Add(new JsonObject
            {
                ["exchange"] = exchange.Length > 0 ? exchange : "NSE", ["pnl"] = BookFormatting.RoundPy(pnl, 2), ["pnlpercent"] = BookFormatting.RoundPy(percent, 2), ["product"] = product, ["quantity"] = (long)quantity,
                ["symbol"] = (openalgo ?? tradeSymbol).Trim(), ["avgprice"] = BookFormatting.RoundPy(avg, 2), ["ltp"] = BookFormatting.RoundPy(ltp, 2), ["investment"] = BookFormatting.RoundPy(investment, 2), ["current_value"] = BookFormatting.RoundPy(current, 2),
            });
        }

        var percentTotal = totalInvestment != 0 ? (totalPnl / totalInvestment) * 100 : 0.0;
        return new BrokerBook(rows, new JsonObject
        {
            ["totalholdingvalue"] = BookFormatting.RoundPy(totalValue, 2), ["totalinvvalue"] = BookFormatting.RoundPy(totalInvestment, 2), ["totalprofitandloss"] = BookFormatting.RoundPy(totalPnl, 2), ["totalpnlpercentage"] = BookFormatting.RoundPy(percentTotal, 2),
        });
    }

    // ---- funds -----------------------------------------------------------------------------------------------

    public async Task<JsonObject> GetFundsAsync(BrokerSession session, CancellationToken cancellationToken)
    {
        var (status, body, _) = await SendAsync(session, HttpMethod.Get, "/api/oms/limits", null, null, cancellationToken, "application/json");
        if (status >= 400 || !IsOk(body) || body is not JsonObject answer || !answer.ContainsKey("d")) throw new BrokerException($"{Display} could not send the funds: {Reason(body) ?? "no reason was given."}", status >= 400 ? status : 500);
        JsonObject margin;
        if (answer["d"] is JsonArray segments)
        {
            if (segments.Count == 0) throw new BrokerException($"{Display} sent no funds figures.", 500);
            var merged = new Dictionary<string, double>();
            foreach (var segment in segments.OfType<JsonObject>())
                foreach (var (key, value) in segment)
                    if (value is JsonValue v && !(v.TryGetValue<bool>(out _)) && BookFormatting.Number(value) is { } n) merged[key] = merged.GetValueOrDefault(key) + n;
            margin = new JsonObject();
            foreach (var (key, value) in merged) margin[key] = value;
        }
        else if (answer["d"] is JsonObject one)
        {
            margin = one;
        }
        else
        {
            throw new BrokerException($"{Display} sent funds that could not be read.", 500);
        }

        string Fixed(string name) => BookFormatting.Fixed2(!margin.ContainsKey(name) ? 0 : Flt(margin[name], "a funds figure"));
        return new JsonObject
        {
            ["availablecash"] = Fixed("availMargin"), ["collateral"] = Fixed("stockCollateral"), ["m2munrealized"] = Fixed("unrealizedPnL"), ["m2mrealized"] = Fixed("realizedPnl"), ["utiliseddebits"] = Fixed("marginUsed"),
        };
    }
}
