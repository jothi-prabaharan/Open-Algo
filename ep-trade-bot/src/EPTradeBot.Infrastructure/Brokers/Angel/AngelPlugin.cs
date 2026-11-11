using System.Text.Json;
using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using EPTradeBot.Core.Brokers;
using EPTradeBot.Core.Brokers.Gtt;
using Microsoft.Extensions.Logging;
using static EPTradeBot.Infrastructure.Brokers.JsonRead;

namespace EPTradeBot.Infrastructure.Brokers.Angel;

/// <summary>
/// Angel One SmartAPI. Port of broker/angel (api/order_api.py, api/funds.py, api/data.py quotes
/// and depth, mapping/transform_data.py, mapping/order_data.py), held to
/// tools/contract-fixtures/angel.fixtures.json. SmartAPI sends many numbers as strings; they
/// are passed through as the books show them. Not ported yet, and refused rather than guessed:
/// login and the SmartAPI feed.
/// </summary>
[BrokerPlugin]
public sealed partial class AngelPlugin(IHttpClientFactory http, BrokerSessionFactory sessions, ISymbolMaster symbols, TimeProvider clock, ILogger<AngelPlugin> logger)
    : IBrokerOrders, IBrokerPortfolio, IBrokerFunds, IBrokerMultiQuotes, IBrokerHistory, IBrokerMargin, IBrokerGtt
{
    private const string BaseUrl = "https://apiconnect.angelone.in";

    public string Name => "angel";
    public string DisplayName => "Angel One";
    public BrokerCapability Capabilities => BrokerCapability.Orders | BrokerCapability.Portfolio | BrokerCapability.Funds | BrokerCapability.Quotes | BrokerCapability.Depth | BrokerCapability.History | BrokerCapability.Margin | BrokerCapability.Gtt;

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
        var fetched = await FetchQuotesAsync(session, new JsonObject { [AngelExchange(exchange)] = new JsonArray(instrument.Token) }, what, cancellationToken);
        return fetched is { Count: > 0 } && fetched[0] is JsonObject quote
            ? quote
            : throw new BrokerException($"Angel One has no {what} for {symbol} right now.");
    }

    private static string AngelExchange(string exchange) => exchange switch { "NSE_INDEX" => "NSE", "BSE_INDEX" => "BSE", "MCX_INDEX" => "MCX", _ => exchange };

    /// <summary>POST market/v1/quote in FULL mode; the reply's fetched quotes.</summary>
    private async Task<JsonArray> FetchQuotesAsync(BrokerSession session, JsonObject exchangeTokens, string what, CancellationToken cancellationToken)
    {
        var body = new JsonObject { ["mode"] = "FULL", ["exchangeTokens"] = exchangeTokens };
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
        return reply!["data"]?["fetched"] as JsonArray ?? [];
    }

    /// <summary>Angel One answers up to 50 instruments per quote call.</summary>
    private const int QuoteBatch = 50;

    public async Task<JsonArray> GetMultiQuotesAsync(BrokerSession session, IReadOnlyList<SymbolRecord> instruments, CancellationToken cancellationToken)
    {
        var results = new JsonArray();
        for (var start = 0; start < instruments.Count; start += QuoteBatch)
        {
            var tokens = new JsonObject();
            var wanted = new Dictionary<string, SymbolRecord>();
            foreach (var instrument in instruments.Skip(start).Take(QuoteBatch))
            {
                var exchange = AngelExchange(instrument.Exchange);
                if (tokens[exchange] is not JsonArray list) tokens[exchange] = list = [];
                list.Add(instrument.Token);
                wanted.TryAdd($"{exchange}:{instrument.Token}", instrument);
            }

            var byToken = new Dictionary<string, JsonObject>();
            foreach (var quote in (await FetchQuotesAsync(session, tokens, "quotes", cancellationToken)).OfType<JsonObject>())
            {
                if (Str(quote["exchange"]) is { Length: > 0 } e && Str(quote["symbolToken"]) is { Length: > 0 } t) byToken[$"{e}:{t}"] = quote;
            }

            foreach (var (key, instrument) in wanted)
            {
                results.Add(byToken.TryGetValue(key, out var q)
                    ? new JsonObject { ["symbol"] = instrument.Symbol, ["exchange"] = instrument.Exchange, ["data"] = QuoteFields(q) }
                    : new JsonObject { ["symbol"] = instrument.Symbol, ["exchange"] = instrument.Exchange, ["error"] = "No quote data available" });
            }
        }

        return results;
    }

    public async Task<JsonObject> GetQuoteAsync(BrokerSession session, string symbol, string exchange, CancellationToken cancellationToken) =>
        QuoteFields(await QuoteAsync(session, symbol, exchange, "quotes", cancellationToken));

    private static JsonObject QuoteFields(JsonObject q)
    {
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

    // ---- history ---------------------------------------------------------------------------------------------

    // timeframe_map and interval_limits of broker/angel/api/data.py: SmartAPI's resolution and the most days one request may span.
    private static readonly Dictionary<string, (string Resolution, int Days)> CandleSpans = new()
    {
        ["1m"] = ("ONE_MINUTE", 30), ["3m"] = ("THREE_MINUTE", 60), ["5m"] = ("FIVE_MINUTE", 100), ["10m"] = ("TEN_MINUTE", 100),
        ["15m"] = ("FIFTEEN_MINUTE", 200), ["30m"] = ("THIRTY_MINUTE", 200), ["1h"] = ("ONE_HOUR", 400), ["D"] = ("ONE_DAY", 2000),
    };

    public IReadOnlyList<string> HistoryTimeframes { get; } = [.. CandleSpans.Keys];

    private static readonly TimeSpan HistoryGap = TimeSpan.FromMilliseconds(500);
    private DateTimeOffset _lastHistoryCall = DateTimeOffset.MinValue;
    private readonly object _historyLock = new();

    /// <summary>SmartAPI allows about 2 history calls a second; every call (a retry too) waits for its turn.</summary>
    private async Task HistoryTurnAsync(BrokerSession session, CancellationToken cancellationToken)
    {
        var gap = sessions.HistoryGap(Name, HistoryGap);
        TimeSpan wait;
        lock (_historyLock)
        {
            var now = clock.GetUtcNow();
            var slot = now > _lastHistoryCall + gap ? now : _lastHistoryCall + gap;
            wait = slot - now;
            _lastHistoryCall = slot;
        }

        if (wait > TimeSpan.Zero) await Task.Delay(wait, cancellationToken);
    }

    public async Task<JsonArray> GetHistoryAsync(BrokerSession session, SymbolRecord instrument, string interval, DateOnly start, DateOnly end, CancellationToken cancellationToken)
    {
        if (!CandleSpans.TryGetValue(interval, out var span))
            throw new BrokerException($"Angel One does not offer {interval} candles. Choose one of: {string.Join(", ", CandleSpans.Keys)}.", 400);

        var exchange = AngelExchange(instrument.Exchange);
        // The window is date-only in India's time: today's request ends at the current minute, any other day at 23:59.
        var nowIst = clock.GetUtcNow().ToOffset(TimeSpan.FromHours(5.5)).DateTime;
        var from = start.ToDateTime(TimeOnly.MinValue);
        var to = end == DateOnly.FromDateTime(nowIst)
            ? new DateTime(nowIst.Year, nowIst.Month, nowIst.Day, nowIst.Hour, nowIst.Minute, 0)
            : end.ToDateTime(new TimeOnly(23, 59));

        var candles = new SortedDictionary<long, JsonObject>();
        foreach (var row in await ChunkedAsync(session, "getCandleData", exchange, instrument.Token, span, from, to, "candles", cancellationToken))
        {
            if (row is not JsonArray c || c.Count < 6) continue;
            var stamp = AngelEpoch(Str(c[0]) ?? "", interval == "D");
            candles.TryAdd(stamp, new JsonObject
            {
                ["close"] = Numeric(c[4]), ["high"] = Numeric(c[2]), ["low"] = Numeric(c[3]), ["open"] = Numeric(c[1]),
                ["timestamp"] = stamp, ["volume"] = Numeric(c[5]), ["oi"] = 0,
            });
        }

        // Open interest has its own call. It is a side figure: when it cannot be had the candles still go out, with 0.
        if (candles.Count > 0 && exchange is "NFO" or "BFO" or "CDS" or "MCX")
        {
            try
            {
                foreach (var row in await ChunkedAsync(session, "getOIData", exchange, instrument.Token, span, from, to, "open interest", cancellationToken))
                {
                    if (row is not JsonObject o) continue;
                    var stamp = AngelEpoch(Str(o["time"]) ?? "", interval == "D");
                    if (candles.TryGetValue(stamp, out var candle)) candle["oi"] = (long)Float(o["oi"]);
                }
            }
            catch (BrokerException ex)
            {
                logger.LogWarning(ex, "Open interest for {Symbol} could not be read; the candles carry 0.", instrument.Symbol);
            }
        }

        return new JsonArray(candles.Values.Select(v => (JsonNode?)v).ToArray());
    }

    /// <summary>One SmartAPI history endpoint over the whole window, cut into the days a request may span.</summary>
    private async Task<List<JsonNode?>> ChunkedAsync(BrokerSession session, string endpoint, string exchange, string token, (string Resolution, int Days) span,
        DateTime from, DateTime to, string what, CancellationToken cancellationToken)
    {
        var rows = new List<JsonNode?>();
        for (var chunkStart = from; chunkStart <= to;)
        {
            var chunkEnd = chunkStart.AddDays(span.Days - 1) < to ? chunkStart.AddDays(span.Days - 1) : to;
            var body = new JsonObject
            {
                ["exchange"] = exchange, ["symboltoken"] = token, ["interval"] = span.Resolution,
                ["fromdate"] = chunkStart.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture),
                ["todate"] = chunkEnd.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture),
            };

            JsonObject? reply = null;
            for (var attempt = 0; attempt < 4; attempt++)
            {
                await HistoryTurnAsync(session, cancellationToken);
                using var request = Request(session, HttpMethod.Post, "/rest/secure/angelbroking/historical/v1/" + endpoint, body);
                using var response = await SendAsync(request, cancellationToken);
                var text = await response.Content.ReadAsStringAsync(cancellationToken);
                var limited = response.StatusCode == HttpStatusCode.TooManyRequests
                    || (response.StatusCode == HttpStatusCode.Forbidden && (text.Contains("exceeding", StringComparison.OrdinalIgnoreCase) || text.Contains("access rate", StringComparison.OrdinalIgnoreCase) || text.Contains("rate limit", StringComparison.OrdinalIgnoreCase)));
                if (response.StatusCode == HttpStatusCode.Forbidden && !limited)
                    throw new BrokerException("Angel One refused the request. Check the private key, or log in to Angel One again.", 403);
                reply = ParseObject(text);
                var message = Str(reply?["message"]) ?? "";
                if (limited || (!Truthy(reply?["status"]) && (message.Contains("exceed", StringComparison.OrdinalIgnoreCase) || message.Contains("rate", StringComparison.OrdinalIgnoreCase))))
                {
                    if (attempt < 3)
                    {
                        await Task.Delay(TimeSpan.FromSeconds(0.5 * (1 << attempt)), cancellationToken);
                        continue;
                    }

                    throw new BrokerException("Angel One is limiting requests. Wait a moment, then try again.", 429);
                }

                break;
            }

            // OpenAlgo logs a chunk Angel One would not answer and carries on, which leaves a gap in the candles
            // with nothing to show for it. EP Trade bot stops and says which days are missing.
            if (!Truthy(reply?["status"]))
            {
                var message = Str(reply?["message"]);
                throw new BrokerException(message == "Invalid Token"
                    ? "Angel One says the session has expired. Log in to Angel One again."
                    : $"Angel One could not send the {what} for {chunkStart:yyyy-MM-dd} to {chunkEnd:yyyy-MM-dd}: {message ?? "no reason given."}", message == "Invalid Token" ? 403 : 500);
            }

            rows.AddRange(reply!["data"] as JsonArray ?? []);
            chunkStart = chunkEnd.AddDays(1);
        }

        return rows;
    }

    /// <summary>Epoch seconds of SmartAPI's "2026-11-02T09:15:00+05:30"; a time without an offset is India's. A daily candle is shifted to 00:00 UTC of its date.</summary>
    private static long AngelEpoch(string text, bool daily)
    {
        var stamp = DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed) && HasOffset(text)
            ? parsed
            : new DateTimeOffset(DateTime.Parse(text, CultureInfo.InvariantCulture), TimeSpan.FromHours(5.5));
        return stamp.ToUnixTimeSeconds() + (daily ? 19_800 : 0);
    }

    /// <summary>SmartAPI sometimes sends numbers as text; a candle carries numbers (a whole one stays whole).</summary>
    private static JsonNode? Numeric(JsonNode? node)
    {
        var text = node is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
        if (text is null) return node?.DeepClone();
        if (long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var whole)) return whole;
        return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) ? number : 0;
    }

    private static JsonObject? ParseObject(string text)
    {
        try
        {
            return string.IsNullOrWhiteSpace(text) ? null : JsonNode.Parse(text) as JsonObject;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static bool HasOffset(string text) => text.EndsWith('Z') || System.Text.RegularExpressions.Regex.IsMatch(text, @"[+-]\d{2}:?\d{2}$");

    // ---- margin ----------------------------------------------------------------------------------------------

    /// <summary>SmartAPI's batch margin calculator. It answers a total and the SPAN part; the exposure part is not broken out.</summary>
    public async Task<JsonObject> CalculateMarginAsync(BrokerSession session, IReadOnlyList<MarginLeg> legs, CancellationToken cancellationToken)
    {
        var positions = new JsonArray();
        foreach (var l in legs)
        {
            // The token must be a number: anything else is not an Angel One instrument.
            if (l.Instrument.Token.Trim().Replace(".", "").Replace("-", "") is not { Length: > 0 } digits || !digits.All(char.IsDigit))
                throw new BrokerException($"{l.Instrument.Symbol} on {l.Instrument.Exchange} has no usable Angel One token, so the margin cannot be worked out. Download the master contract again.", 400);
            positions.Add(new JsonObject
            {
                ["exchange"] = l.Instrument.Exchange, ["qty"] = l.Quantity, ["price"] = (double)l.Price, ["productType"] = Product(l.Product),
                ["token"] = l.Instrument.Token.Trim(), ["tradeType"] = l.Action, ["orderType"] = OrderType(l.PriceType),
            });
        }

        using var request = Request(session, HttpMethod.Post, "/rest/secure/angelbroking/margin/v1/batch", new JsonObject { ["positions"] = positions });
        using var response = await SendAsync(request, cancellationToken);
        var reply = await ReadAsync(response, cancellationToken) as JsonObject
            ?? throw new BrokerException("Angel One sent a reply EP Trade bot could not read. Try again in a moment.");
        // OpenAlgo answers success when Angel One refuses with a 200 and "status": false; a refusal is an error here.
        if (reply["status"] is JsonValue st && st.TryGetValue<bool>(out var ok) && !ok)
            throw new BrokerException($"Angel One could not work out the margin: {Str(reply["message"]) ?? "Failed to calculate margin"}", response.IsSuccessStatusCode ? 400 : (int)response.StatusCode);
        if (!response.IsSuccessStatusCode)
            throw new BrokerException($"Angel One could not work out the margin: {Str(reply["message"]) ?? "Failed to calculate margin"}", (int)response.StatusCode);

        var data = reply["data"] as JsonObject;
        return new JsonObject
        {
            ["total_margin_required"] = data?["totalMarginRequired"]?.DeepClone() ?? 0, ["span_margin"] = data?["marginComponents"]?["spanMargin"]?.DeepClone() ?? 0, ["exposure_margin"] = 0,
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
