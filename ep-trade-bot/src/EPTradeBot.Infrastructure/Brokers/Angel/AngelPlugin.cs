using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using EPTradeBot.Core.Brokers;
using Microsoft.Extensions.Logging;
using static EPTradeBot.Infrastructure.Brokers.JsonRead;

namespace EPTradeBot.Infrastructure.Brokers.Angel;

/// <summary>
/// Angel One SmartAPI. Port of broker/angel (api/order_api.py, api/funds.py, api/data.py quotes
/// and depth, mapping/transform_data.py, mapping/order_data.py), held to
/// tools/contract-fixtures/angel.fixtures.json. SmartAPI sends many numbers as strings; they
/// are passed through as the books show them. Not ported yet, and refused rather than guessed:
/// history, GTT, margin, login and the SmartAPI feed.
/// </summary>
[BrokerPlugin]
public sealed class AngelPlugin(IHttpClientFactory http, BrokerSessionFactory sessions, ISymbolMaster symbols, ILogger<AngelPlugin> logger)
    : IBrokerOrders, IBrokerPortfolio, IBrokerFunds, IBrokerQuotes
{
    private const string BaseUrl = "https://apiconnect.angelone.in";

    public string Name => "angel";
    public string DisplayName => "Angel One";
    public BrokerCapability Capabilities => BrokerCapability.Orders | BrokerCapability.Portfolio | BrokerCapability.Funds | BrokerCapability.Quotes | BrokerCapability.Depth;

    /// <summary>ApiKey: the SmartAPI private key, sent as X-PrivateKey.</summary>
    public IReadOnlyList<string> RequiredKeys { get; } = ["ApiKey"];

    private static string OrderType(string priceType) => priceType switch
    {
        "LIMIT" => "LIMIT", "SL" => "STOPLOSS_LIMIT", "SL-M" => "STOPLOSS_MARKET", _ => "MARKET",
    };

    private static string Product(string product) => product switch { "CNC" => "DELIVERY", "NRML" => "CARRYFORWARD", _ => "INTRADAY" };

    private static string Variety(string priceType) => priceType is "SL" or "SL-M" ? "STOPLOSS" : "NORMAL";

    /// <summary>The book's product, renamed only when the symbol is known (as OpenAlgo does).</summary>
    private static string BookProduct(string exchange, string product) =>
        (exchange is "NSE" or "BSE") && product == "DELIVERY" ? "CNC"
        : product == "INTRADAY" ? "MIS"
        : exchange is "NFO" or "MCX" or "BFO" or "CDS" && product == "CARRYFORWARD" ? "NRML"
        : product;

    // ---- orders ----------------------------------------------------------------------------------------------

    public async Task<BrokerCallResult> PlaceAsync(BrokerSession session, BrokerOrder order, CancellationToken cancellationToken)
    {
        var instrument = await symbols.FindAsync(Name, order.Symbol, order.Exchange, cancellationToken);
        if (instrument is null) return BrokerCallResult.Refused(400, $"{order.Symbol} on {order.Exchange} is not in the Angel One master contract. Check the symbol, or download the master contract again.");

        var tag = "oa" + Guid.NewGuid().ToString("N")[..16];
        var body = new JsonObject
        {
            ["variety"] = Variety(order.PriceType), ["tradingsymbol"] = instrument.BrokerSymbol, ["symboltoken"] = instrument.Token,
            ["transactiontype"] = order.Action.ToUpperInvariant(), ["exchange"] = order.Exchange, ["ordertype"] = OrderType(order.PriceType),
            ["producttype"] = Product(order.Product), ["duration"] = "DAY", ["price"] = (double)order.Price,
            ["triggerprice"] = (double)order.TriggerPrice, ["squareoff"] = "0", ["stoploss"] = (double)order.TriggerPrice,
            ["quantity"] = order.Quantity, ["ordertag"] = tag,
        };

        JsonObject? reply = null;
        var status = 502;
        try
        {
            using var request = Request(session, HttpMethod.Post, "/rest/secure/angelbroking/order/v1/placeOrder", body);
            using var response = await Client().SendAsync(request, cancellationToken);
            status = (int)response.StatusCode;
            reply = await ReadAsync(response, cancellationToken) as JsonObject;
        }
        catch (HttpRequestException ex)
        {
            logger.LogWarning(ex, "Angel One order POST got no answer; reconciling tag {Tag}.", tag);
        }

        if (reply?["status"] is JsonValue s && s.TryGetValue<bool>(out var ok))
        {
            if (ok && Str(reply["data"]?["orderid"]) is { Length: > 0 } id) return BrokerCallResult.Success(id);
            if (!ok) return BrokerCallResult.Refused(status == 200 ? 500 : status, Str(reply["message"]) ?? "Failed to place order");
        }

        // No usable answer: the order may exist. Look for it by its tag and fields before reporting.
        var found = await FindByTagAsync(session, tag, instrument, order, cancellationToken);
        return found is not null
            ? BrokerCallResult.Success(found)
            : new BrokerCallResult(false, status == 200 ? 500 : status,
                "Angel One sent no usable reply and the order is not in the order book yet, so it may or may not have been placed. Check the order book on Angel One before trying again.",
                MayHaveBeenSent: true);
    }

    private async Task<string?> FindByTagAsync(BrokerSession session, string tag, SymbolRecord instrument, BrokerOrder order, CancellationToken cancellationToken)
    {
        try
        {
            var book = await GetAsync(session, "/rest/secure/angelbroking/order/v1/getOrderBook", cancellationToken);
            var matches = (book["data"] as JsonArray ?? []).OfType<JsonObject>().Where(o =>
                Str(o["ordertag"]) == tag && Str(o["tradingsymbol"]) == instrument.BrokerSymbol && Str(o["symboltoken"]) == instrument.Token
                && Str(o["exchange"]) == order.Exchange && Str(o["transactiontype"]) == order.Action.ToUpperInvariant()
                && Str(o["quantity"]) == order.Quantity.ToString(CultureInfo.InvariantCulture)).ToList();
            return matches.Count == 1 ? Str(matches[0]["orderid"]) : null;
        }
        catch (BrokerException ex)
        {
            logger.LogWarning(ex, "Could not read the Angel One order book while reconciling tag {Tag}.", tag);
            return null;
        }
    }

    public async Task<BrokerCallResult> ModifyAsync(BrokerSession session, BrokerOrderChange change, CancellationToken cancellationToken)
    {
        var instrument = await symbols.FindAsync(Name, change.Symbol, change.Exchange, cancellationToken);
        if (instrument is null) return BrokerCallResult.Refused(400, $"{change.Symbol} on {change.Exchange} is not in the Angel One master contract.");
        var body = new JsonObject
        {
            ["variety"] = Variety(change.PriceType), ["orderid"] = change.OrderId, ["ordertype"] = OrderType(change.PriceType),
            ["producttype"] = Product(change.Product), ["duration"] = "DAY", ["price"] = (double)change.Price, ["quantity"] = change.Quantity,
            ["tradingsymbol"] = instrument.BrokerSymbol, ["symboltoken"] = instrument.Token, ["exchange"] = change.Exchange,
            ["disclosedquantity"] = change.DisclosedQuantity, ["stoploss"] = (double)change.TriggerPrice, ["triggerprice"] = (double)change.TriggerPrice,
        };
        using var request = Request(session, HttpMethod.Post, "/rest/secure/angelbroking/order/v1/modifyOrder", body);
        using var response = await SendAsync(request, cancellationToken);
        var reply = await ReadAsync(response, cancellationToken) as JsonObject;
        var accepted = reply?["status"] is JsonValue v && ((v.TryGetValue<bool>(out var b) && b) || Str(v) == "true") || Str(reply?["message"]) == "SUCCESS";
        if (accepted && Str(reply!["data"]?["orderid"]) is { Length: > 0 } id) return BrokerCallResult.Success(id);
        // OpenAlgo answers success for a refused modify (Angel replies HTTP 200); the refusal is reported here.
        return BrokerCallResult.Refused(response.IsSuccessStatusCode ? 400 : (int)response.StatusCode, Str(reply?["message"]) ?? "Failed to modify order");
    }

    public async Task<BrokerCallResult> CancelAsync(BrokerSession session, string orderId, CancellationToken cancellationToken)
    {
        using var request = Request(session, HttpMethod.Post, "/rest/secure/angelbroking/order/v1/cancelOrder", new JsonObject { ["variety"] = "NORMAL", ["orderid"] = orderId });
        using var response = await SendAsync(request, cancellationToken);
        var reply = await ReadAsync(response, cancellationToken) as JsonObject;
        if (Truthy(reply?["status"])) return BrokerCallResult.Success(orderId);
        return BrokerCallResult.Refused(response.IsSuccessStatusCode ? 400 : (int)response.StatusCode, Str(reply?["message"]) ?? "Failed to cancel order");
    }

    // ---- books -----------------------------------------------------------------------------------------------

    public async Task<BrokerBook> GetOrderBookAsync(BrokerSession session, CancellationToken cancellationToken)
    {
        var rows = Rows(await GetAsync(session, "/rest/secure/angelbroking/order/v1/getOrderBook", cancellationToken));
        int buy = 0, sell = 0, completed = 0, open = 0, rejected = 0;
        var orders = new JsonArray();
        foreach (var o in rows)
        {
            var exchange = Str(o["exchange"]) ?? "";
            var record = await symbols.FindByTokenAsync(Name, Str(o["symboltoken"]) ?? "", exchange, cancellationToken);
            var side = Str(o["transactiontype"]);
            if (side == "BUY") buy++;
            else if (side == "SELL") sell++;
            var status = Str(o["status"]);
            if (status == "complete") completed++;
            else if (status == "open") open++;
            else if (status == "rejected") rejected++;
            var type = Str(o["ordertype"]) ?? "";
            orders.Add(new JsonObject
            {
                ["symbol"] = record?.Symbol ?? Get(o, "tradingsymbol", ""), ["exchange"] = Get(o, "exchange", ""),
                ["action"] = Get(o, "transactiontype", ""), ["quantity"] = Get(o, "quantity", 0),
                // The average fill price when there is one, else the order price.
                ["price"] = Truthy(o["averageprice"]) ? o["averageprice"]!.DeepClone() : Get(o, "price", 0.0),
                ["trigger_price"] = Get(o, "triggerprice", 0.0),
                ["pricetype"] = type switch { "STOPLOSS_LIMIT" => "SL", "STOPLOSS_MARKET" => "SL-M", _ => type },
                ["product"] = record is null ? Get(o, "producttype", "") : BookProduct(exchange, Str(o["producttype"]) ?? ""),
                ["orderid"] = Get(o, "orderid", ""), ["order_status"] = Get(o, "status", ""), ["timestamp"] = Get(o, "updatetime", ""),
            });
        }

        return new BrokerBook(orders, new JsonObject
        {
            ["total_buy_orders"] = buy, ["total_sell_orders"] = sell, ["total_completed_orders"] = completed,
            ["total_open_orders"] = open, ["total_rejected_orders"] = rejected,
        });
    }

    public async Task<JsonArray> GetTradeBookAsync(BrokerSession session, CancellationToken cancellationToken)
    {
        var rows = Rows(await GetAsync(session, "/rest/secure/angelbroking/order/v1/getTradeBook", cancellationToken));
        var trades = new JsonArray();
        foreach (var t in rows)
        {
            var exchange = Str(t["exchange"]) ?? "";
            var record = await symbols.FindByBrokerSymbolAsync(Name, Str(t["tradingsymbol"]) ?? "", exchange, cancellationToken);
            trades.Add(new JsonObject
            {
                ["symbol"] = record?.Symbol ?? Get(t, "tradingsymbol", ""), ["exchange"] = Get(t, "exchange", ""),
                ["product"] = record is null ? Get(t, "producttype", "") : BookProduct(exchange, Str(t["producttype"]) ?? ""),
                ["action"] = Get(t, "transactiontype", ""),
                // SmartAPI reports a trade's size as fillsize; OpenAlgo reads a "quantity" field it never sends, so every trade showed 0.
                ["quantity"] = Get(t, "quantity", null) ?? Get(t, "fillsize", 0),
                ["average_price"] = Get(t, "fillprice", 0.0), ["trade_value"] = Get(t, "tradevalue", 0),
                ["orderid"] = Get(t, "orderid", ""), ["timestamp"] = Get(t, "filltime", ""),
            });
        }

        return trades;
    }

    public async Task<JsonArray> GetPositionsAsync(BrokerSession session, CancellationToken cancellationToken)
    {
        var rows = Rows(await GetAsync(session, "/rest/secure/angelbroking/order/v1/getPosition", cancellationToken));
        var positions = new JsonArray();
        foreach (var p in rows)
        {
            var exchange = Str(p["exchange"]) ?? "";
            var record = await symbols.FindByTokenAsync(Name, Str(p["symboltoken"]) ?? "", exchange, cancellationToken);
            positions.Add(new JsonObject
            {
                ["symbol"] = record?.Symbol ?? Get(p, "tradingsymbol", ""), ["exchange"] = Get(p, "exchange", ""),
                ["product"] = record is null ? Get(p, "producttype", "") : BookProduct(exchange, Str(p["producttype"]) ?? ""),
                ["quantity"] = Int(p["netqty"]), ["average_price"] = Float(p["avgnetprice"]), ["ltp"] = Float(p["ltp"]), ["pnl"] = Float(p["pnl"]),
            });
        }

        return positions;
    }

    public async Task<BrokerBook> GetHoldingsAsync(BrokerSession session, CancellationToken cancellationToken)
    {
        var reply = await GetAsync(session, "/rest/secure/angelbroking/portfolio/v1/getAllHolding", cancellationToken);
        var data = reply["data"] as JsonObject;
        var holdings = new JsonArray();
        foreach (var h in (data?["holdings"] as JsonArray ?? []).OfType<JsonObject>())
        {
            var record = await symbols.FindByBrokerSymbolAsync(Name, Str(h["tradingsymbol"]) ?? "", Str(h["exchange"]) ?? "", cancellationToken);
            holdings.Add(new JsonObject
            {
                ["symbol"] = record?.Symbol ?? Get(h, "tradingsymbol", ""), ["exchange"] = Get(h, "exchange", ""), ["quantity"] = Int(h["quantity"]),
                // Holdings sit in the demat account, so CNC whatever SmartAPI labels them.
                ["product"] = "CNC", ["average_price"] = Float(h["averageprice"]), ["ltp"] = Float(h["ltp"]),
                ["pnl"] = Float(h["profitandloss"]), ["pnlpercent"] = Float(h["pnlpercentage"]),
            });
        }

        var total = data?["totalholding"] as JsonObject;
        return new BrokerBook(holdings, new JsonObject
        {
            ["totalholdingvalue"] = Float(total?["totalholdingvalue"]), ["totalinvvalue"] = Float(total?["totalinvvalue"]),
            ["totalprofitandloss"] = Float(total?["totalprofitandloss"]), ["totalpnlpercentage"] = Float(total?["totalpnlpercentage"]),
        });
    }

    // ---- funds -----------------------------------------------------------------------------------------------

    public async Task<JsonObject> GetFundsAsync(BrokerSession session, CancellationToken cancellationToken)
    {
        using var request = Request(session, HttpMethod.Get, "/rest/secure/angelbroking/user/v1/getRMS");
        using var response = await SendAsync(request, cancellationToken);
        var reply = await ReadAsync(response, cancellationToken) as JsonObject;
        // OpenAlgo answers an empty success when SmartAPI refuses; the refusal is reported here.
        if (reply?["data"] is not JsonObject data || data.Count == 0)
        {
            var message = Str(reply?["message"]);
            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden || message is "Invalid Token")
                throw new BrokerException("Angel One says the session has expired. Log in to Angel One again.", 403);
            throw new BrokerException($"Angel One could not report funds: {message ?? "try again in a moment."}");
        }

        var net = Float(data["availablecash"]);
        var collateral = net - Float(data["utilisedpayout"]);
        var debits = Float(data["utiliseddebits"]);

        // Realised and unrealised from the position book: SmartAPI's own RMS m2m fields have been seen reading 0.
        double realised = 0, unrealised = 0;
        try
        {
            foreach (var p in (await GetPositionsAsync(session, cancellationToken)).OfType<JsonObject>())
            {
                var pnl = Float(p["pnl"]);
                if (Float(p["quantity"]) == 0) realised += pnl;
                else unrealised += pnl;
            }
        }
        catch (BrokerException ex)
        {
            logger.LogWarning(ex, "Could not read Angel One positions for P&L.");
        }

        return new JsonObject
        {
            ["availablecash"] = BookFormatting.Fixed2(net + debits - collateral), ["collateral"] = BookFormatting.Fixed2(collateral),
            ["m2mrealized"] = BookFormatting.Fixed2(realised), ["m2munrealized"] = BookFormatting.Fixed2(unrealised),
            ["utiliseddebits"] = BookFormatting.Fixed2(debits),
        };
    }

    // ---- market data -----------------------------------------------------------------------------------------

    private async Task<JsonObject> QuoteAsync(BrokerSession session, string symbol, string exchange, string what, CancellationToken cancellationToken)
    {
        var instrument = await symbols.FindAsync(Name, symbol, exchange, cancellationToken)
            ?? throw new BrokerException($"Error fetching {what}: {symbol} on {exchange} is not in the Angel One master contract.");
        var angelExchange = exchange switch { "NSE_INDEX" => "NSE", "BSE_INDEX" => "BSE", "MCX_INDEX" => "MCX", _ => exchange };
        var body = new JsonObject { ["mode"] = "FULL", ["exchangeTokens"] = new JsonObject { [angelExchange] = new JsonArray(instrument.Token) } };
        using var request = Request(session, HttpMethod.Post, "/rest/secure/angelbroking/market/v1/quote/", body);
        using var response = await SendAsync(request, cancellationToken);
        var reply = await ReadAsync(response, cancellationToken) as JsonObject;
        if (response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests && (reply is null || !Truthy(reply["status"])))
            throw new BrokerException(response.StatusCode == HttpStatusCode.TooManyRequests
                ? "Angel One is limiting requests. Wait a moment, then try again."
                : "Angel One refused the request. Check the private key, or log in to Angel One again.", 403);
        if (!Truthy(reply?["status"]))
            throw new BrokerException(Str(reply?["message"]) == "Invalid Token"
                ? "Angel One says the session has expired. Log in to Angel One again."
                : $"Angel One could not send the {what}: {Str(reply?["message"]) ?? "no reason given."}", Str(reply?["message"]) == "Invalid Token" ? 403 : 500);
        return reply!["data"]?["fetched"] is JsonArray { Count: > 0 } fetched && fetched[0] is JsonObject quote
            ? quote
            : throw new BrokerException($"Angel One has no {what} for {symbol} right now.");
    }

    public async Task<JsonObject> GetQuoteAsync(BrokerSession session, string symbol, string exchange, CancellationToken cancellationToken)
    {
        var q = await QuoteAsync(session, symbol, exchange, "quotes", cancellationToken);
        var bids = q["depth"]?["buy"] as JsonArray;
        var asks = q["depth"]?["sell"] as JsonArray;
        JsonNode First(JsonArray? side, string key, bool integer) =>
            side is { Count: > 0 } ? integer ? JsonValue.Create(Int(side[0]?[key])) : JsonValue.Create(Float(side[0]?[key])) : 0;
        return new JsonObject
        {
            ["bid"] = First(bids, "price", false), ["ask"] = First(asks, "price", false),
            ["bid_qty"] = First(bids, "quantity", true), ["ask_qty"] = First(asks, "quantity", true),
            ["open"] = Float(q["open"]), ["high"] = Float(q["high"]), ["low"] = Float(q["low"]), ["ltp"] = Float(q["ltp"]),
            ["prev_close"] = Float(q["close"]), ["volume"] = Int(q["tradeVolume"]), ["oi"] = Int(q["opnInterest"]),
        };
    }

    public async Task<JsonObject> GetDepthAsync(BrokerSession session, string symbol, string exchange, CancellationToken cancellationToken)
    {
        var q = await QuoteAsync(session, symbol, exchange, "market depth", cancellationToken);
        static JsonArray Levels(JsonArray? side)
        {
            var levels = new JsonArray();
            for (var i = 0; i < 5; i++)
            {
                levels.Add(side is not null && i < side.Count && side[i] is JsonObject level
                    ? new JsonObject { ["price"] = Get(level, "price", 0), ["quantity"] = Get(level, "quantity", 0) }
                    : new JsonObject { ["price"] = 0, ["quantity"] = 0 });
            }

            return levels;
        }

        return new JsonObject
        {
            ["bids"] = Levels(q["depth"]?["buy"] as JsonArray), ["asks"] = Levels(q["depth"]?["sell"] as JsonArray),
            ["high"] = Get(q, "high", 0), ["low"] = Get(q, "low", 0), ["ltp"] = Get(q, "ltp", 0), ["ltq"] = Get(q, "lastTradeQty", 0),
            ["open"] = Get(q, "open", 0), ["prev_close"] = Get(q, "close", 0), ["volume"] = Get(q, "tradeVolume", 0),
            ["oi"] = Get(q, "opnInterest", 0), ["totalbuyqty"] = Get(q, "totBuyQuan", 0), ["totalsellqty"] = Get(q, "totSellQuan", 0),
        };
    }

    // ---- plumbing --------------------------------------------------------------------------------------------

    private static List<JsonObject> Rows(JsonObject reply) => (reply["data"] as JsonArray ?? []).OfType<JsonObject>().ToList();

    private HttpClient Client() => http.CreateClient(Name);

    private HttpRequestMessage Request(BrokerSession session, HttpMethod method, string path, JsonObject? body = null)
    {
        var request = new HttpRequestMessage(method, sessions.BaseUrl(Name, BaseUrl) + path);
        request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {session.AuthToken}");
        request.Headers.TryAddWithoutValidation("Accept", "application/json");
        request.Headers.TryAddWithoutValidation("X-UserType", "USER");
        request.Headers.TryAddWithoutValidation("X-SourceID", "WEB");
        request.Headers.TryAddWithoutValidation("X-ClientLocalIP", "CLIENT_LOCAL_IP");
        request.Headers.TryAddWithoutValidation("X-ClientPublicIP", "CLIENT_PUBLIC_IP");
        request.Headers.TryAddWithoutValidation("X-MACAddress", "MAC_ADDRESS");
        request.Headers.TryAddWithoutValidation("X-PrivateKey", session.Key("ApiKey") ?? "");
        if (body is not null)
        {
            request.Content = new StringContent(body.ToJsonString(), Encoding.UTF8);
            request.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
        }

        return request;
    }

    private async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        try
        {
            return await Client().SendAsync(request, cancellationToken);
        }
        catch (HttpRequestException ex)
        {
            throw new BrokerException("Angel One did not answer. Check your connection, then try again.", 502, ex);
        }
    }

    private async Task<JsonObject> GetAsync(BrokerSession session, string path, CancellationToken cancellationToken)
    {
        using var request = Request(session, HttpMethod.Get, path);
        using var response = await SendAsync(request, cancellationToken);
        var reply = await ReadAsync(response, cancellationToken) as JsonObject
            ?? throw new BrokerException("Angel One sent a reply EP Trade bot could not read. Try again in a moment.");
        if (reply["status"] is JsonValue v && v.TryGetValue<bool>(out var ok) && !ok)
        {
            var message = Str(reply["message"]);
            throw new BrokerException(message == "Invalid Token" ? "Angel One says the session has expired. Log in to Angel One again." : $"Angel One could not answer: {message ?? "try again in a moment."}",
                message == "Invalid Token" ? 403 : 500);
        }

        return reply;
    }
}
