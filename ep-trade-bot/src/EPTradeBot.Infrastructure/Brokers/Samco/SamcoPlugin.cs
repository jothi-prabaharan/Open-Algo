using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using EPTradeBot.Core.Brokers;
using EPTradeBot.Core.Brokers.Gtt;
using static EPTradeBot.Infrastructure.Brokers.JsonRead;

namespace EPTradeBot.Infrastructure.Brokers.Samco;

/// <summary>
/// Samco (tradeapi.samco.in). Port of broker/samco: api/order_api.py, api/funds.py, mapping/transform_data.py and mapping/order_data.py. The session token goes in x-session-token. Samco's order calls take only
/// limit and stop-limit orders, so a market order is sent as a limit order a slab beyond the last price (read from the quote call) and a stop-market as a stop-limit a slab beyond its trigger, each with the slab
/// percentage as marketProtection. Books keep Samco's own text for prices and quantities, as OpenAlgo does.
/// </summary>
[BrokerPlugin]
public sealed partial class SamcoPlugin(IHttpClientFactory http, BrokerSessionFactory sessions, ISymbolMaster symbols, TimeProvider clock) : IBrokerOrders, IBrokerSquareOffPositions, IBrokerFunds, IBrokerMultiQuotes, IBrokerHistory
{
    private const string DefaultBaseUrl = "https://tradeapi.samco.in";

    public string Name => "samco";
    public string DisplayName => "Samco";
    public BrokerCapability Capabilities => BrokerCapability.Orders | BrokerCapability.Portfolio | BrokerCapability.Funds | BrokerCapability.Margin | BrokerCapability.Quotes | BrokerCapability.Depth | BrokerCapability.History;
    public IReadOnlyList<string> RequiredKeys { get; } = ["ApiKey", "ApiSecret"];

    // ---- number reading ---------------------------------------------------------------------------------------

    /// <summary>safe_float: None and empty are the default, commas are dropped, anything that is not a number is the default.</summary>
    private static double SafeFloat(JsonNode? node, double fallback = 0.0)
    {
        if (node is null) return fallback;
        if (BookFormatting.Number(node) is { } n) return n;
        if (Str(node) is not { } text || text.Length == 0) return fallback;
        return double.TryParse(text.Replace(",", "", StringComparison.Ordinal).Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : fallback;
    }

    private static long SafeInt(JsonNode? node, long fallback = 0) => double.IsFinite(SafeFloat(node, fallback)) ? (long)Math.Truncate(SafeFloat(node, fallback)) : fallback;

    /// <summary>float(value or 0) for a value that is a number or plain text.</summary>
    private static double Plain(JsonNode? node) => Truthy(node) ? SafeFloat(node) : 0.0;

    private static string PyFloat(double value)
    {
        var text = value.ToString("R", CultureInfo.InvariantCulture);
        return text.Contains('.', StringComparison.Ordinal) || text.Contains('E', StringComparison.Ordinal) ? text : text + ".0";
    }

    /// <summary>The slab as Samco's marketProtection ("0.5", "3"), printed the way %g prints it.</summary>
    private static string Percent(double value) => value.ToString("0.######", CultureInfo.InvariantCulture);

    /// <summary>str(decimal) for a request value: the number as a trader typed it, without a trailing zero tail.</summary>
    private static string Text(decimal value) => value.ToString(CultureInfo.InvariantCulture);

    // ---- calls -----------------------------------------------------------------------------------------------

    private HttpClient Client() => http.CreateClient(Name);

    private async Task<(int Status, JsonObject Body)> SendAsync(BrokerSession session, HttpMethod method, string path, JsonNode? body, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(method, sessions.BaseUrl(Name, DefaultBaseUrl) + path);
        request.Headers.TryAddWithoutValidation("x-session-token", session.AuthToken);
        request.Headers.TryAddWithoutValidation("Accept", "application/json");
        if (body is not null) request.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
        try
        {
            using var response = await Client().SendAsync(request, cancellationToken);
            var text = await response.Content.ReadAsStringAsync(cancellationToken);
            JsonObject? parsed = null;
            try
            {
                parsed = string.IsNullOrWhiteSpace(text) ? null : JsonNode.Parse(text) as JsonObject;
            }
            catch (System.Text.Json.JsonException)
            {
            }

            return ((int)response.StatusCode, parsed ?? []);
        }
        catch (HttpRequestException ex)
        {
            throw new BrokerException($"{DisplayName} did not answer. Check your connection, then try again.", 502, ex);
        }
    }

    private static bool Succeeded(JsonObject body) => Str(body["status"]) == "Success";

    private static string Reason(JsonObject body, string fallback) => Str(body["statusMessage"]) is { Length: > 0 } text ? text : fallback;

    // ---- vocabulary ------------------------------------------------------------------------------------------

    private static string Product(string? product) => (product ?? "") switch { "CNC" => "CNC", "NRML" => "NRML", "MIS" => "MIS", _ => "MIS" };

    private static string MapStatus(string? status) => (status ?? "").ToLowerInvariant() switch
    {
        "open" or "pending" or "ordered" or "trigger pending" or "after market order req received" => "open",
        "complete" or "completed" or "executed" or "filled" => "complete",
        "cancelled" or "canceled" => "cancelled",
        "rejected" => "rejected",
        var other => other,
    };

    private static readonly HashSet<string> Placeholders = ["", "NA", "N/A", "--", "-", "None", "null"];

    private static string CleanText(JsonNode? node)
    {
        var text = Truthy(node) ? (Str(node) ?? "").Trim() : "";
        return Placeholders.Contains(text) ? "" : text;
    }

    // ---- orders ----------------------------------------------------------------------------------------------

    private static readonly Regex SafeOrderId = new("^[A-Za-z0-9_-]+$", RegexOptions.Compiled);

    private sealed record Resolved(string OrderType, string? Price, string? Protection);

    /// <summary>resolve_order_type: a market order becomes a limit order a slab beyond the last price, a stop-market a stop-limit a slab beyond its trigger. Refuses with the reason when the price cannot be worked out.</summary>
    private async Task<Resolved> ResolveAsync(BrokerSession session, SymbolRecord info, string priceType, string symbol, string exchange, string action, decimal price, decimal trigger, CancellationToken cancellationToken)
    {
        var instrument = MarketPriceProtection.InstrumentType(null, symbol);
        double? tick = info.TickSize is { } t && t > 0 ? t : null;
        if (priceType == "MARKET")
        {
            double ltp;
            try
            {
                ltp = (await QuoteAsync(session, info, cancellationToken)).Ltp;
            }
            catch (BrokerException ex)
            {
                throw new BrokerException($"{DisplayName} could not give the last price of {symbol}, so the market order was not sent: {ex.Message}", ex.HttpStatus == 502 ? 502 : 400);
            }

            if (ltp <= 0) throw new BrokerException($"{DisplayName} shows no last price for {symbol}, so the market order was not sent. Place a limit order instead.", 400);
            return new Resolved("L", PyFloat(MarketPriceProtection.ProtectedPrice(ltp, action, instrument, tick)), Percent(MarketPriceProtection.Percentage(ltp, instrument)));
        }

        if (priceType == "SL-M")
        {
            var level = (double)trigger;
            if (level <= 0) throw new BrokerException($"A stop-market order for {symbol} needs a trigger price, so it was not sent.", 400);
            return new Resolved("SL", PyFloat(MarketPriceProtection.ProtectedPrice(level, action, instrument, tick)), Percent(MarketPriceProtection.Percentage(level, instrument)));
        }

        return new Resolved(priceType == "LIMIT" ? "L" : priceType == "SL" ? "SL" : "MKT", Text(price), null);
    }

    public async Task<BrokerCallResult> PlaceAsync(BrokerSession session, BrokerOrder order, CancellationToken cancellationToken)
    {
        var info = await symbols.FindAsync(Name, order.Symbol, order.Exchange, cancellationToken);
        if (info is null) return BrokerCallResult.Refused(400, $"{order.Symbol} on {order.Exchange} is not in the {DisplayName} master contract. Check the symbol, or download the master contract again.");

        Resolved resolved;
        try
        {
            resolved = await ResolveAsync(session, info, order.PriceType, order.Symbol, order.Exchange, order.Action.ToUpperInvariant(), order.Price, order.TriggerPrice, cancellationToken);
        }
        catch (BrokerException ex)
        {
            return BrokerCallResult.Refused(ex.HttpStatus, ex.Message);
        }

        var payload = new JsonObject
        {
            ["symbolName"] = info.BrokerSymbol, ["exchange"] = order.Exchange, ["transactionType"] = order.Action.ToUpperInvariant(), ["orderType"] = resolved.OrderType,
            ["quantity"] = order.Quantity.ToString(CultureInfo.InvariantCulture), ["disclosedQuantity"] = order.DisclosedQuantity.ToString(CultureInfo.InvariantCulture), ["orderValidity"] = "DAY",
            ["productType"] = Product(order.Product), ["afterMarketOrderFlag"] = "NO",
        };
        if (resolved.OrderType is "L" or "SL") payload["price"] = resolved.Price;
        if (resolved.OrderType == "SL" || order.PriceType is "SL" or "SL-M") payload["triggerPrice"] = Text(order.TriggerPrice);
        if (resolved.Protection is not null) payload["marketProtection"] = resolved.Protection;

        (int Status, JsonObject Body) reply;
        try
        {
            reply = await SendAsync(session, HttpMethod.Post, "/order/placeOrder", payload, cancellationToken);
        }
        catch (BrokerException ex) when (ex.HttpStatus == 502)
        {
            return new BrokerCallResult(false, 502, $"The order was sent to {DisplayName} but no reply came back, so it may have been placed. Check the order book on {DisplayName} before trying again.", MayHaveBeenSent: true);
        }

        if (Succeeded(reply.Body))
        {
            if (Str(reply.Body["orderNumber"]) is { Length: > 0 } id) return BrokerCallResult.Success(id);
            return new BrokerCallResult(false, 502, $"{DisplayName} accepted the order but sent no order number, so it cannot be followed. Check the order book on {DisplayName} before trying again.", MayHaveBeenSent: true);
        }

        return BrokerCallResult.Refused(reply.Status is >= 400 ? reply.Status : 400, Reason(reply.Body, "Failed to place order"));
    }

    public async Task<BrokerCallResult> ModifyAsync(BrokerSession session, BrokerOrderChange change, CancellationToken cancellationToken)
    {
        if (!SafeOrderId.IsMatch(change.OrderId ?? "")) return BrokerCallResult.Refused(400, $"Invalid orderid: '{change.OrderId}'");
        var info = await symbols.FindAsync(Name, change.Symbol, change.Exchange, cancellationToken);
        if (info is null) return BrokerCallResult.Refused(400, $"{change.Symbol} on {change.Exchange} is not in the {DisplayName} master contract. Check the symbol, or download the master contract again.");

        Resolved resolved;
        try
        {
            resolved = await ResolveAsync(session, info, change.PriceType, change.Symbol, change.Exchange, change.Action.ToUpperInvariant(), change.Price, change.TriggerPrice, cancellationToken);
        }
        catch (BrokerException ex)
        {
            return BrokerCallResult.Refused(ex.HttpStatus, ex.Message);
        }

        var payload = new JsonObject { ["orderType"] = resolved.OrderType, ["quantity"] = change.Quantity.ToString(CultureInfo.InvariantCulture), ["orderValidity"] = "DAY" };
        if (change.DisclosedQuantity > 0) payload["disclosedQuantity"] = change.DisclosedQuantity.ToString(CultureInfo.InvariantCulture);
        if (resolved.OrderType is "L" or "SL") payload["price"] = resolved.Price;
        if (resolved.OrderType == "SL" || change.PriceType is "SL" or "SL-M") payload["triggerPrice"] = Text(change.TriggerPrice);
        if (resolved.Protection is not null) payload["marketProtection"] = resolved.Protection;

        var reply = await SendAsync(session, HttpMethod.Put, $"/order/modifyOrder/{change.OrderId}", payload, cancellationToken);
        if (Succeeded(reply.Body))
        {
            var id = Truthy(reply.Body["orderNumber"]) ? Str(reply.Body["orderNumber"]) : Truthy(reply.Body["ordernumber"]) ? Str(reply.Body["ordernumber"]) : change.OrderId;
            return BrokerCallResult.Success(id!);
        }

        return BrokerCallResult.Refused(reply.Status is >= 400 ? reply.Status : 400, Reason(reply.Body, "Failed to modify order"));
    }

    public async Task<BrokerCallResult> CancelAsync(BrokerSession session, string orderId, CancellationToken cancellationToken)
    {
        if (!SafeOrderId.IsMatch(orderId ?? "")) return BrokerCallResult.Refused(400, $"Invalid orderid: '{orderId}'");
        var reply = await SendAsync(session, HttpMethod.Delete, $"/order/cancelOrder?orderNumber={Uri.EscapeDataString(orderId!)}", null, cancellationToken);
        if (Succeeded(reply.Body)) return BrokerCallResult.Success(orderId!);
        return BrokerCallResult.Refused(reply.Status is >= 400 ? reply.Status : 400, Reason(reply.Body, "Failed to cancel order"));
    }

    // ---- books -----------------------------------------------------------------------------------------------

    private async Task<JsonObject> ReadBookAsync(BrokerSession session, string path, string what, CancellationToken cancellationToken)
    {
        var (_, body) = await SendAsync(session, HttpMethod.Get, path, null, cancellationToken);
        if (!Succeeded(body)) throw new BrokerException($"{DisplayName} could not send the {what}: {Reason(body, "no reason was given.")}", 500);
        return body;
    }

    private async Task<string> SymbolAsync(string? tradingSymbol, string exchange, CancellationToken cancellationToken)
    {
        var raw = tradingSymbol ?? "";
        return (await symbols.FindByBrokerSymbolAsync(Name, raw, exchange, cancellationToken))?.Symbol is { Length: > 0 } found ? found : raw;
    }

    private static JsonNode Raw(JsonObject row, string key, JsonNode fallback) => row[key]?.DeepClone() ?? fallback;

    private static List<JsonObject> Details(JsonObject body, string key) => (body[key] as JsonArray)?.OfType<JsonObject>().ToList() ?? [];

    public async Task<BrokerBook> GetOrderBookAsync(BrokerSession session, CancellationToken cancellationToken)
    {
        var body = await ReadBookAsync(session, "/order/orderBook", "order book", cancellationToken);
        int buy = 0, sell = 0, completed = 0, open = 0, rejected = 0;
        var result = new JsonArray();
        foreach (var r in Details(body, "orderBookDetails"))
        {
            var side = Str(r["transactionType"]);
            if (side == "BUY") buy++;
            else if (side == "SELL") sell++;
            var rawStatus = (Str(r["orderStatus"]) ?? "").ToLowerInvariant();
            if (rawStatus is "complete" or "executed") completed++;
            else if (rawStatus is "open" or "pending" or "trigger pending") open++;
            else if (rawStatus == "rejected") rejected++;

            var type = Str(r["orderType"]) ?? "";
            var pricetype = type switch { "L" => Truthy(r["marketProtection"]) ? "MARKET" : "LIMIT", "MKT" => "MARKET", _ => type };
            var exchange = Str(r["exchange"]) ?? "";
            var pending = r["unfilledQuantity"] is not null ? r["unfilledQuantity"] : r["pendingQuantity"];
            var average = SafeFloat(r["averagePrice"]);
            if (average == 0.0) average = SafeFloat(r["fillPrice"]);
            result.Add(new JsonObject
            {
                ["symbol"] = await SymbolAsync(Str(r["tradingSymbol"]), exchange, cancellationToken), ["exchange"] = exchange, ["action"] = side ?? "", ["quantity"] = Raw(r, "totalQuanity", 0), ["price"] = Raw(r, "orderPrice", 0.0),
                ["trigger_price"] = Raw(r, "triggerPrice", 0.0), ["pricetype"] = pricetype, ["product"] = Str(r["productCode"]) ?? "", ["orderid"] = Str(r["orderNumber"]) ?? "", ["order_status"] = MapStatus(Str(r["orderStatus"])),
                ["timestamp"] = Raw(r, "orderTime", ""), ["filled_quantity"] = SafeInt(r["filledQuantity"]), ["pending_quantity"] = SafeInt(pending), ["average_price"] = average, ["rejection_reason"] = CleanText(r["rejectionReason"]),
            });
        }

        return new BrokerBook(result, new JsonObject { ["total_buy_orders"] = buy, ["total_sell_orders"] = sell, ["total_completed_orders"] = completed, ["total_open_orders"] = open, ["total_rejected_orders"] = rejected });
    }

    public async Task<JsonArray> GetTradeBookAsync(BrokerSession session, CancellationToken cancellationToken)
    {
        var body = await ReadBookAsync(session, "/trade/tradeBook", "trade book", cancellationToken);
        var result = new JsonArray();
        foreach (var r in Details(body, "tradeBookDetails"))
        {
            var exchange = Str(r["exchange"]) ?? "";
            result.Add(new JsonObject
            {
                ["symbol"] = await SymbolAsync(Str(r["tradingSymbol"]), exchange, cancellationToken), ["exchange"] = exchange, ["product"] = Str(r["productCode"]) ?? "", ["action"] = Str(r["transactionType"]) ?? "",
                ["quantity"] = Raw(r, "filledQuantity", 0), ["average_price"] = Raw(r, "tradePrice", 0.0), ["trade_value"] = Raw(r, "orderValue", 0), ["orderid"] = Str(r["orderNumber"]) ?? "", ["timestamp"] = Raw(r, "tradeTime", ""),
            });
        }

        return result;
    }

    /// <summary>One position book of Samco's two (DAY for today, NET for what carries forward).</summary>
    private async Task<List<JsonObject>> PositionsAsync(BrokerSession session, string positionType, CancellationToken cancellationToken)
    {
        var body = await ReadBookAsync(session, $"/position/getPositions?positionType={positionType}", $"{positionType} positions", cancellationToken);
        return Details(body, "positionDetails");
    }

    private async Task<JsonObject> PositionRowAsync(JsonObject r, CancellationToken cancellationToken)
    {
        var exchange = Str(r["exchange"]) ?? "";
        var side = Str(r["transactionType"]);
        var average = SafeFloat(side == "SELL" ? r["averageSellPrice"] : r["averageBuyPrice"]);
        var quantity = SafeInt(r["netQuantity"]);
        if (side == "SELL" && quantity > 0) quantity = -quantity;
        return new JsonObject
        {
            ["symbol"] = await SymbolAsync(Str(r["tradingSymbol"]), exchange, cancellationToken), ["exchange"] = exchange, ["product"] = Str(r["productCode"]) ?? "", ["quantity"] = quantity.ToString(CultureInfo.InvariantCulture),
            ["average_price"] = BookFormatting.Fixed2(average), ["ltp"] = BookFormatting.Round2(SafeFloat(r["lastTradedPrice"])),
            ["pnl"] = BookFormatting.Round2(BookFormatting.RoundPy(Plain(r["realizedGainAndLoss"]) + Plain(r["unrealizedGainAndLoss"]), 2)),
        };
    }

    public async Task<JsonArray> GetPositionsAsync(BrokerSession session, CancellationToken cancellationToken)
    {
        var result = new JsonArray();
        foreach (var r in await PositionsAsync(session, "DAY", cancellationToken)) result.Add(await PositionRowAsync(r, cancellationToken));
        return result;
    }

    /// <summary>Today's book and the carried-forward book together. A position a trader carried forward and traded again today is in both, so it is counted once, from today's book.</summary>
    public async Task<JsonArray> GetSquareOffPositionsAsync(BrokerSession session, CancellationToken cancellationToken)
    {
        var merged = new Dictionary<(string?, string?, string?), JsonObject>();
        foreach (var type in new[] { "DAY", "NET" })
        {
            foreach (var r in await PositionsAsync(session, type, cancellationToken)) merged.TryAdd((Str(r["tradingSymbol"]), Str(r["exchange"]), Str(r["productCode"])), r);
        }

        var result = new JsonArray();
        foreach (var r in merged.Values) result.Add(await PositionRowAsync(r, cancellationToken));
        return result;
    }

    public async Task<BrokerBook> GetHoldingsAsync(BrokerSession session, CancellationToken cancellationToken)
    {
        var body = await ReadBookAsync(session, "/holding/getHoldings", "holdings", cancellationToken);
        var holdings = new JsonArray();
        foreach (var r in Details(body, "holdingDetails"))
        {
            var exchange = Str(r["exchange"]) ?? "NSE";
            var pnl = Plain(r["totalGainAndLoss"]);
            var value = Plain(r["holdingsValue"]);
            var percent = value > 0 ? (value - pnl) != 0 ? BookFormatting.RoundPy(pnl / (value - pnl) * 100, 2) : 0.0 : 0.0;
            holdings.Add(new JsonObject
            {
                ["symbol"] = await SymbolAsync(Str(r["tradingSymbol"]), exchange, cancellationToken), ["exchange"] = exchange, ["quantity"] = (long)Math.Truncate(Plain(r["holdingsQuantity"])), ["product"] = "CNC",
                ["pnl"] = BookFormatting.Round2(pnl), ["pnlpercent"] = percent,
            });
        }

        if (body["holdingSummary"] is not JsonObject summary) return new BrokerBook(holdings, new JsonObject { ["totalholdingvalue"] = 0, ["totalinvvalue"] = 0, ["totalprofitandloss"] = 0, ["totalpnlpercentage"] = 0 });
        var portfolio = Plain(summary["portfolioValue"]);
        var total = Plain(summary["totalGainAndLossAmount"]);
        var invested = portfolio - total;
        return new BrokerBook(holdings, new JsonObject
        {
            ["totalholdingvalue"] = BookFormatting.Round2(portfolio), ["totalinvvalue"] = BookFormatting.Round2(invested), ["totalprofitandloss"] = BookFormatting.Round2(total),
            ["totalpnlpercentage"] = invested != 0 ? BookFormatting.RoundPy(total / invested * 100, 2) : 0.0,
        });
    }

    // ---- funds -----------------------------------------------------------------------------------------------

    public async Task<JsonObject> GetFundsAsync(BrokerSession session, CancellationToken cancellationToken)
    {
        var body = await ReadBookAsync(session, "/limit/getLimits", "funds", cancellationToken);
        var equity = body["equityLimit"] as JsonObject ?? [];
        return new JsonObject
        {
            ["availablecash"] = BookFormatting.Fixed2(Plain(equity["netAvailableMargin"])), ["collateral"] = BookFormatting.Fixed2(Plain(equity["collateralMarginAgainstShares"])), ["m2mrealized"] = "0.00",
            ["m2munrealized"] = "0.00", ["utiliseddebits"] = BookFormatting.Fixed2(Plain(equity["marginUsed"])),
        };
    }
}
