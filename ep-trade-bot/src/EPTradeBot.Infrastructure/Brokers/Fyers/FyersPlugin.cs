using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using EPTradeBot.Core.Brokers;
using Microsoft.Extensions.Logging;
using static EPTradeBot.Infrastructure.Brokers.JsonRead;

namespace EPTradeBot.Infrastructure.Brokers.Fyers;

/// <summary>
/// Fyers API v3. Port of broker/fyers (api/order_api.py, api/funds.py, api/data.py quotes and
/// depth, mapping/transform_data.py, mapping/order_data.py), held to
/// tools/contract-fixtures/fyers.fixtures.json. Fyers reports exchanges as numeric codes and
/// orders, sides and states as numbers; they are translated as OpenAlgo does. Not ported yet,
/// and refused rather than guessed: history, GTT, margin, login and the Fyers feed.
/// </summary>
[BrokerPlugin]
public sealed class FyersPlugin(IHttpClientFactory http, BrokerSessionFactory sessions, ISymbolMaster symbols, ILogger<FyersPlugin> logger)
    : IBrokerOrders, IBrokerPortfolio, IBrokerFunds, IBrokerMultiQuotes, IBrokerOptionChain
{
    private const string BaseUrl = "https://api-t1.fyers.in";

    public string Name => "fyers";
    public string DisplayName => "Fyers";
    public BrokerCapability Capabilities => BrokerCapability.Orders | BrokerCapability.Portfolio | BrokerCapability.Funds | BrokerCapability.Quotes | BrokerCapability.Depth;

    /// <summary>ApiKey: the Fyers app id, sent with the token as "app_id:token".</summary>
    public IReadOnlyList<string> RequiredKeys { get; } = ["ApiKey", "ApiSecret"];

    private static int OrderType(string priceType) => priceType switch { "LIMIT" => 1, "SL" => 4, "SL-M" => 3, _ => 2 };

    private static string Product(string product) => product switch { "CNC" => "CNC", "NRML" => "MARGIN", "CO" => "CO", "BO" => "BO", _ => "INTRADAY" };

    private static string BookProduct(string? product) => product switch
    {
        "CNC" => "CNC", "INTRADAY" => "MIS", "MARGIN" => "NRML", "CO" => "CO", "BO" => "BO", _ => "unknown",
    };

    /// <summary>Fyers' (exchange, segment) codes.</summary>
    private static string Exchange(JsonNode? exchange, JsonNode? segment) => (Int(exchange), Int(segment)) switch
    {
        (10, 10) => "NSE", (10, 11) => "NFO", (10, 12) => "CDS", (12, 10) => "BSE", (12, 11) => "BFO", (11, 20) => "MCX",
        _ => "Unknown Exchange",
    };

    // ---- orders ----------------------------------------------------------------------------------------------

    public async Task<BrokerCallResult> PlaceAsync(BrokerSession session, BrokerOrder order, CancellationToken cancellationToken)
    {
        var instrument = await symbols.FindAsync(Name, order.Symbol, order.Exchange, cancellationToken);
        if (instrument is null) return BrokerCallResult.Refused(400, $"{order.Symbol} on {order.Exchange} is not in the Fyers master contract. Check the symbol, or download the master contract again.");
        var body = new JsonObject
        {
            ["symbol"] = instrument.BrokerSymbol, ["qty"] = order.Quantity, ["type"] = OrderType(order.PriceType),
            ["side"] = order.Action.Equals("BUY", StringComparison.OrdinalIgnoreCase) ? 1 : -1, ["productType"] = Product(order.Product),
            ["limitPrice"] = (double)order.Price, ["stopPrice"] = (double)order.TriggerPrice, ["validity"] = "DAY",
            ["disclosedQty"] = order.DisclosedQuantity, ["offlineOrder"] = false, ["stopLoss"] = 0, ["takeProfit"] = 0, ["orderTag"] = "openalgo",
        };

        HttpResponseMessage response;
        using var request = Request(session, HttpMethod.Post, "/api/v3/orders/sync", body);
        try
        {
            response = await Client().SendAsync(request, cancellationToken);
        }
        catch (HttpRequestException ex)
        {
            logger.LogWarning(ex, "Fyers order POST got no answer.");
            return new BrokerCallResult(false, 502,
                "The order was sent to Fyers but no reply came back, so it may have been placed. Check the order book on Fyers before trying again.", MayHaveBeenSent: true);
        }

        using (response)
        {
            var reply = await ReadAsync(response, cancellationToken) as JsonObject;
            if (Str(reply?["s"]) == "ok" && Str(reply?["id"]) is { Length: > 0 } id) return BrokerCallResult.Success(id);
            // OpenAlgo reports a refusal Fyers sends with HTTP 200 as a success with no order id; it is a refusal here.
            var status = (int)response.StatusCode;
            return BrokerCallResult.Refused(status == 200 ? 500 : status, Str(reply?["message"]) ?? "Failed to place order");
        }
    }

    public async Task<BrokerCallResult> ModifyAsync(BrokerSession session, BrokerOrderChange change, CancellationToken cancellationToken)
    {
        var body = new JsonObject
        {
            ["id"] = change.OrderId, ["qty"] = change.Quantity, ["type"] = OrderType(change.PriceType),
            ["limitPrice"] = (double)change.Price, ["stopPrice"] = (double)change.TriggerPrice,
        };
        using var request = Request(session, HttpMethod.Patch, "/api/v3/orders/sync", body);
        using var response = await SendAsync(request, cancellationToken);
        var reply = await ReadAsync(response, cancellationToken) as JsonObject;
        if (Str(reply?["s"]) is "ok" or "OK" && Str(reply?["id"]) is { Length: > 0 } id) return BrokerCallResult.Success(id);
        return BrokerCallResult.Refused(response.IsSuccessStatusCode ? 500 : (int)response.StatusCode, Str(reply?["message"]) ?? "Failed to modify order");
    }

    public async Task<BrokerCallResult> CancelAsync(BrokerSession session, string orderId, CancellationToken cancellationToken)
    {
        using var request = Request(session, HttpMethod.Delete, "/api/v3/orders/sync", new JsonObject { ["id"] = orderId });
        using var response = await SendAsync(request, cancellationToken);
        var reply = await ReadAsync(response, cancellationToken) as JsonObject;
        if (Str(reply?["s"]) == "ok") return BrokerCallResult.Success(Str(reply?["id"]) ?? orderId);
        return BrokerCallResult.Refused(response.IsSuccessStatusCode ? 500 : (int)response.StatusCode, Str(reply?["message"]) ?? "Failed to cancel order");
    }

    // ---- books -----------------------------------------------------------------------------------------------

    /// <summary>The OpenAlgo symbol and exchange; both stay as Fyers sent them when the symbol is not in the master contract.</summary>
    private async Task<(JsonNode? Symbol, JsonNode? Exchange)> MapAsync(JsonObject row, CancellationToken cancellationToken)
    {
        var exchange = Exchange(row["exchange"], row["segment"]);
        if (Str(row["symbol"]) is { Length: > 0 } symbol && await symbols.FindByBrokerSymbolAsync(Name, symbol, exchange, cancellationToken) is { } record)
            return (record.Symbol, exchange);
        return (Get(row, "symbol", ""), Get(row, "exchange", ""));
    }

    public async Task<BrokerBook> GetOrderBookAsync(BrokerSession session, CancellationToken cancellationToken)
    {
        var rows = await ListAsync(session, "/api/v3/orders", "orderBook", cancellationToken);
        int buy = 0, sell = 0, completed = 0, open = 0, rejected = 0;
        var orders = new JsonArray();
        foreach (var o in rows)
        {
            var (symbol, exchange) = await MapAsync(o, cancellationToken);
            var side = Int(o["side"]);
            var status = Int(o["status"]);
            if (side == 1) buy++;
            else if (side == -1) sell++;
            if (status == 2) completed++;
            else if (status == 6) open++;
            else if (status == 5) rejected++;
            orders.Add(new JsonObject
            {
                ["symbol"] = symbol, ["exchange"] = exchange, ["action"] = side switch { 1 => "BUY", -1 => "SELL", _ => "unknown" },
                ["quantity"] = Get(o, "qty", 0), ["price"] = Get(o, "limitPrice", 0.0), ["trigger_price"] = Get(o, "stopPrice", 0.0),
                ["pricetype"] = Int(o["type"]) switch { 1 => "LIMIT", 2 => "MARKET", 3 => "SL-M", 4 => "SL", _ => "unknown" },
                ["product"] = BookProduct(Str(o["productType"])), ["orderid"] = Get(o, "id", ""),
                ["order_status"] = status switch { 2 => "complete", 5 => "rejected", 4 => "trigger pending", 6 => "open", 1 => "cancelled", _ => "unknown" },
                ["timestamp"] = Get(o, "orderDateTime", ""),
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
        var trades = new JsonArray();
        foreach (var t in await ListAsync(session, "/api/v3/tradebook", "tradeBook", cancellationToken))
        {
            var (symbol, exchange) = await MapAsync(t, cancellationToken);
            trades.Add(new JsonObject
            {
                ["symbol"] = symbol, ["exchange"] = exchange, ["product"] = BookProduct(Str(t["productType"])),
                ["action"] = Int(t["side"]) switch { 1 => "BUY", -1 => "SELL", _ => "unknown" },
                ["quantity"] = Get(t, "tradedQty", 0), ["average_price"] = Get(t, "tradePrice", 0.0), ["trade_value"] = Get(t, "tradeValue", 0),
                ["orderid"] = Get(t, "orderNumber", ""), ["timestamp"] = Get(t, "orderDateTime", ""),
            });
        }

        return trades;
    }

    public async Task<JsonArray> GetPositionsAsync(BrokerSession session, CancellationToken cancellationToken)
    {
        var positions = new JsonArray();
        foreach (var p in await ListAsync(session, "/api/v3/positions", "netPositions", cancellationToken))
        {
            var (symbol, exchange) = await MapAsync(p, cancellationToken);
            positions.Add(new JsonObject
            {
                ["symbol"] = symbol, ["exchange"] = exchange, ["product"] = BookProduct(Str(p["productType"])), ["quantity"] = Get(p, "netQty", "0"),
                ["average_price"] = BookFormatting.Round2(Float(p["netAvg"])), ["ltp"] = BookFormatting.Round2(Float(p["ltp"])),
                ["pnl"] = BookFormatting.Round2(Float(p["pl"])),
            });
        }

        return positions;
    }

    public async Task<BrokerBook> GetHoldingsAsync(BrokerSession session, CancellationToken cancellationToken)
    {
        var holdings = new JsonArray();
        double value = 0, invested = 0, pnl = 0;
        foreach (var h in await ListAsync(session, "/api/v3/holdings", "holdings", cancellationToken))
        {
            var (symbol, exchange) = await MapAsync(h, cancellationToken);
            var cost = Float(h["costPrice"]);
            var ltp = Float(h["ltp"]);
            var quantity = Float(h["quantity"]);
            value += ltp * quantity;
            invested += cost * quantity;
            pnl += Float(h["pl"]);
            holdings.Add(new JsonObject
            {
                ["symbol"] = symbol, ["exchange"] = exchange, ["quantity"] = Get(h, "quantity", 0),
                ["product"] = Str(h["holdingType"]) is "HLD" or "T1" ? "CNC" : Get(h, "holdingType", ""),
                ["average_price"] = BookFormatting.Round2(cost), ["ltp"] = BookFormatting.Round2(ltp), ["pnl"] = BookFormatting.Round2(Float(h["pl"])),
                ["pnlpercent"] = cost != 0 ? BookFormatting.Round2((ltp - cost) / cost * 100) : 0.0,
            });
        }

        return new BrokerBook(holdings, new JsonObject
        {
            ["totalholdingvalue"] = value, ["totalinvvalue"] = invested, ["totalprofitandloss"] = pnl,
            ["totalpnlpercentage"] = invested != 0 ? BookFormatting.Round2(pnl / invested * 100) : 0,
        });
    }

    // ---- funds -----------------------------------------------------------------------------------------------

    public async Task<JsonObject> GetFundsAsync(BrokerSession session, CancellationToken cancellationToken)
    {
        using var request = Request(session, HttpMethod.Get, "/api/v3/funds");
        using var response = await SendAsync(request, cancellationToken);
        var reply = await ReadAsync(response, cancellationToken) as JsonObject;
        // OpenAlgo answers success with no funds when Fyers refuses; the refusal is reported here.
        if (!response.IsSuccessStatusCode || Int(reply?["code"]) != 200)
        {
            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                throw new BrokerException("Fyers says the session has expired. Log in to Fyers again.", 403);
            throw new BrokerException($"Fyers could not report funds: {Str(reply?["message"]) ?? "try again in a moment."}");
        }

        var limits = new Dictionary<string, (double Equity, double Commodity)>(StringComparer.Ordinal);
        foreach (var f in (reply!["fund_limit"] as JsonArray ?? []).OfType<JsonObject>())
            if (Str(f["title"]) is { } title) limits[title.ToLowerInvariant().Replace(' ', '_')] = (Float(f["equityAmount"]), Float(f["commodityAmount"]));
        double Total(string key) => limits.TryGetValue(key, out var v) ? v.Equity + v.Commodity : 0;

        double realised = 0, unrealised = 0;
        foreach (var p in await ListAsync(session, "/api/v3/positions", "netPositions", cancellationToken))
        {
            realised += Float(p["realized_profit"]);
            unrealised += Float(p["unrealized_profit"]);
        }

        return new JsonObject
        {
            ["availablecash"] = BookFormatting.Fixed2(Total("clear_balance")), ["collateral"] = BookFormatting.Fixed2(Total("collaterals")),
            ["m2munrealized"] = BookFormatting.Fixed2(unrealised), ["m2mrealized"] = BookFormatting.Fixed2(realised),
            ["utiliseddebits"] = BookFormatting.Fixed2(Total("utilized_amount")),
        };
    }

    // ---- market data -----------------------------------------------------------------------------------------

    private async Task<JsonObject> DepthDataAsync(BrokerSession session, string symbol, string exchange, string what, CancellationToken cancellationToken)
    {
        var instrument = await symbols.FindAsync(Name, symbol, exchange, cancellationToken)
            ?? throw new BrokerException($"Error fetching {what}: {symbol} on {exchange} is not in the Fyers master contract.");
        using var request = Request(session, HttpMethod.Get, $"/data/depth?symbol={Uri.EscapeDataString(instrument.BrokerSymbol)}&ohlcv_flag=1");
        using var response = await SendAsync(request, cancellationToken);
        var reply = await ReadAsync(response, cancellationToken) as JsonObject;
        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            throw new BrokerException("Fyers says the session has expired. Log in to Fyers again.", 403);
        if (Str(reply?["s"]) != "ok") throw new BrokerException($"Fyers could not send the {what}: {Str(reply?["message"]) ?? "no reason given."}");
        return reply!["d"]?[instrument.BrokerSymbol] as JsonObject is { Count: > 0 } data
            ? data
            : throw new BrokerException($"Fyers has no {what} for {symbol} right now.");
    }

    /// <summary>Fyers answers up to 50 symbols per /data/quotes call; OpenAlgo waits 100 ms between calls.</summary>
    private const int QuoteBatch = 50;

    /// <summary>Above this many symbols OpenAlgo leaves open interest at 0: Fyers' bulk quotes carry none, and one depth call per symbol would take too long.</summary>
    private const int OpenInterestLimit = 100;

    private static readonly HashSet<string> DerivativeExchanges = ["NFO", "BFO", "MCX", "CDS", "BCD", "NCDEX", "NCO", "CRYPTO"];

    public async Task<JsonArray> GetMultiQuotesAsync(BrokerSession session, IReadOnlyList<SymbolRecord> instruments, CancellationToken cancellationToken)
    {
        var withOpenInterest = instruments.Count <= OpenInterestLimit;
        var results = new JsonArray();
        for (var start = 0; start < instruments.Count; start += QuoteBatch)
        {
            if (start > 0) await Task.Delay(TimeSpan.FromMilliseconds(100), cancellationToken);
            var batch = instruments.Skip(start).Take(QuoteBatch).ToList();
            var wanted = new Dictionary<string, SymbolRecord>();
            foreach (var instrument in batch) wanted[instrument.BrokerSymbol] = instrument;

            using var request = Request(session, HttpMethod.Get, $"/data/quotes?symbols={Uri.EscapeDataString(string.Join(",", batch.Select(b => b.BrokerSymbol)))}");
            using var response = await SendAsync(request, cancellationToken);
            var reply = await ReadAsync(response, cancellationToken) as JsonObject;
            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                throw new BrokerException("Fyers says the session has expired. Log in to Fyers again.", 403);
            // OpenAlgo answers a refused batch as success with the symbols left out; here the refusal is reported.
            if (Str(reply?["s"]) != "ok") throw new BrokerException($"Fyers could not send quotes: {Str(reply?["message"]) ?? "no reason given."}");

            var quotes = new Dictionary<string, JsonObject>();
            foreach (var item in (reply!["d"] as JsonArray ?? []).OfType<JsonObject>())
            {
                if (Str(item["s"]) == "ok" && Str(item["n"]) is { } n && item["v"] is JsonObject v) quotes[n] = v;
            }

            foreach (var instrument in batch)
            {
                if (!quotes.TryGetValue(instrument.BrokerSymbol, out var q) || q.Count == 0)
                {
                    results.Add(new JsonObject { ["symbol"] = instrument.Symbol, ["exchange"] = instrument.Exchange, ["error"] = "No quote data available" });
                    continue;
                }

                var original = wanted[instrument.BrokerSymbol];
                var oi = withOpenInterest && DerivativeExchanges.Contains(original.Exchange) ? await OpenInterestAsync(session, instrument.BrokerSymbol, cancellationToken) : 0;
                results.Add(new JsonObject
                {
                    ["symbol"] = original.Symbol, ["exchange"] = original.Exchange,
                    ["data"] = new JsonObject
                    {
                        ["bid"] = Get(q, "bid", 0), ["ask"] = Get(q, "ask", 0), ["open"] = Get(q, "open_price", 0), ["high"] = Get(q, "high_price", 0),
                        ["low"] = Get(q, "low_price", 0), ["ltp"] = Get(q, "lp", 0), ["prev_close"] = Get(q, "prev_close_price", 0),
                        ["volume"] = Get(q, "volume", 0), ["oi"] = oi,
                    },
                });
            }
        }

        return results;
    }

    /// <summary>One symbol's open interest from /data/depth; 0 when Fyers cannot say, so one symbol never fails the batch.</summary>
    private async Task<long> OpenInterestAsync(BrokerSession session, string brokerSymbol, CancellationToken cancellationToken)
    {
        try
        {
            using var request = Request(session, HttpMethod.Get, $"/data/depth?symbol={Uri.EscapeDataString(brokerSymbol)}&ohlcv_flag=1");
            using var response = await SendAsync(request, cancellationToken);
            var reply = await ReadAsync(response, cancellationToken) as JsonObject;
            return Str(reply?["s"]) == "ok" ? Int(reply!["d"]?[brokerSymbol]?["oi"]) : 0;
        }
        catch (BrokerException)
        {
            return 0;
        }
    }

    /// <summary>Fyers' options-chain-v3 caps the strikes either side of ATM at 50.</summary>
    public async Task<IReadOnlyDictionary<(double Strike, string Type), JsonObject>> GetOptionChainAsync(BrokerSession session, SymbolRecord underlying, int strikeCount,
        CancellationToken cancellationToken)
    {
        var chain = new Dictionary<(double, string), JsonObject>();
        JsonObject? reply;
        try
        {
            using var request = Request(session, HttpMethod.Get,
                $"/data/options-chain-v3?symbol={Uri.EscapeDataString(underlying.BrokerSymbol)}&strikecount={Math.Clamp(strikeCount, 1, 50).ToString(CultureInfo.InvariantCulture)}");
            using var response = await SendAsync(request, cancellationToken);
            reply = await ReadAsync(response, cancellationToken) as JsonObject;
        }
        catch (BrokerException ex)
        {
            logger.LogWarning(ex, "Fyers option chain unavailable; falling back to quotes.");
            return chain;
        }

        if (reply is null || Str(reply["s"]) == "error" || Int(reply["code"]) != 200) return chain;
        foreach (var item in (reply["data"]?["optionsChain"] as JsonArray ?? []).OfType<JsonObject>())
        {
            var type = Str(item["option_type"]);
            if (type is not ("CE" or "PE") || item["strike_price"] is null) continue;
            var ltp = Truthy(item["ltp"]) ? Float(item["ltp"]) : 0;
            var change = Truthy(item["ltpch"]) ? Float(item["ltpch"]) : 0;
            chain[(Float(item["strike_price"]), type)] = new JsonObject
            {
                ["ltp"] = Truthy(item["ltp"]) ? item["ltp"]!.DeepClone() : 0, ["bid"] = Get(item, "bid", 0), ["ask"] = Get(item, "ask", 0),
                ["prev_close"] = ltp - change, ["volume"] = Get(item, "volume", 0), ["oi"] = Truthy(item["oi"]) ? Int(item["oi"]) : 0,
            };
        }

        return chain;
    }

    public async Task<JsonObject> GetQuoteAsync(BrokerSession session, string symbol, string exchange, CancellationToken cancellationToken)
    {
        var d = await DepthDataAsync(session, symbol, exchange, "quotes", cancellationToken);
        var bids = d["bids"] as JsonArray;
        var asks = d["ask"] as JsonArray;
        return new JsonObject
        {
            ["bid"] = bids is { Count: > 0 } ? Get(bids[0] as JsonObject, "price", 0) : 0, ["ask"] = asks is { Count: > 0 } ? Get(asks[0] as JsonObject, "price", 0) : 0,
            ["open"] = Get(d, "o", 0), ["high"] = Get(d, "h", 0), ["low"] = Get(d, "l", 0), ["ltp"] = Get(d, "ltp", 0),
            ["prev_close"] = Get(d, "c", 0), ["volume"] = Get(d, "v", 0), ["oi"] = Int(d["oi"]),
        };
    }

    public async Task<JsonObject> GetDepthAsync(BrokerSession session, string symbol, string exchange, CancellationToken cancellationToken)
    {
        var d = await DepthDataAsync(session, symbol, exchange, "market depth", cancellationToken);
        static JsonArray Levels(JsonNode? side)
        {
            var levels = new JsonArray();
            foreach (var level in (side as JsonArray ?? []).OfType<JsonObject>().Take(5))
                levels.Add(new JsonObject { ["price"] = Get(level, "price", 0), ["quantity"] = Get(level, "volume", 0) });
            while (levels.Count < 5) levels.Add(new JsonObject { ["price"] = 0, ["quantity"] = 0 });
            return levels;
        }

        return new JsonObject
        {
            ["bids"] = Levels(d["bids"]), ["asks"] = Levels(d["ask"]), ["totalbuyqty"] = Get(d, "totalbuyqty", 0), ["totalsellqty"] = Get(d, "totalsellqty", 0),
            ["high"] = Get(d, "h", 0), ["low"] = Get(d, "l", 0), ["ltp"] = Get(d, "ltp", 0), ["ltq"] = Get(d, "ltq", 0), ["open"] = Get(d, "o", 0),
            ["prev_close"] = Get(d, "c", 0), ["volume"] = Get(d, "v", 0), ["oi"] = Int(d["oi"]),
        };
    }

    // ---- plumbing --------------------------------------------------------------------------------------------

    private HttpClient Client() => http.CreateClient(Name);

    private HttpRequestMessage Request(BrokerSession session, HttpMethod method, string pathAndQuery, JsonObject? body = null)
    {
        var request = new HttpRequestMessage(method, sessions.BaseUrl(Name, BaseUrl) + pathAndQuery);
        request.Headers.TryAddWithoutValidation("Authorization", $"{session.Key("ApiKey")}:{session.AuthToken}");
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
            throw new BrokerException("Fyers did not answer. Check your connection, then try again.", 502, ex);
        }
    }

    private async Task<List<JsonObject>> ListAsync(BrokerSession session, string path, string key, CancellationToken cancellationToken)
    {
        using var request = Request(session, HttpMethod.Get, path);
        using var response = await SendAsync(request, cancellationToken);
        var reply = await ReadAsync(response, cancellationToken) as JsonObject;
        if (response.IsSuccessStatusCode && Str(reply?["s"]) == "ok") return (reply![key] as JsonArray ?? []).OfType<JsonObject>().ToList();
        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            throw new BrokerException("Fyers says the session has expired. Log in to Fyers again.", 403);
        throw new BrokerException($"Fyers could not answer: {Str(reply?["message"]) ?? "try again in a moment."}");
    }
}
