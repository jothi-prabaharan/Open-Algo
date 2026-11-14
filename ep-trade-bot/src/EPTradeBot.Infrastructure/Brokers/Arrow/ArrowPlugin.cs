using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using EPTradeBot.Core.Brokers;
using static EPTradeBot.Infrastructure.Brokers.JsonRead;

namespace EPTradeBot.Infrastructure.Brokers.Arrow;

/// <summary>
/// Arrow (edge.arrow.trade). Port of broker/arrow: api/order_api.py, api/funds.py, mapping/transform_data.py and mapping/order_data.py. Every call carries the application id
/// and the access token in two headers of their own (appID and token). Prices and quantities travel as text; a market order is sent as MKT with market price protection (mpp);
/// the books show the broker's own text values unchanged. Not ported yet: margin, sign-in and the feed.
/// </summary>
[BrokerPlugin]
public sealed partial class ArrowPlugin(IHttpClientFactory http, BrokerSessionFactory sessions, ISymbolMaster symbols) : IBrokerOrders, IBrokerPortfolio, IBrokerFunds, IBrokerMultiQuotes, IBrokerHistory
{
    private const string DefaultBaseUrl = "https://edge.arrow.trade";

    public string Name => "arrow";
    public string DisplayName => "Arrow";
    public BrokerCapability Capabilities => BrokerCapability.Orders | BrokerCapability.Portfolio | BrokerCapability.Funds | BrokerCapability.Quotes | BrokerCapability.Depth | BrokerCapability.History;
    public IReadOnlyList<string> RequiredKeys { get; } = ["ApiKey", "ApiSecret"];

    // ---- vocabulary ------------------------------------------------------------------------------------------

    private static string OrderType(string? priceType) => (priceType ?? "") switch { "MARKET" => "MKT", "LIMIT" => "LMT", "SL" => "SL-LMT", "SL-M" => "SL-MKT", _ => "MKT" };

    private static string ReverseOrderType(string? type) => type switch { "LMT" => "LIMIT", "MKT" => "MARKET", "SL-LMT" => "SL", "SL-MKT" => "SL-M", _ => type ?? "" };

    private static string Product(string? product) => (product ?? "") switch { "CNC" => "C", "NRML" => "M", "MIS" => "I", _ => "I" };

    private static string? ReverseProduct(string? code) => code switch { "C" => "CNC", "M" => "NRML", "I" => "MIS", _ => null };

    private static string Status(string? raw) => raw switch
    {
        "COMPLETE" => "complete", "OPEN" or "PENDING" => "open", "TRIGGER_PENDING" => "trigger pending", "CANCELLED" => "cancelled", "REJECTED" => "rejected", _ => raw ?? "",
    };

    /// <summary>Python's str(float): the shortest text that reads back, with ".0" on a whole number.</summary>
    private static string PyFloat(decimal value)
    {
        var text = ((double)value).ToString("R", CultureInfo.InvariantCulture);
        return text.Contains('.', StringComparison.Ordinal) || text.Contains('E', StringComparison.Ordinal) ? text : text + ".0";
    }

    /// <summary>The value as the text the broker sent, a number as its own digits.</summary>
    private static JsonNode? Same(JsonNode? node) => node?.DeepClone();

    private static double D(JsonNode? node) => Truthy(node) ? Float(node) : 0.0;

    // ---- calls -----------------------------------------------------------------------------------------------

    private HttpClient Client() => http.CreateClient(Name);

    private async Task<(int Status, JsonNode? Body)> CallAsync(BrokerSession session, HttpMethod method, string path, JsonNode? body, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(method, sessions.BaseUrl(Name, DefaultBaseUrl) + path);
        if (session.Key("ApiKey") is { } appId) request.Headers.TryAddWithoutValidation("appID", appId);
        request.Headers.TryAddWithoutValidation("token", session.AuthToken);
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

    /// <summary>A read: Arrow's own refusal (an error status) becomes a failure a trader can read.</summary>
    private async Task<JsonObject> ReadAsync(BrokerSession session, string path, string what, CancellationToken cancellationToken)
    {
        var (status, body) = await CallAsync(session, HttpMethod.Get, path, null, cancellationToken);
        if (status >= 400) throw new BrokerException($"{DisplayName} could not send the {what}: {Str((body as JsonObject)?["message"]) ?? "no reason was given."}", 500);
        return body as JsonObject ?? [];
    }

    private async Task<string?> OaSymbolAsync(string? brokerSymbol, string? exchange, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(brokerSymbol) || exchange is null) return null;
        return (await symbols.FindByBrokerSymbolAsync(Name, brokerSymbol, exchange, cancellationToken))?.Symbol;
    }

    private static string Message(JsonNode? body, string fallback) => Str((body as JsonObject)?["message"]) is { Length: > 0 } m ? m : fallback;

    // ---- orders ----------------------------------------------------------------------------------------------

    private async Task<string?> BrokerSymbolAsync(string symbol, string exchange, CancellationToken cancellationToken) =>
        (await symbols.FindAsync(Name, symbol, exchange, cancellationToken))?.BrokerSymbol;

    public async Task<BrokerCallResult> PlaceAsync(BrokerSession session, BrokerOrder order, CancellationToken cancellationToken)
    {
        var type = OrderType(order.PriceType);
        var strategy = string.IsNullOrEmpty(order.Strategy) ? "openalgo" : order.Strategy;
        var payload = new JsonObject
        {
            ["exchange"] = order.Exchange, ["symbol"] = await BrokerSymbolAsync(order.Symbol, order.Exchange, cancellationToken), ["quantity"] = order.Quantity.ToString(CultureInfo.InvariantCulture),
            ["transactionType"] = order.Action.Equals("BUY", StringComparison.OrdinalIgnoreCase) ? "B" : "S", ["order"] = type, ["product"] = Product(order.Product), ["price"] = PyFloat(order.Price),
            ["validity"] = "DAY", ["disclosedQty"] = order.DisclosedQuantity.ToString(CultureInfo.InvariantCulture), ["remarks"] = strategy.Length > 16 ? strategy[..16] : strategy,
        };
        if (type == "MKT") payload["mpp"] = true;
        if (type is "SL-LMT" or "SL-MKT") payload["triggerPrice"] = PyFloat(order.TriggerPrice);

        // An order is sent once: a retry after a lost answer could place it twice.
        (int Status, JsonNode? Body) reply;
        try
        {
            reply = await CallAsync(session, HttpMethod.Post, "/order/regular", payload, cancellationToken);
        }
        catch (BrokerException ex) when (ex.HttpStatus == 502)
        {
            return new BrokerCallResult(false, 502, $"The order was sent to {DisplayName} but no reply came back, so it may have been placed. Check the order book on {DisplayName} before trying again.", MayHaveBeenSent: true);
        }

        if (Str((reply.Body as JsonObject)?["status"]) == "success" && Str(((reply.Body as JsonObject)?["data"] as JsonObject)?["orderNo"]) is { Length: > 0 } id) return BrokerCallResult.Success(id);
        return BrokerCallResult.Refused(reply.Status >= 400 ? reply.Status : 400, Message(reply.Body, "Failed to place order"));
    }

    public async Task<BrokerCallResult> ModifyAsync(BrokerSession session, BrokerOrderChange change, CancellationToken cancellationToken)
    {
        var type = OrderType(change.PriceType);
        var payload = new JsonObject
        {
            ["exchange"] = change.Exchange, ["symbol"] = await BrokerSymbolAsync(change.Symbol, change.Exchange, cancellationToken), ["quantity"] = change.Quantity.ToString(CultureInfo.InvariantCulture),
            ["order"] = type, ["product"] = Product(change.Product), ["price"] = PyFloat(change.Price), ["validity"] = "DAY", ["disclosedQty"] = change.DisclosedQuantity.ToString(CultureInfo.InvariantCulture),
        };
        if (type is "SL-LMT" or "SL-MKT") payload["triggerPrice"] = PyFloat(change.TriggerPrice);

        var (status, body) = await CallAsync(session, HttpMethod.Patch, $"/order/regular/{Uri.EscapeDataString(change.OrderId)}", payload, cancellationToken);
        if (Str((body as JsonObject)?["status"]) == "success")
            return BrokerCallResult.Success(Str(((body as JsonObject)?["data"] as JsonObject)?["orderNo"]) is { Length: > 0 } id ? id : change.OrderId);
        return BrokerCallResult.Refused(status >= 400 ? status : 400, Message(body, "Failed to modify order"));
    }

    public async Task<BrokerCallResult> CancelAsync(BrokerSession session, string orderId, CancellationToken cancellationToken)
    {
        var (status, body) = await CallAsync(session, HttpMethod.Delete, $"/order/regular/{Uri.EscapeDataString(orderId)}", null, cancellationToken);
        // A cancel answers with a plain text acknowledgement on success, so the status is the signal.
        if (status < 400) return BrokerCallResult.Success(orderId);
        return BrokerCallResult.Refused(500, $"Failed to cancel order: {Message(body, $"{DisplayName} did not accept the cancel.")}");
    }

    // ---- books -----------------------------------------------------------------------------------------------

    private static List<JsonObject> Rows(JsonObject body) => (body["data"] as JsonArray ?? []).OfType<JsonObject>().ToList();

    public async Task<BrokerBook> GetOrderBookAsync(BrokerSession session, CancellationToken cancellationToken)
    {
        int buy = 0, sell = 0, completed = 0, open = 0, rejected = 0;
        var rows = new JsonArray();
        foreach (var o in Rows(await ReadAsync(session, "/user/orders", "order book", cancellationToken)))
        {
            var exchange = Str(o["exchange"]);
            var brokerSymbol = Str(o["symbol"]);
            var symbol = Truthy(o["symbol"]) ? await OaSymbolAsync(brokerSymbol, exchange, cancellationToken) : (o.ContainsKey("symbol") ? null : "");
            var rawSide = Str(o["transactionType"]);
            var side = rawSide switch { "B" => "BUY", "S" => "SELL", _ => rawSide };
            var rawStatus = (Str(o["orderStatus"]) ?? "").ToUpperInvariant().Replace(' ', '_');
            if (side?.ToUpperInvariant() is "BUY" or "B") buy++;
            else if (side?.ToUpperInvariant() is "SELL" or "S") sell++;
            if (rawStatus == "COMPLETE") completed++;
            else if (rawStatus is "OPEN" or "PENDING" or "TRIGGER_PENDING") open++;
            else if (rawStatus == "REJECTED") rejected++;

            var product = ReverseProduct(Str(o["product"])) is { } p ? JsonValue.Create(p) : Same(o["product"]);
            rows.Add(new JsonObject
            {
                ["symbol"] = symbol, ["exchange"] = Same(o["exchange"]) ?? "", ["action"] = side ?? "", ["quantity"] = Same(o["quantity"]) ?? 0, ["price"] = Same(o["price"]) ?? 0.0,
                ["trigger_price"] = Same(o["triggerPrice"]) ?? 0.0, ["pricetype"] = ReverseOrderType(Str(o["order"])), ["product"] = product,
                ["orderid"] = Truthy(o["orderNo"]) ? Same(o["orderNo"]) : Truthy(o["id"]) ? Same(o["id"]) : "", ["order_status"] = Status(Str(o["orderStatus"])),
                ["timestamp"] = Truthy(o["orderTime"]) ? Same(o["orderTime"]) : Truthy(o["requestTime"]) ? Same(o["requestTime"]) : "",
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
        foreach (var t in Rows(await ReadAsync(session, "/user/trades", "trade book", cancellationToken)))
        {
            var exchange = Str(t["exchange"]);
            var symbol = Truthy(t["symbol"]) ? await OaSymbolAsync(Str(t["symbol"]), exchange, cancellationToken) : null;
            var rawSide = Str(t["transactionType"]);
            // A fill's quantity and price: its own fields, else the order-style ones; a missing or empty figure is zero.
            var quantity = Truthy(t["fillQuantity"]) || t.ContainsKey("fillQuantity") ? t["fillQuantity"] : t["quantity"];
            var price = Truthy(t["fillPrice"]) || t.ContainsKey("fillPrice") ? t["fillPrice"] : t["averagePrice"];
            var q = Truthy(quantity) ? Same(quantity) : JsonValue.Create(0);
            var pr = Truthy(price) ? Same(price) : JsonValue.Create(0.0);
            result.Add(new JsonObject
            {
                ["symbol"] = symbol, ["exchange"] = Same(t["exchange"]) ?? "", ["product"] = ReverseProduct(Str(t["product"])) is { } p ? JsonValue.Create(p) : Same(t["product"]) ?? "",
                ["action"] = rawSide switch { "B" => "BUY", "S" => "SELL", _ => rawSide ?? "" }, ["quantity"] = q, ["average_price"] = pr, ["trade_value"] = Float(q) * Float(pr),
                ["orderid"] = Truthy(t["orderNo"]) ? Same(t["orderNo"]) : Truthy(t["id"]) ? Same(t["id"]) : "",
                ["timestamp"] = Truthy(t["fillTime"]) ? Same(t["fillTime"]) : Truthy(t["orderTime"]) ? Same(t["orderTime"]) : "",
            });
        }

        return result;
    }

    public async Task<JsonArray> GetPositionsAsync(BrokerSession session, CancellationToken cancellationToken)
    {
        var result = new JsonArray();
        foreach (var p in Rows(await ReadAsync(session, "/user/positions", "positions", cancellationToken)))
        {
            var exchange = Str(p["exchange"]);
            var symbol = Truthy(p["symbol"]) ? await OaSymbolAsync(Str(p["symbol"]), exchange, cancellationToken) : (p.ContainsKey("symbol") ? null : "");
            result.Add(new JsonObject
            {
                ["symbol"] = symbol, ["exchange"] = Same(p["exchange"]) ?? "", ["product"] = ReverseProduct(Str(p["product"])) is { } code ? JsonValue.Create(code) : Same(p["product"]) ?? "",
                ["quantity"] = p.ContainsKey("qty") ? Same(p["qty"]) : "0", ["pnl"] = BookFormatting.Round2(D(p["realisedPnL"]) + D(p["unrealisedMarkToMarket"])),
                ["average_price"] = BookFormatting.Fixed2(D(p["avgPrice"])), ["ltp"] = BookFormatting.Round2(D(p["ltp"])),
            });
        }

        return result;
    }

    public async Task<BrokerBook> GetHoldingsAsync(BrokerSession session, CancellationToken cancellationToken)
    {
        var holdings = new JsonArray();
        double value = 0, invested = 0, pnlTotal = 0;
        foreach (var h in Rows(await ReadAsync(session, "/user/holdings", "holdings", cancellationToken)))
        {
            var primary = (h["symbols"] as JsonArray)?.FirstOrDefault() as JsonObject ?? [];
            var brokerSymbol = Truthy(primary["tradingSymbol"]) ? Str(primary["tradingSymbol"]) : Str(primary["symbol"]);
            var exchange = Str(primary["exchange"]);
            string? symbol;
            string exchangeOut;
            if (!string.IsNullOrEmpty(brokerSymbol) && !string.IsNullOrEmpty(exchange))
            {
                symbol = await OaSymbolAsync(brokerSymbol, exchange, cancellationToken);
                exchangeOut = exchange;
            }
            else
            {
                symbol = brokerSymbol ?? "";
                exchangeOut = exchange ?? "";
            }

            var average = D(h["avgPrice"]);
            var ltp = D(h["ltp"]);
            var quantity = D(h["qty"]);
            value += ltp * quantity;
            invested += average * quantity;
            pnlTotal += D(h["pnl"]);
            holdings.Add(new JsonObject
            {
                ["symbol"] = symbol, ["exchange"] = exchangeOut, ["quantity"] = h.ContainsKey("qty") ? Same(h["qty"]) : 0, ["product"] = "CNC", ["average_price"] = average,
                ["pnl"] = BookFormatting.Round2(D(h["pnl"])), ["pnlpercent"] = average == 0 ? 0.0 : BookFormatting.Round2((ltp - average) / average * 100),
            });
        }

        return new BrokerBook(holdings, new JsonObject
        {
            ["totalholdingvalue"] = value, ["totalinvvalue"] = invested, ["totalprofitandloss"] = pnlTotal, ["totalpnlpercentage"] = invested != 0 ? pnlTotal / invested * 100 : 0.0,
        });
    }

    // ---- funds -----------------------------------------------------------------------------------------------

    private static double Number(JsonNode? node) => Float(node, 0.0);

    public async Task<JsonObject> GetFundsAsync(BrokerSession session, CancellationToken cancellationToken)
    {
        var (status, body) = await CallAsync(session, HttpMethod.Get, "/user/limits", null, cancellationToken);
        var payload = body as JsonObject ?? [];
        if (status >= 400 || Str(payload["status"]) is { } s && s != "success")
            throw new BrokerException($"{DisplayName} could not send the funds: {Message(payload, "no reason was given.")}", 500);

        var data = payload["data"] as JsonObject ?? [];
        var margin = data["margin"] as JsonObject ?? [];
        var allocations = (data["allocations"] as JsonArray ?? []).OfType<JsonObject>().ToList();
        var allocated = Number(margin["allocated"]);
        var utilized = Number(margin["utilized"]);
        var collateral = allocations.Sum(a => Number(a["nonCashCurrent"]));
        return new JsonObject
        {
            ["availablecash"] = BookFormatting.Fixed2(allocated - utilized), ["collateral"] = BookFormatting.Fixed2(collateral), ["m2munrealized"] = BookFormatting.Fixed2(Number(margin["unrealizedPnl"])),
            ["m2mrealized"] = BookFormatting.Fixed2(Number(margin["realizedPnl"])), ["utiliseddebits"] = BookFormatting.Fixed2(utilized),
        };
    }
}
