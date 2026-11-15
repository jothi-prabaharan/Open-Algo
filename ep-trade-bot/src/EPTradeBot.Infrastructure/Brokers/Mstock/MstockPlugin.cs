using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using EPTradeBot.Core.Brokers;
using static EPTradeBot.Infrastructure.Brokers.JsonRead;

namespace EPTradeBot.Infrastructure.Brokers.Mstock;

/// <summary>
/// mStock by Mirae Asset, the Type B API (api.mstock.trade/openapi/typeb): port of broker/mstock api/order_api.py, api/funds.py, mapping/transform_data.py and mapping/order_data.py. Every call carries the version header, the API key
/// (X-PrivateKey, the broker's second key here) and the bearer token. mStock answers a business refusal with status 200, so the payload's status says whether a call worked; a reply may be wrapped in a one-item list.
/// </summary>
[BrokerPlugin]
public sealed partial class MstockPlugin(IHttpClientFactory http, BrokerSessionFactory sessions, ISymbolMaster symbols, TimeProvider clock) : IBrokerOrders, IBrokerPortfolio, IBrokerFunds
{
    private const string DefaultBaseUrl = "https://api.mstock.trade/openapi/typeb";
    private const string Display = "mStock";

    public string Name => "mstock";
    public string DisplayName => "mStock by Mirae Asset";
    public BrokerCapability Capabilities => BrokerCapability.Orders | BrokerCapability.Portfolio | BrokerCapability.Funds;
    public IReadOnlyList<string> RequiredKeys { get; } = ["ApiKey", "ApiSecret"];

    private HttpClient Client() => http.CreateClient(Name);

    /// <summary>One call; the status and the body as read (null when it was not JSON). A request body goes on a GET as well, as mStock's quote and history calls need.</summary>
    private async Task<(int Status, JsonNode? Body)> SendAsync(BrokerSession session, HttpMethod method, string path, JsonNode? body, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(method, sessions.BaseUrl(Name, DefaultBaseUrl) + path);
        request.Headers.TryAddWithoutValidation("X-Mirae-Version", "1");
        request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {session.AuthToken}");
        request.Headers.TryAddWithoutValidation("X-PrivateKey", session.Key("ApiSecret") ?? "");
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

    /// <summary>mStock's success: status true, as a boolean or as the text true in either case.</summary>
    private static bool Success(JsonNode? node)
    {
        if (node is not JsonObject o || o["status"] is not JsonValue v) return false;
        if (v.TryGetValue<bool>(out var flag)) return flag;
        return v.TryGetValue<string>(out var text) && text is "true" or "True" or "TRUE";
    }

    /// <summary>The answer with a one-item list taken out of its list; a list of anything else stays as it is.</summary>
    private static JsonNode? Unwrap(JsonNode? body) => body is JsonArray { Count: > 0 } list ? list[0] : body;

    private static string? Message(JsonNode? body) => body is JsonObject o && Truthy(o["message"]) ? Str(o["message"]) : null;

    // ---- vocabulary ------------------------------------------------------------------------------------------

    private static string Product(string? product) => product switch { "CNC" => "DELIVERY", "NRML" => "CARRYFORWARD", "MIS" => "INTRADAY", _ => "INTRADAY" };

    private static string OrderType(string? type) => type switch { "MARKET" => "MARKET", "LIMIT" => "LIMIT", "SL" => "STOPLOSS_LIMIT", "SL-M" => "STOPLOSS_MARKET", _ => "MARKET" };

    private static string Variety(string? type) => type is "SL" or "SL-M" ? "STOPLOSS" : "NORMAL";

    private static string Text(decimal value) => value.ToString(CultureInfo.InvariantCulture);

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
        var body = new JsonObject
        {
            ["variety"] = Variety(order.PriceType), ["tradingsymbol"] = info.BrokerSymbol, ["symboltoken"] = info.Token, ["exchange"] = order.Exchange, ["transactiontype"] = order.Action.ToUpperInvariant(),
            ["ordertype"] = OrderType(order.PriceType), ["quantity"] = order.Quantity.ToString(CultureInfo.InvariantCulture), ["producttype"] = Product(order.Product), ["price"] = PyFloat(order.Price),
            ["triggerprice"] = PyFloat(order.TriggerPrice), ["squareoff"] = "0", ["stoploss"] = "0", ["trailingStopLoss"] = "", ["disclosedquantity"] = order.DisclosedQuantity.ToString(CultureInfo.InvariantCulture),
            ["duration"] = "DAY", ["ordertag"] = "",
        };

        (int Status, JsonNode? Body) reply;
        try
        {
            reply = await SendAsync(session, HttpMethod.Post, "/orders/regular", body, cancellationToken);
        }
        catch (BrokerException ex) when (ex.HttpStatus == 502)
        {
            return new BrokerCallResult(false, 502, $"The order was sent to {Display} but no reply came back, so it may have been placed. Check the order book on {Display} before trying again.", MayHaveBeenSent: true);
        }

        if (reply.Body is null) return new BrokerCallResult(false, 502, $"{Display} could not confirm the order, so it may have been placed. Check the order book on {Display} before trying again.", MayHaveBeenSent: true);
        var answer = Unwrap(reply.Body);
        if (!Success(answer)) return BrokerCallResult.Refused(reply.Status >= 400 ? reply.Status : 400, Message(answer) ?? "Failed to place order");
        var id = answer!["data"] is JsonObject data ? (Truthy(data["orderid"]) ? Str(data["orderid"]) : Truthy(data["uniqueorderid"]) ? Str(data["uniqueorderid"]) : null) : null;
        if (id is { Length: > 0 }) return BrokerCallResult.Success(id);
        return new BrokerCallResult(false, 502, $"{Display} accepted the order but sent no order number, so it cannot be followed. Check the order book on {Display} before trying again.", MayHaveBeenSent: true);
    }

    public async Task<BrokerCallResult> ModifyAsync(BrokerSession session, BrokerOrderChange change, CancellationToken cancellationToken)
    {
        var info = await symbols.FindAsync(Name, change.Symbol, change.Exchange, cancellationToken);
        if (info is null) return BrokerCallResult.Refused(400, $"{change.Symbol} on {change.Exchange} is not in the {Display} master contract. Check the symbol, or download the master contract again.");
        var body = new JsonObject
        {
            ["variety"] = Variety(change.PriceType), ["tradingsymbol"] = info.BrokerSymbol, ["symboltoken"] = info.Token, ["exchange"] = change.Exchange, ["transactiontype"] = change.Action.ToUpperInvariant(),
            ["orderid"] = change.OrderId, ["ordertype"] = OrderType(change.PriceType), ["quantity"] = change.Quantity.ToString(CultureInfo.InvariantCulture), ["producttype"] = Product(change.Product), ["duration"] = "DAY",
            ["price"] = PyFloat(change.Price), ["triggerprice"] = PyFloat(change.TriggerPrice), ["disclosedquantity"] = change.DisclosedQuantity.ToString(CultureInfo.InvariantCulture), ["modqty_remng"] = "0",
        };
        var (status, reply) = await SendAsync(session, HttpMethod.Put, $"/orders/regular/{change.OrderId}", body, cancellationToken);
        var answer = Unwrap(reply);
        if (Success(answer) || Message(answer) == "SUCCESS")
        {
            var id = answer is JsonObject o && o["data"] is JsonObject data ? (Truthy(data["orderid"]) ? Str(data["orderid"])! : change.OrderId).Trim() : change.OrderId;
            return BrokerCallResult.Success(id);
        }

        return BrokerCallResult.Refused(status >= 400 ? status : 400, Message(answer) ?? "Failed to modify order");
    }

    public async Task<BrokerCallResult> CancelAsync(BrokerSession session, string orderId, CancellationToken cancellationToken)
    {
        var (status, reply) = await SendAsync(session, HttpMethod.Delete, $"/orders/regular/{orderId}", new JsonObject { ["variety"] = "NORMAL", ["orderid"] = orderId }, cancellationToken);
        var answer = Unwrap(reply);
        if (Success(answer) || Message(answer) == "SUCCESS") return BrokerCallResult.Success(orderId);
        return BrokerCallResult.Refused(status >= 400 ? status : 400, Message(answer) ?? "Failed to cancel order");
    }

    // ---- books -----------------------------------------------------------------------------------------------

    /// <summary>The rows of a book. An answer with no data is an empty book; one that failed is an error naming the call, where OpenAlgo shows it as an empty book.</summary>
    private async Task<List<JsonObject>> ReadBookAsync(BrokerSession session, string path, string what, CancellationToken cancellationToken)
    {
        var (_, body) = await SendAsync(session, HttpMethod.Get, path, null, cancellationToken);
        if (body is JsonObject o && Success(o)) return (o["data"] as JsonArray)?.OfType<JsonObject>().ToList() ?? [];
        throw new BrokerException($"{Display} could not send the {what}: {Message(body) ?? "no reason was given."}", 500);
    }

    private static bool Derivative(string? instrumentType) => instrumentType is "OPTIDX" or "OPTSTK" or "FUTIDX" or "FUTSTK";

    /// <summary>mStock names a derivative on the cash exchange; OpenAlgo's exchange for it is the derivatives one.</summary>
    private static string Lookup(string? exchange, string? instrumentType) =>
        Derivative(instrumentType) ? exchange switch { "NSE" => "NFO", "BSE" => "BFO", _ => exchange ?? "" } : exchange ?? "";

    private async Task<string?> SymbolByTokenAsync(string? token, string exchange, CancellationToken cancellationToken) =>
        !string.IsNullOrEmpty(token) ? (await symbols.FindByTokenAsync(Name, token, exchange, cancellationToken))?.Symbol : null;

    private async Task<string?> SymbolByBrokerAsync(string? brokerSymbol, string exchange, CancellationToken cancellationToken) =>
        !string.IsNullOrEmpty(brokerSymbol) ? (await symbols.FindByBrokerSymbolAsync(Name, brokerSymbol, exchange, cancellationToken))?.Symbol : null;

    private static string StatusOf(string raw)
    {
        if (raw == "Traded" || raw.Contains("TRADE CONFIRMED", StringComparison.Ordinal)) return "complete";
        if (raw is "O-Pending" or "Pending" or "pending" or "O-Modified" or "o-modified") return "open";
        if (raw is "Rejected" or "rejected") return "rejected";
        if (raw is "Cancelled" or "cancelled" or "O-Cancelled" or "o-cancelled") return "cancelled";
        if (raw.Contains("trigger pending", StringComparison.OrdinalIgnoreCase)) return "trigger pending";
        return raw.ToLowerInvariant();
    }

    private static string PriceType(string raw) => raw switch
    {
        "STOPLOSS_LIMIT" or "STOP_LOSS" or "SL" => "SL", "STOPLOSS_MARKET" or "STOP_LOSS_MARKET" or "SL-M" => "SL-M", _ => raw,
    };

    /// <summary>The product as OpenAlgo shows it, decided on OpenAlgo's exchange: OpenAlgo itself decides it on the exchange mStock names, so a derivative that mStock puts on the cash exchange is left as CARRYFORWARD there, which no order can carry.</summary>
    private static string BookProduct(string exchange, string product) =>
        exchange is "NSE" or "BSE" && product == "DELIVERY" ? "CNC" : product == "INTRADAY" ? "MIS" : exchange is "NFO" or "MCX" or "BFO" or "CDS" && product == "CARRYFORWARD" ? "NRML" : product;

    private static double Round(double value) => BookFormatting.RoundPy(value, 2);

    public async Task<BrokerBook> GetOrderBookAsync(BrokerSession session, CancellationToken cancellationToken)
    {
        int buy = 0, sell = 0, completed = 0, open = 0, rejected = 0;
        var result = new JsonArray();
        foreach (var r in await ReadBookAsync(session, "/orders", "order book", cancellationToken))
        {
            var exchange = Str(r["exchange"]) ?? "";
            var instrumentType = Str(r["instrumenttype"]) ?? "";
            var lookup = Lookup(exchange, instrumentType);
            var symbol = Str(r["tradingsymbol"]) ?? "";
            var token = Str(r["symboltoken"]);
            if (!string.IsNullOrEmpty(token) && exchange.Length > 0)
                symbol = await SymbolByTokenAsync(token, lookup, cancellationToken) ?? (symbol.Length > 0 ? await SymbolByBrokerAsync(symbol, lookup, cancellationToken) : null) ?? symbol;
            var raw = Str(r["status"]) ?? "";
            var lower = raw.ToLowerInvariant();
            if (Str(r["transactiontype"]) == "BUY") buy++;
            else if (Str(r["transactiontype"]) == "SELL") sell++;
            if (lower.Contains("traded", StringComparison.Ordinal) || lower.Contains("complete", StringComparison.Ordinal)) completed++;
            else if (lower.Contains("pending", StringComparison.Ordinal) || lower.Contains("open", StringComparison.Ordinal)) open++;
            else if (lower.Contains("rejected", StringComparison.Ordinal) || lower.Contains("cancelled", StringComparison.Ordinal)) rejected++;

            var type = PriceType(Str(r["ordertype"]) ?? "");
            var status = StatusOf(raw);
            var shown = status == "complete" ? r["averageprice"] : type is "LIMIT" or "SL" ? r["price"] : r["averageprice"];
            var price = Truthy(shown) && Number(shown) is { } n ? n : 0.0;
            result.Add(new JsonObject
            {
                ["symbol"] = symbol, ["exchange"] = Lookup(exchange, instrumentType), ["action"] = Str(r["transactiontype"]) ?? "", ["quantity"] = r["quantity"]?.DeepClone() ?? 0, ["price"] = price,
                ["trigger_price"] = r.ContainsKey("triggerprice") ? r["triggerprice"]?.DeepClone() : 0.0, ["pricetype"] = type, ["product"] = BookProduct(lookup, Str(r["producttype"]) ?? ""),
                ["orderid"] = Str(r["orderid"]) ?? "", ["order_status"] = status, ["timestamp"] = Str(r["updatetime"]) ?? "",
            });
        }

        return new BrokerBook(result, new JsonObject
        {
            ["total_buy_orders"] = buy, ["total_sell_orders"] = sell, ["total_completed_orders"] = completed, ["total_open_orders"] = open, ["total_rejected_orders"] = rejected, ["total_cancelled_orders"] = 0,
        });
    }

    private static double? Number(JsonNode? node) => BookFormatting.Number(node) is { } n ? n : Str(node) is { } s && double.TryParse(s.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : null;

    private static long WholeOrZero(JsonNode? node)
    {
        if (node is JsonValue v && (v.TryGetValue<long>(out var l) || (v.TryGetValue<int>(out var i) && (l = i) == i))) return l;
        return BookFormatting.Number(node) is { } n ? (long)Math.Truncate(n) : Str(node) is { } s && long.TryParse(s.Trim(), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var parsed) ? parsed : 0;
    }

    public async Task<JsonArray> GetTradeBookAsync(BrokerSession session, CancellationToken cancellationToken)
    {
        var result = new JsonArray();
        foreach (var t in await ReadBookAsync(session, "/tradebook", "trade book", cancellationToken))
        {
            var exchange = Str(t["EXCHANGE"]) ?? "";
            var instrumentName = Str(t["INSTRUMENT_NAME"]) ?? "";
            var lookup = Lookup(exchange, instrumentName);
            var baseSymbol = Str(t["SYMBOL"]) ?? "";
            var token = Str(t["SEC_ID"]) ?? "";
            string symbol;
            if (exchange.Length == 0)
            {
                symbol = baseSymbol;
            }
            else
            {
                var brokerSymbol = exchange is "NSE" or "BSE" && instrumentName == "EQUITY" ? $"{baseSymbol}-EQ" : baseSymbol;
                symbol = (token.Length > 0 ? await SymbolByTokenAsync(token, lookup, cancellationToken) : null)
                    ?? (baseSymbol.Length > 0 ? await SymbolByBrokerAsync(brokerSymbol, lookup, cancellationToken) ?? baseSymbol : "");
            }

            var raw = Str(t["PRODUCT"]) ?? "";
            var product = lookup is "NSE" or "BSE" && raw == "DELIVERY" ? "CNC" : raw == "CNC" ? "CNC" : raw == "INTRADAY" ? "MIS" : lookup is "NFO" or "MCX" or "BFO" or "CDS" && raw == "CARRYFORWARD" ? "NRML" : raw;
            result.Add(new JsonObject
            {
                ["symbol"] = symbol, ["exchange"] = Lookup(exchange, instrumentName), ["product"] = product, ["action"] = (Str(t["BUY_SELL"]) ?? "").ToUpperInvariant(), ["quantity"] = WholeNumber(t["QUANTITY"]),
                ["average_price"] = Number(t["PRICE"]) ?? 0.0, ["trade_value"] = Number(t["TRADE_VALUE"]) ?? 0.0, ["orderid"] = Str(t["ORDER_NUMBER"]) ?? "", ["timestamp"] = Str(t["ORDER_DATE_TIME"]) ?? "",
            });
        }

        return result;
    }

    /// <summary>Python's int(x) in a try: a whole number or text of one, a number with decimals cut, anything else zero (text with decimals included).</summary>
    private static long WholeNumber(JsonNode? node)
    {
        if (node is JsonValue v)
        {
            if (v.TryGetValue<string>(out var text)) return long.TryParse(text.Trim(), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var parsed) ? parsed : 0;
            if (BookFormatting.Number(node) is { } n) return (long)Math.Truncate(n);
        }

        return 0;
    }

    public async Task<JsonArray> GetPositionsAsync(BrokerSession session, CancellationToken cancellationToken)
    {
        var result = new JsonArray();
        foreach (var p in await ReadBookAsync(session, "/portfolio/positions", "positions", cancellationToken))
        {
            var exchange = Str(p["exchange"]) ?? "";
            var instrumentType = Str(p["instrumenttype"]) ?? "";
            var lookup = Lookup(exchange, instrumentType);
            var token = Str(p["symboltoken"]);
            var name = Str(p["symbolname"]) ?? "";
            var symbol = "";
            if (!string.IsNullOrEmpty(token) && exchange.Length > 0)
                symbol = await SymbolByTokenAsync(token, lookup, cancellationToken) ?? (name.Length > 0 ? await SymbolByBrokerAsync(name, lookup, cancellationToken) ?? name : "");
            result.Add(new JsonObject
            {
                ["symbol"] = symbol, ["exchange"] = lookup, ["product"] = BookProduct(lookup, Str(p["producttype"]) ?? ""), ["quantity"] = WholeNumber(p["netqty"]), ["average_price"] = Number(p["avgnetprice"]) ?? 0.0,
                ["ltp"] = "NA", ["pnl"] = Number(p["netvalue"]) ?? 0.0,
            });
        }

        return result;
    }

    public async Task<BrokerBook> GetHoldingsAsync(BrokerSession session, CancellationToken cancellationToken)
    {
        var holdings = new JsonArray();
        double value = 0, invested = 0, profit = 0;
        foreach (var h in await ReadBookAsync(session, "/portfolio/holdings", "holdings", cancellationToken))
        {
            var exchange = Truthy(h["exchange"]) ? Str(h["exchange"])! : "NSE";
            var symbol = Str(h["tradingsymbol"]) ?? "";
            if (symbol.Length > 0) symbol = await SymbolByBrokerAsync(symbol, exchange, cancellationToken) ?? symbol;
            var quantity = h["quantity"] is JsonValue q && q.TryGetValue<string>(out _) ? WholeNumber(h["quantity"]) : h["quantity"]?.DeepClone() ?? 0;
            var pnl = Truthy(h["profitandloss"]) ? Number(h["profitandloss"]) ?? 0.0 : 0.0;
            var percent = Truthy(h["pnlpercentage"]) ? Number(h["pnlpercentage"]) ?? 0.0 : 0.0;
            holdings.Add(new JsonObject { ["symbol"] = symbol, ["exchange"] = exchange, ["quantity"] = quantity.DeepClone(), ["product"] = "CNC", ["pnl"] = Round(pnl), ["pnlpercent"] = Round(percent) });
            // A holding whose quantity or prices cannot be read is left out of the totals.
            var amount = Truthy(h["quantity"]) ? Number(h["quantity"]) : 0.0;
            var average = Truthy(h["averageprice"]) ? Number(h["averageprice"]) : 0.0;
            var last = Truthy(h["ltp"]) ? Number(h["ltp"]) : 0.0;
            var line = Truthy(h["profitandloss"]) ? Number(h["profitandloss"]) : 0.0;
            if (amount is { } a && average is { } avg && last is { } l && line is { } pl)
            {
                invested += a * avg;
                value += a * l;
                profit += pl;
            }
        }

        return new BrokerBook(holdings, new JsonObject
        {
            ["totalholdingvalue"] = Round(value), ["totalinvvalue"] = Round(invested), ["totalprofitandloss"] = Round(profit), ["totalpnlpercentage"] = invested > 0 ? Round(profit / invested * 100) : 0,
        });
    }

    // ---- funds -----------------------------------------------------------------------------------------------

    public async Task<JsonObject> GetFundsAsync(BrokerSession session, CancellationToken cancellationToken)
    {
        var (_, body) = await SendAsync(session, HttpMethod.Get, "/user/fundsummary", null, cancellationToken);
        if (!Success(body) || body!["data"] is not JsonArray { Count: > 0 } list || list[0] is not JsonObject row)
            throw new BrokerException($"{Display} could not send the funds: {Message(body) ?? "no reason was given."}", 500);
        string Money(string name)
        {
            var node = row[name];
            if (node is null || Str(node) is "None" or "") return "0.00";
            return Number(node) is { } n ? BookFormatting.Fixed2(n) : "0.00";
        }

        return new JsonObject
        {
            ["availablecash"] = Money("AVAILABLE_BALANCE"), ["collateral"] = Money("COLLATERALS"), ["m2mrealized"] = Money("REALISED_PROFITS"), ["m2munrealized"] = Money("MTM_COMBINED"), ["utiliseddebits"] = Money("AMOUNT_UTILIZED"),
        };
    }
}
