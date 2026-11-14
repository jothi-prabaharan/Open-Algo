using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using EPTradeBot.Core.Brokers;
using EPTradeBot.Core.Brokers.Gtt;
using static EPTradeBot.Infrastructure.Brokers.JsonRead;

namespace EPTradeBot.Infrastructure.Brokers.Firstock;

/// <summary>
/// Firstock (api.firstock.in/V1): port of broker/firstock api/order_api.py, api/funds.py, mapping/transform_data.py and mapping/order_data.py. Every call is a POST with the session key (jKey) and the user id in the body;
/// the user id is the broker key without its last four characters. A market order and a stop-market are sent as a limit and a stop-limit order a slab beyond the last price (read with /getQuote), and go as they are when
/// no price can be had. A name with an ampersand is sent with it escaped. Books keep Firstock's own text for prices and quantities, as OpenAlgo does.
/// </summary>
[BrokerPlugin]
public sealed partial class FirstockPlugin(IHttpClientFactory http, BrokerSessionFactory sessions, ISymbolMaster symbols) : IBrokerOrders, IBrokerPortfolio, IBrokerFunds, IBrokerMultiQuotes, IBrokerHistory
{
    private const string DefaultBaseUrl = "https://api.firstock.in/V1";

    public string Name => "firstock";
    public string DisplayName => "Firstock";
    public BrokerCapability Capabilities => BrokerCapability.Orders | BrokerCapability.Portfolio | BrokerCapability.Funds | BrokerCapability.Margin | BrokerCapability.Quotes | BrokerCapability.Depth | BrokerCapability.History;
    public IReadOnlyList<string> RequiredKeys { get; } = ["ApiKey", "ApiSecret"];

    // ---- calls -----------------------------------------------------------------------------------------------

    private static string UserId(BrokerSession session)
    {
        var key = session.Key("ApiKey") ?? "";
        if (key.Length <= 4) throw new BrokerException($"The {Display} key in the server's settings is too short to hold a user id. Correct it, then sign in again.", 409);
        return key[..^4];
    }

    private const string Display = "Firstock";

    private HttpClient Client() => http.CreateClient(Name);

    /// <summary>One POST: the session key and user id are added to the body. The status, and the body as an object (an answer that is not JSON is an empty one).</summary>
    private async Task<(int Status, JsonObject Body)> PostAsync(BrokerSession session, string path, JsonObject body, CancellationToken cancellationToken, bool accept = false)
    {
        body["jKey"] = session.AuthToken;
        body["userId"] = UserId(session);
        using var request = new HttpRequestMessage(HttpMethod.Post, sessions.BaseUrl(Name, DefaultBaseUrl) + path) { Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json") };
        if (accept) request.Headers.TryAddWithoutValidation("Accept", "application/json");
        try
        {
            using var response = await Client().SendAsync(request, cancellationToken);
            return ((int)response.StatusCode, await ReadAsync(response, cancellationToken) as JsonObject ?? []);
        }
        catch (HttpRequestException ex)
        {
            throw new BrokerException($"{DisplayName} did not answer. Check your connection, then try again.", 502, ex);
        }
    }

    private static bool Succeeded(JsonObject body) => Str(body["status"]) == "success";

    private static string Reason(JsonObject body, string fallback) => Str((body["error"] as JsonObject)?["message"]) is { Length: > 0 } m ? m : Str(body["message"]) is { Length: > 0 } t ? t : fallback;

    // ---- vocabulary ------------------------------------------------------------------------------------------

    private static string Product(string? product) => product switch { "CNC" => "C", "NRML" => "M", "MIS" => "I", _ => "I" };

    private static string ShownProduct(string exchange, string? product) =>
        exchange is "NSE" or "BSE" && product == "C" ? "CNC" : product == "I" ? "MIS" : exchange is "NFO" or "MCX" or "BFO" or "CDS" && product == "M" ? "NRML" : product ?? "";

    private static string Escape(string brokerSymbol) => brokerSymbol.Replace("&", "%26", StringComparison.Ordinal);

    private static string PyFloat(double value)
    {
        var text = value.ToString("R", CultureInfo.InvariantCulture);
        return text.Contains('.', StringComparison.Ordinal) || text.Contains('E', StringComparison.Ordinal) ? text : text + ".0";
    }

    private static string Text(decimal value) => value.ToString(CultureInfo.InvariantCulture);

    private static string MapExchange(string exchange) => exchange switch { "NSE_INDEX" => "NSE", "BSE_INDEX" => "BSE", _ => exchange };

    // ---- orders ----------------------------------------------------------------------------------------------

    /// <summary>The price and order type a market order or a stop-market is sent with: a limit order a slab beyond the last price, or, when no price can be had, as it is. Null in the third place means "as it is".</summary>
    private async Task<(string Price, string Type, bool Protected)> ProtectAsync(BrokerSession session, SymbolRecord info, string symbol, string exchange, string action, string priceType, string price, CancellationToken cancellationToken)
    {
        var original = priceType == "MARKET" ? "MKT" : priceType == "LIMIT" ? "LMT" : priceType == "SL" ? "SL-LMT" : priceType == "SL-M" ? "SL-MKT" : "MKT";
        if (priceType is not ("MARKET" or "SL-M")) return (price, original, false);
        try
        {
            var quote = await QuoteAsync(session, info, exchange, cancellationToken);
            if (quote.Ltp > 0)
            {
                double? tick = info.TickSize is { } t && t > 0 ? t : null;
                var protectedPrice = MarketPriceProtection.ProtectedPrice(quote.Ltp, action, MarketPriceProtection.InstrumentType(null, symbol), tick);
                return (PyFloat(protectedPrice), priceType == "MARKET" ? "LMT" : "SL-LMT", true);
            }
        }
        catch (BrokerException)
        {
        }

        return (price, original, false);
    }

    public async Task<BrokerCallResult> PlaceAsync(BrokerSession session, BrokerOrder order, CancellationToken cancellationToken)
    {
        var info = await symbols.FindAsync(Name, order.Symbol, order.Exchange, cancellationToken);
        if (info is null) return BrokerCallResult.Refused(400, $"{order.Symbol} on {order.Exchange} is not in the {DisplayName} master contract. Check the symbol, or download the master contract again.");

        var action = order.Action.ToUpperInvariant();
        var (price, type, _) = await ProtectAsync(session, info, order.Symbol, order.Exchange, action, order.PriceType, Text(order.Price), cancellationToken);
        var body = new JsonObject
        {
            ["exchange"] = order.Exchange, ["tradingSymbol"] = Escape(info.BrokerSymbol), ["quantity"] = order.Quantity.ToString(CultureInfo.InvariantCulture), ["price"] = price, ["triggerPrice"] = Text(order.TriggerPrice),
            ["product"] = Product(order.Product), ["transactionType"] = action == "BUY" ? "B" : "S", ["priceType"] = type, ["retention"] = "DAY", ["mkt_protection"] = "0",
            ["remarks"] = string.IsNullOrEmpty(order.Strategy) ? "Place Order" : order.Strategy,
        };

        (int Status, JsonObject Body) reply;
        try
        {
            reply = await PostAsync(session, "/placeOrder", body, cancellationToken);
        }
        catch (BrokerException ex) when (ex.HttpStatus == 502)
        {
            return new BrokerCallResult(false, 502, $"The order was sent to {DisplayName} but no reply came back, so it may have been placed. Check the order book on {DisplayName} before trying again.", MayHaveBeenSent: true);
        }

        if (reply.Body.Count == 0 && reply.Status >= 500)
            return new BrokerCallResult(false, 502, $"{DisplayName} could not confirm the order, so it may have been placed. Check the order book on {DisplayName} before trying again.", MayHaveBeenSent: true);
        if (Succeeded(reply.Body))
        {
            if (Str((reply.Body["data"] as JsonObject)?["orderNumber"]) is { Length: > 0 } id) return BrokerCallResult.Success(id);
            return new BrokerCallResult(false, 502, $"{DisplayName} accepted the order but sent no order number, so it cannot be followed. Check the order book on {DisplayName} before trying again.", MayHaveBeenSent: true);
        }

        return BrokerCallResult.Refused(reply.Status is >= 400 ? reply.Status : 400, Reason(reply.Body, "Failed to place order"));
    }

    public async Task<BrokerCallResult> ModifyAsync(BrokerSession session, BrokerOrderChange change, CancellationToken cancellationToken)
    {
        var info = await symbols.FindAsync(Name, change.Symbol, change.Exchange, cancellationToken);
        var brokerSymbol = Escape(info?.BrokerSymbol ?? change.Symbol);
        var action = change.Action.ToUpperInvariant();
        var protection = "0";
        string price = Text(change.Price), type;
        if (change.PriceType is "MARKET" or "SL-M")
        {
            var converted = false;
            (price, type, converted) = info is null ? (price, change.PriceType == "MARKET" ? "MKT" : "SL-MKT", false) : await ProtectAsync(session, info, change.Symbol, change.Exchange, action, change.PriceType, price, cancellationToken);
            if (!converted) protection = "1";
        }
        else
        {
            type = change.PriceType == "LIMIT" ? "LMT" : change.PriceType == "SL" ? "SL-LMT" : "MKT";
        }

        var body = new JsonObject
        {
            ["exchange"] = change.Exchange, ["orderNumber"] = change.OrderId, ["priceType"] = type, ["price"] = price, ["quantity"] = change.Quantity.ToString(CultureInfo.InvariantCulture), ["tradingSymbol"] = brokerSymbol,
            ["triggerPrice"] = Text(change.TriggerPrice), ["retention"] = "DAY", ["mkt_protection"] = protection,
        };
        if (!string.IsNullOrEmpty(change.Product)) body["product"] = Product(change.Product);

        var reply = await PostAsync(session, "/modifyOrder", body, cancellationToken);
        if (Succeeded(reply.Body)) return BrokerCallResult.Success(change.OrderId);
        return BrokerCallResult.Refused(reply.Status is >= 400 ? reply.Status : 400, Reason(reply.Body, "Failed to modify order"));
    }

    public async Task<BrokerCallResult> CancelAsync(BrokerSession session, string orderId, CancellationToken cancellationToken)
    {
        var reply = await PostAsync(session, "/cancelOrder", new JsonObject { ["orderNumber"] = orderId }, cancellationToken);
        if (Succeeded(reply.Body)) return BrokerCallResult.Success(orderId);
        var code = int.TryParse(Str(reply.Body["code"]), NumberStyles.Integer, CultureInfo.InvariantCulture, out var c) && c >= 400 ? c : 400;
        return BrokerCallResult.Refused(code, Reason(reply.Body, "Failed to cancel order"));
    }

    // ---- books -----------------------------------------------------------------------------------------------

    private async Task<List<JsonObject>> ReadBookAsync(BrokerSession session, string path, string what, CancellationToken cancellationToken)
    {
        var (_, body) = await PostAsync(session, path, [], cancellationToken);
        if (!Succeeded(body)) throw new BrokerException($"{DisplayName} could not send the {what}: {Reason(body, "no reason was given.")}", 500);
        return body["data"] switch { JsonArray list => list.OfType<JsonObject>().ToList(), JsonObject one => [one], _ => [] };
    }

    private async Task<string> SymbolAsync(JsonObject row, string fallbackName, CancellationToken cancellationToken)
    {
        var token = Str(row["token"]);
        var exchange = Str(row["exchange"]) ?? "";
        if (!string.IsNullOrEmpty(token) && (await symbols.FindByTokenAsync(Name, token, exchange, cancellationToken))?.Symbol is { Length: > 0 } found) return found;
        return fallbackName;
    }

    private static JsonNode Raw(JsonObject row, string key, JsonNode fallback) => row[key]?.DeepClone() ?? fallback;

    private static string PriceType(string? raw)
    {
        var key = (raw ?? "").ToUpperInvariant().Replace("_", "-", StringComparison.Ordinal).Trim();
        return key switch
        {
            "MKT" or "MARKET" => "MARKET", "LMT" or "LIMIT" => "LIMIT", "SL-MKT" or "SL-MARKET" or "SL-M" => "SL-M", "SL-LMT" or "SL-LIMIT" or "SL" => "SL", _ => key,
        };
    }

    private static string OrderStatus(string? raw)
    {
        var upper = (raw ?? "").ToUpperInvariant();
        return upper switch
        {
            "COMPLETE" => "complete", "OPEN" => "open", "REJECTED" => "rejected", "CANCELED" or "CANCELLED" => "cancelled", "TRIGGER PENDING" or "TRIGGER_PENDING" => "trigger pending", "PENDING" => "open", _ => upper.ToLowerInvariant(),
        };
    }

    public async Task<BrokerBook> GetOrderBookAsync(BrokerSession session, CancellationToken cancellationToken)
    {
        int buy = 0, sell = 0, completed = 0, open = 0, rejected = 0;
        var result = new JsonArray();
        foreach (var r in await ReadBookAsync(session, "/orderBook", "order book", cancellationToken))
        {
            var exchange = Str(r["exchange"]) ?? "";
            var side = Str(r["transactionType"]) ?? "";
            if (side == "B") { buy++; side = "BUY"; }
            else if (side == "S") { sell++; side = "SELL"; }

            var status = (Str(r["status"]) ?? "").ToUpperInvariant();
            if (status == "COMPLETE") completed++;
            else if (status is "OPEN" or "TRIGGER PENDING" or "TRIGGER_PENDING" or "PENDING") open++;
            else if (status == "REJECTED") rejected++;

            var trigger = r["triggerPrice"];
            result.Add(new JsonObject
            {
                ["symbol"] = await SymbolAsync(r, Str(r["tradingSymbol"]) ?? "", cancellationToken), ["exchange"] = exchange, ["action"] = side, ["quantity"] = Raw(r, "quantity", "0"), ["price"] = Raw(r, "price", "0.00"),
                ["trigger_price"] = trigger is null || Str(trigger) == "" ? "0.00" : trigger.DeepClone(), ["pricetype"] = PriceType(Str(r["priceType"])), ["product"] = ShownProduct(exchange, Str(r["product"])),
                ["orderid"] = Str(r["orderNumber"]) ?? "", ["order_status"] = OrderStatus(Str(r["status"])), ["timestamp"] = Raw(r, "orderTime", ""),
            });
        }

        return new BrokerBook(result, new JsonObject { ["total_buy_orders"] = buy, ["total_sell_orders"] = sell, ["total_completed_orders"] = completed, ["total_open_orders"] = open, ["total_rejected_orders"] = rejected });
    }

    private static double Num(JsonNode? node, double fallback = 0)
    {
        var text = Str(node);
        return text is not null && double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : BookFormatting.Number(node) ?? fallback;
    }

    public async Task<JsonArray> GetTradeBookAsync(BrokerSession session, CancellationToken cancellationToken)
    {
        var result = new JsonArray();
        foreach (var r in await ReadBookAsync(session, "/tradeBook", "trade book", cancellationToken))
        {
            var exchange = Str(r["exchange"]) ?? "";
            var side = Str(r["transactionType"]) ?? "";
            side = side == "B" ? "BUY" : side == "S" ? "SELL" : side;
            result.Add(new JsonObject
            {
                ["symbol"] = await SymbolAsync(r, Str(r["tradingSymbol"]) ?? "", cancellationToken), ["exchange"] = exchange, ["product"] = ShownProduct(exchange, Str(r["product"])), ["action"] = side,
                ["quantity"] = Raw(r, "fillQuantity", "0"), ["average_price"] = Raw(r, "fillPrice", "0.00"), ["trade_value"] = BookFormatting.Fixed2(Num(r["fillQuantity"]) * Num(r["fillPrice"])),
                ["orderid"] = Str(r["orderNumber"]) ?? "", ["timestamp"] = Raw(r, "fillTime", ""),
            });
        }

        return result;
    }

    public async Task<JsonArray> GetPositionsAsync(BrokerSession session, CancellationToken cancellationToken)
    {
        var result = new JsonArray();
        foreach (var r in await ReadBookAsync(session, "/positionBook", "positions", cancellationToken))
        {
            var exchange = Str(r["exchange"]) ?? "";
            result.Add(new JsonObject
            {
                ["symbol"] = await SymbolAsync(r, Str(r["tradingSymbol"]) ?? "", cancellationToken), ["exchange"] = exchange, ["product"] = ShownProduct(exchange, Str(r["product"])), ["quantity"] = Raw(r, "netQuantity", "0"),
                ["average_price"] = Raw(r, "netAveragePrice", "0.00"), ["last_price"] = "0.00", ["pnl"] = Raw(r, "RealizedPNL", "0.00"), ["day_buy_quantity"] = Raw(r, "dayBuyQuantity", "0"),
                ["day_sell_quantity"] = Raw(r, "daySellQuantity", "0"), ["day_buy_amount"] = Raw(r, "dayBuyAmount", "0.00"), ["day_sell_amount"] = Raw(r, "daySellAmount", "0.00"),
                ["day_buy_average_price"] = Raw(r, "dayBuyAveragePrice", "0.00"), ["day_sell_average_price"] = Raw(r, "daySellAveragePrice", "0.00"), ["unrealized_pnl"] = Raw(r, "unrealizedMTOM", "0.00"),
                ["realized_pnl"] = Raw(r, "RealizedPNL", "0.00"),
            });
        }

        return result;
    }

    /// <summary>Firstock's holdings call gives the symbols a holding trades under and nothing else (no quantity, no price), so the book is a list of symbols with zeros, as OpenAlgo shows it. A row that lists its exchanges (the shape the call also answers in) is left out there, and here.</summary>
    public async Task<BrokerBook> GetHoldingsAsync(BrokerSession session, CancellationToken cancellationToken)
    {
        var holdings = new JsonArray();
        foreach (var r in await ReadBookAsync(session, "/holdings", "holdings", cancellationToken))
        {
            if (!r.ContainsKey("exchange") || !r.ContainsKey("tradingSymbol")) continue;
            var trading = (Str(r["tradingSymbol"]) ?? "").Replace("-EQ", "", StringComparison.Ordinal);
            var symbol = Truthy(r["token"]) ? await SymbolAsync(r, trading, cancellationToken) : trading;
            holdings.Add(new JsonObject { ["symbol"] = symbol, ["exchange"] = Str(r["exchange"]) ?? "", ["quantity"] = 0, ["product"] = "CNC", ["pnl"] = 0.0, ["pnlpercent"] = 0.0 });
        }

        return new BrokerBook(holdings, new JsonObject { ["totalholdingvalue"] = 0.0, ["totalinvvalue"] = 0.0, ["totalprofitandloss"] = 0.0, ["totalpnlpercentage"] = 0.0 });
    }

    // ---- funds -----------------------------------------------------------------------------------------------

    public async Task<JsonObject> GetFundsAsync(BrokerSession session, CancellationToken cancellationToken)
    {
        var (_, body) = await PostAsync(session, "/limit", [], cancellationToken);
        if (!Succeeded(body)) throw new BrokerException($"{DisplayName} could not send the funds: {Reason(body, "no reason was given.")}", 500);
        var data = body["data"] as JsonObject ?? [];
        var available = Num(data["cash"]) + Num(data["payin"]) - Num(data["marginused"]);
        return new JsonObject
        {
            ["availablecash"] = BookFormatting.Fixed2(available), ["collateral"] = BookFormatting.Fixed2(Num(data["brkcollamt"])), ["m2mrealized"] = "0.00", ["m2munrealized"] = "0.00", ["utiliseddebits"] = BookFormatting.Fixed2(Num(data["marginused"])),
        };
    }
}
