using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using EPTradeBot.Core.Brokers;
using Microsoft.Extensions.Logging;
using static EPTradeBot.Infrastructure.Brokers.JsonRead;

namespace EPTradeBot.Infrastructure.Brokers.Xts;

/// <summary>
/// The XTS family (Symphony Fintech's trading front end API), which Wisdom Capital, iBulls, Jainam, 5paisa XTS, IIFL, CompositEdge and RMoney share.
/// Port of broker/wisdom: api/order_api.py, api/funds.py and mapping/order_data.py. Held to tools/contract-fixtures/wisdom.fixtures.json.
/// Quotes, depth and several quotes (api/data.py) come from the market-data service. Not ported yet, and refused rather than guessed: history, margin, and the feed.
/// </summary>
public abstract partial class XtsPluginBase(IHttpClientFactory http, BrokerSessionFactory sessions, ISymbolMaster symbols, ILogger logger)
    : IBrokerOrders, IBrokerPortfolio, IBrokerFunds, IBrokerLogin, IBrokerMultiQuotes
{
    public abstract string Name { get; }
    public abstract string DisplayName { get; }

    /// <summary>The address of the broker's XTS server, without the /interactive path.</summary>
    protected abstract string DefaultBaseUrl { get; }

    public BrokerCapability Capabilities => BrokerCapability.Orders | BrokerCapability.Portfolio | BrokerCapability.Funds | BrokerCapability.Quotes | BrokerCapability.Depth;
    public IReadOnlyList<string> RequiredKeys { get; } = ["ApiKey", "ApiSecret", "ApiKeyMarket", "ApiSecretMarket"];

    private static readonly Dictionary<string, string> Segments = new()
    {
        ["NSE"] = "NSECM", ["BSE"] = "BSECM", ["NFO"] = "NSEFO", ["BFO"] = "BSEFO", ["MCX"] = "MCXFO", ["CDS"] = "NSECD",
    };

    private static readonly Dictionary<string, string> Exchanges = Segments.ToDictionary(kv => kv.Value, kv => kv.Key);

    private static string OrderType(string priceType) => priceType switch { "LIMIT" => "LIMIT", "SL" => "STOPLIMIT", "SL-M" => "STOPMARKET", _ => "MARKET" };

    private static string BookPriceType(string orderType) => orderType switch { "Limit" => "LIMIT", "Market" => "MARKET", "StopLimit" => "SL", "StopMarket" => "SL-M", _ => orderType };

    private static string BookStatus(string status) => status switch { "Filled" => "complete", "Rejected" => "rejected", "Cancelled" => "cancelled", "New" => "open", "Trigger Pending" => "trigger pending", _ => status };

    // ---- orders ----------------------------------------------------------------------------------------------

    public async Task<BrokerCallResult> PlaceAsync(BrokerSession session, BrokerOrder order, CancellationToken cancellationToken)
    {
        var instrument = await symbols.FindAsync(Name, order.Symbol, order.Exchange, cancellationToken);
        if (instrument is null) return BrokerCallResult.Refused(400, UnknownSymbol(order.Symbol, order.Exchange));
        if (!Segments.TryGetValue(order.Exchange, out var segment)) return BrokerCallResult.Refused(400, $"{DisplayName} does not take orders on {order.Exchange}.");
        return await SendOrderAsync(session, new JsonObject
        {
            ["exchangeSegment"] = segment,
            ["exchangeInstrumentID"] = instrument.Token,
            ["productType"] = order.Product,
            ["orderType"] = OrderType(order.PriceType),
            ["orderSide"] = order.Action.ToUpperInvariant(),
            ["timeInForce"] = "DAY",
            ["disclosedQuantity"] = order.DisclosedQuantity,
            ["orderQuantity"] = order.Quantity,
            ["limitPrice"] = (double)order.Price,
            ["stopPrice"] = (double)order.TriggerPrice,
            ["orderUniqueIdentifier"] = "openalgo",
        }, cancellationToken);
    }

    private async Task<BrokerCallResult> SendOrderAsync(BrokerSession session, JsonObject body, CancellationToken cancellationToken)
    {
        using var request = Request(session, HttpMethod.Post, "/orders", body);
        HttpResponseMessage response;
        try
        {
            response = await Client().SendAsync(request, cancellationToken);
        }
        catch (HttpRequestException ex)
        {
            logger.LogWarning(ex, "{Broker} order POST got no answer.", DisplayName);
            return new BrokerCallResult(false, 502, $"The order was sent to {DisplayName} but no reply came back, so it may have been placed. Check the order book on {DisplayName} before trying again.", MayHaveBeenSent: true);
        }

        using (response)
        {
            var reply = await ReadAsync(response, cancellationToken) as JsonObject;
            if (Str(reply?["type"]) == "success" && Str(reply?["result"]?["AppOrderID"]) is { Length: > 0 } id) return BrokerCallResult.Success(id);
            return BrokerCallResult.Refused(response.IsSuccessStatusCode ? 500 : (int)response.StatusCode, Reason(reply, "Failed to place order"));
        }
    }

    public async Task<BrokerCallResult> ModifyAsync(BrokerSession session, BrokerOrderChange change, CancellationToken cancellationToken)
    {
        using var request = Request(session, HttpMethod.Put, "/orders", new JsonObject
        {
            ["appOrderID"] = change.OrderId,
            ["modifiedProductType"] = change.Product,
            ["modifiedOrderType"] = OrderType(change.PriceType),
            ["modifiedOrderQuantity"] = change.Quantity,
            ["modifiedDisclosedQuantity"] = change.DisclosedQuantity,
            ["modifiedLimitPrice"] = (double)change.Price,
            ["modifiedStopPrice"] = (double)change.TriggerPrice,
            ["modifiedTimeInForce"] = "DAY",
            ["orderUniqueIdentifier"] = "openalgo",
        });
        using var response = await SendAsync(request, cancellationToken);
        var reply = await ReadAsync(response, cancellationToken) as JsonObject;
        if (Str(reply?["type"]) == "success") return BrokerCallResult.Success(change.OrderId);
        return BrokerCallResult.Refused(response.IsSuccessStatusCode ? 500 : (int)response.StatusCode, Reason(reply, "Failed to modify order"));
    }

    public async Task<BrokerCallResult> CancelAsync(BrokerSession session, string orderId, CancellationToken cancellationToken)
    {
        using var request = Request(session, HttpMethod.Delete, $"/orders?appOrderID={Uri.EscapeDataString(orderId)}", content: true);
        using var response = await SendAsync(request, cancellationToken);
        var reply = await ReadAsync(response, cancellationToken) as JsonObject;
        if (Str(reply?["type"]) == "success") return BrokerCallResult.Success(orderId);
        return BrokerCallResult.Refused(response.IsSuccessStatusCode ? 500 : (int)response.StatusCode, Reason(reply, "Failed to cancel order"));
    }

    // ---- books -----------------------------------------------------------------------------------------------

    /// <summary>The OpenAlgo symbol and exchange of a row: found by instrument id in the master contract, else the broker's own trading symbol.</summary>
    private async Task<(string Symbol, string Exchange)> MapRowAsync(JsonObject row, string idField, CancellationToken cancellationToken)
    {
        var segment = Str(row["ExchangeSegment"]) ?? "";
        var exchange = Exchanges.GetValueOrDefault(segment, segment);
        var token = Str(row[idField]) ?? "";
        var record = token.Length == 0 ? null : await symbols.FindByTokenAsync(Name, token, exchange, cancellationToken);
        return (record?.Symbol ?? Str(row["TradingSymbol"]) ?? "", exchange);
    }

    private static string OrderId(JsonNode? value) => ((long)Math.Truncate(Float(value))).ToString(System.Globalization.CultureInfo.InvariantCulture);

    public async Task<BrokerBook> GetOrderBookAsync(BrokerSession session, CancellationToken cancellationToken)
    {
        int buy = 0, sell = 0, completed = 0, open = 0, rejected = 0;
        var orders = new JsonArray();
        foreach (var o in (await GetResultAsync(session, "/orders", cancellationToken)).OfType<JsonObject>())
        {
            var (symbol, exchange) = await MapRowAsync(o, "ExchangeInstrumentID", cancellationToken);
            var side = Str(o["OrderSide"]);
            if (side == "BUY") buy++;
            else if (side == "SELL") sell++;
            var raw = Str(o["OrderStatus"]) ?? "";
            if (raw == "Filled") completed++;
            else if (raw == "New") open++;
            else if (raw == "Rejected") rejected++;
            orders.Add(new JsonObject
            {
                ["symbol"] = symbol, ["exchange"] = exchange, ["action"] = Get(o, "OrderSide", ""), ["quantity"] = Get(o, "OrderQuantity", 0),
                ["price"] = Get(o, "OrderPrice", 0.0), ["trigger_price"] = Get(o, "OrderStopPrice", 0.0),
                ["pricetype"] = BookPriceType(Str(o["OrderType"]) ?? ""), ["product"] = Get(o, "ProductType", ""),
                ["orderid"] = OrderId(o["AppOrderID"]), ["order_status"] = BookStatus(raw), ["timestamp"] = Get(o, "LastUpdateDateTime", ""),
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
        foreach (var t in (await GetResultAsync(session, "/orders/trades", cancellationToken)).OfType<JsonObject>())
        {
            var (symbol, exchange) = await MapRowAsync(t, "ExchangeInstrumentID", cancellationToken);
            var quantity = Int(t["OrderQuantity"]);
            var average = Float(t["OrderAverageTradedPrice"]);
            trades.Add(new JsonObject
            {
                ["symbol"] = symbol, ["exchange"] = exchange, ["product"] = Get(t, "ProductType", ""), ["action"] = Get(t, "OrderSide", ""),
                ["quantity"] = quantity, ["average_price"] = average, ["trade_value"] = quantity * average,
                ["orderid"] = OrderId(t["AppOrderID"]), ["timestamp"] = Get(t, "OrderGeneratedDateTime", ""),
            });
        }

        return trades;
    }

    public async Task<JsonArray> GetPositionsAsync(BrokerSession session, CancellationToken cancellationToken)
    {
        var positions = new JsonArray();
        using var request = Request(session, HttpMethod.Get, "/portfolio/positions?dayOrNet=NetWise");
        using var response = await SendAsync(request, cancellationToken);
        var reply = await ReadAsync(response, cancellationToken) as JsonObject;
        RefuseExpired(response, reply);
        if ((reply?["result"] as JsonObject)?["positionList"] is not JsonArray rows) return positions;
        foreach (var p in rows.OfType<JsonObject>())
        {
            var (symbol, exchange) = await MapRowAsync(p, "ExchangeInstrumentId", cancellationToken);
            var quantity = Float(p["Quantity"]);
            var average = quantity > 0 ? Float(p["BuyAveragePrice"]) : quantity < 0 ? Float(p["SellAveragePrice"]) : 0;
            positions.Add(new JsonObject
            {
                ["symbol"] = symbol, ["exchange"] = exchange, ["product"] = Get(p, "ProductType", ""), ["quantity"] = Get(p, "Quantity", 0),
                ["average_price"] = BookFormatting.Fixed2(average), ["ltp"] = Get(p, "ltp", 0.0), ["pnl"] = Get(p, "pnl", 0.0),
            });
        }

        return positions;
    }

    public async Task<BrokerBook> GetHoldingsAsync(BrokerSession session, CancellationToken cancellationToken)
    {
        using var request = Request(session, HttpMethod.Get, "/portfolio/holdings");
        using var response = await SendAsync(request, cancellationToken);
        var reply = await ReadAsync(response, cancellationToken) as JsonObject;
        RefuseExpired(response, reply);
        var holdings = new JsonArray();
        double value = 0;
        var table = (((reply?["result"] as JsonObject)?["RMSHoldings"] as JsonObject)?["Holdings"]) as JsonObject;
        if (Str(reply?["type"]) == "success" && table is not null)
        {
            foreach (var (isin, node) in table)
            {
                if (node is not JsonObject h) continue;
                var token = Str(h["ExchangeNSEInstrumentId"]) ?? "";
                var record = token.Length == 0 ? null : await symbols.FindByTokenAsync(Name, token, "NSE", cancellationToken);
                var quantity = Float(h["HoldingQuantity"]);
                value += quantity * Float(h["BuyAvgPrice"]);
                holdings.Add(new JsonObject
                {
                    ["symbol"] = record?.Symbol ?? isin, ["exchange"] = "NSE", ["quantity"] = Get(h, "HoldingQuantity", 0),
                    ["product"] = "CNC", ["pnl"] = 0.0, ["pnlpercent"] = 0.0,
                });
            }
        }

        return new BrokerBook(holdings, new JsonObject
        {
            ["totalholdingvalue"] = value, ["totalinvvalue"] = value, ["totalprofitandloss"] = 0, ["totalpnlpercentage"] = 0,
        });
    }

    // ---- funds -----------------------------------------------------------------------------------------------

    public async Task<JsonObject> GetFundsAsync(BrokerSession session, CancellationToken cancellationToken)
    {
        using var request = Request(session, HttpMethod.Get, "/user/balance");
        using var response = await SendAsync(request, cancellationToken);
        var reply = await ReadAsync(response, cancellationToken) as JsonObject;
        // OpenAlgo shows an empty balance when the broker refuses the token; a refusal is reported here so nothing is mistaken for a balance.
        RefuseExpired(response, reply);
        if (reply is null || (Str(reply["type"]) == "error" && !response.IsSuccessStatusCode))
            throw new BrokerException($"{DisplayName} could not report funds: {Reason(reply, "try again in a moment.")}");
        var list = (reply["result"] as JsonObject)?["BalanceList"] as JsonArray;
        if (list is null || list.Count == 0) return [];
        var limits = (list[0]?["limitObject"] as JsonObject)?["RMSSubLimits"] as JsonObject;
        string Field(string name) => Str(limits?[name])?.Equals("nan", StringComparison.OrdinalIgnoreCase) == true ? "0.00" : BookFormatting.Fixed2(Float(limits?[name]));
        return new JsonObject
        {
            ["availablecash"] = Field("netMarginAvailable"), ["collateral"] = Field("collateral"), ["m2munrealized"] = Field("UnrealizedMTM"),
            ["m2mrealized"] = Field("RealizedMTM"), ["utiliseddebits"] = Field("marginUtilized"),
        };
    }


    // ---- market data -----------------------------------------------------------------------------------------

    private static readonly Dictionary<string, int> DataSegments = new()
    {
        ["NSE"] = 1, ["NSE_INDEX"] = 1, ["NFO"] = 2, ["CDS"] = 3, ["BSE"] = 11, ["BSE_INDEX"] = 11, ["BFO"] = 12, ["MCX"] = 51,
    };

    /// <summary>POST /apimarketdata/instruments/quotes with the feed token. The quotes of the answer, or the reason the broker gave.</summary>
    private async Task<(List<JsonObject> Quotes, string? Refusal)> QuotesAsync(BrokerSession session, JsonArray instruments, int code, CancellationToken cancellationToken)
    {
        var body = new JsonObject { ["instruments"] = instruments, ["xtsMessageCode"] = code, ["publishFormat"] = "JSON" };
        using var request = new HttpRequestMessage(HttpMethod.Post, sessions.BaseUrl(Name, DefaultBaseUrl) + "/apimarketdata/instruments/quotes");
        request.Headers.TryAddWithoutValidation("authorization", string.IsNullOrEmpty(session.FeedToken) ? session.AuthToken : session.FeedToken);
        request.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
        using var response = await SendAsync(request, cancellationToken);
        var reply = await ReadAsync(response, cancellationToken) as JsonObject;
        if (Str(reply?["type"]) != "success") return ([], Str(reply?["description"]) is { Length: > 0 } d ? d : "no reason was given.");
        var quotes = new List<JsonObject>();
        foreach (var raw in ((reply!["result"] as JsonObject)?["listQuotes"] as JsonArray) ?? [])
        {
            var node = raw is JsonValue v && v.TryGetValue<string>(out var text) && text.Length > 0 ? JsonNode.Parse(text) : raw;
            if (node is JsonObject q) quotes.Add(q);
        }

        return (quotes, null);
    }

    private static JsonArray Instrument(SymbolRecord r, int segment) => new(new JsonObject { ["exchangeSegment"] = segment, ["exchangeInstrumentID"] = r.Token });

    private async Task<(JsonObject Market, double Oi)> MarketAsync(BrokerSession session, string symbol, string exchange, CancellationToken cancellationToken)
    {
        var record = await symbols.FindAsync(Name, symbol, exchange, cancellationToken)
            ?? throw new BrokerException($"{symbol} on {exchange} is not in the {DisplayName} master contract. Check the symbol, or download the master contract again.", 400);
        if (!DataSegments.TryGetValue(exchange, out var segment)) throw new BrokerException($"{DisplayName} has no market data for {exchange}.", 400);
        var (quotes, refusal) = await QuotesAsync(session, Instrument(record, segment), 1502, cancellationToken);
        if (refusal is not null) throw new BrokerException($"{DisplayName} could not send the quote: {refusal}");
        if (quotes.Count == 0) throw new BrokerException($"{DisplayName} has no quote for {symbol} right now.");
        // Open interest is a second call that may fail without spoiling the quote.
        double oi = 0;
        try
        {
            var (interest, _) = await QuotesAsync(session, Instrument(record, segment), 1510, cancellationToken);
            if (interest.Count > 0) oi = Float(interest[0]["OpenInterest"]);
        }
        catch (BrokerException)
        {
        }

        return (quotes[0], oi);
    }

    private static JsonObject QuoteFrom(JsonObject market, double oi)
    {
        var t = market["Touchline"] as JsonObject;
        return new JsonObject
        {
            ["ask"] = Get(t?["AskInfo"] as JsonObject, "Price", 0), ["bid"] = Get(t?["BidInfo"] as JsonObject, "Price", 0),
            ["high"] = Get(t, "High", 0), ["low"] = Get(t, "Low", 0), ["ltp"] = Get(t, "LastTradedPrice", 0), ["open"] = Get(t, "Open", 0),
            ["prev_close"] = Get(t, "Close", 0), ["volume"] = Get(t, "TotalTradedQuantity", 0), ["oi"] = oi,
        };
    }

    public async Task<JsonObject> GetQuoteAsync(BrokerSession session, string symbol, string exchange, CancellationToken cancellationToken)
    {
        var (market, oi) = await MarketAsync(session, symbol, exchange, cancellationToken);
        return QuoteFrom(market, oi);
    }

    public async Task<JsonObject> GetDepthAsync(BrokerSession session, string symbol, string exchange, CancellationToken cancellationToken)
    {
        var (market, oi) = await MarketAsync(session, symbol, exchange, cancellationToken);
        var t = market["Touchline"] as JsonObject;
        static JsonArray Levels(JsonNode? side) => new((side as JsonArray ?? []).Take(5).Select(l => (JsonNode?)new JsonObject
        {
            ["price"] = Get(l as JsonObject, "Price", 0), ["quantity"] = Get(l as JsonObject, "Size", 0),
        }).ToArray());
        return new JsonObject
        {
            ["bids"] = Levels(market["Bids"]), ["asks"] = Levels(market["Asks"]), ["high"] = Get(t, "High", 0), ["low"] = Get(t, "Low", 0),
            ["ltp"] = Get(t, "LastTradedPrice", 0), ["ltq"] = Get(t, "LastTradedQunatity", 0), ["open"] = Get(t, "Open", 0),
            ["prev_close"] = Get(t, "Close", 0), ["volume"] = Get(t, "TotalTradedQuantity", 0), ["oi"] = oi,
            ["totalbuyqty"] = Get(t, "TotalBuyQuantity", 0), ["totalsellqty"] = Get(t, "TotalSellQuantity", 0),
        };
    }

    public async Task<JsonArray> GetMultiQuotesAsync(BrokerSession session, IReadOnlyList<SymbolRecord> instruments, CancellationToken cancellationToken)
    {
        const int batch = 50;
        var results = new JsonArray();
        var skipped = new JsonArray();
        var wanted = new List<(SymbolRecord Record, int Segment)>();
        foreach (var r in instruments)
        {
            if (DataSegments.TryGetValue(r.Exchange, out var segment)) wanted.Add((r, segment));
            else skipped.Add(new JsonObject { ["symbol"] = r.Symbol, ["exchange"] = r.Exchange, ["error"] = $"{DisplayName} has no market data for {r.Exchange}." });
        }

        for (var i = 0; i < wanted.Count; i += batch)
        {
            var chunk = wanted.Skip(i).Take(batch).ToList();
            var (quotes, refusal) = await QuotesAsync(session, new JsonArray(chunk.Select(c => Instrument(c.Record, c.Segment)[0]!.DeepClone()).ToArray()), 1502, cancellationToken);
            if (refusal is not null) throw new BrokerException($"{DisplayName} could not send the quotes: {refusal}");
            foreach (var q in quotes)
            {
                var key = $"{Str(q["ExchangeSegment"])}_{Str(q["ExchangeInstrumentID"])}";
                var match = chunk.FirstOrDefault(c => $"{c.Segment}_{c.Record.Token}" == key);
                if (match.Record is null) continue;
                results.Add(new JsonObject { ["symbol"] = match.Record.Symbol, ["exchange"] = match.Record.Exchange, ["data"] = QuoteFrom(q, 0) });
            }
        }

        foreach (var node in results) skipped.Add(node!.DeepClone());
        return skipped;
    }

    // ---- plumbing --------------------------------------------------------------------------------------------

    private string UnknownSymbol(string symbol, string exchange) =>
        $"{symbol} on {exchange} is not in the {DisplayName} master contract. Check the symbol, or download the master contract again.";

    private static string Reason(JsonObject? reply, string fallback) => Str(reply?["description"]) is { Length: > 0 } d ? d : fallback;

    private void RefuseExpired(HttpResponseMessage response, JsonObject? reply)
    {
        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden || Str(reply?["code"]) is "e-session-0002" or "e-session-0001")
            throw new BrokerException($"{DisplayName} says the session has expired. Log in to {DisplayName} again.", 403);
    }

    private HttpClient Client() => http.CreateClient(Name);

    private HttpRequestMessage Request(BrokerSession session, HttpMethod method, string path, JsonObject? body = null, bool content = false)
    {
        var request = new HttpRequestMessage(method, sessions.BaseUrl(Name, DefaultBaseUrl) + "/interactive" + path);
        request.Headers.TryAddWithoutValidation("authorization", session.AuthToken);
        if (body is not null || content)
        {
            request.Content = new StringContent(body?.ToJsonString() ?? "", Encoding.UTF8);
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
            throw new BrokerException($"{DisplayName} did not answer. Check your connection, then try again.", 502, ex);
        }
    }

    private async Task<JsonArray> GetResultAsync(BrokerSession session, string path, CancellationToken cancellationToken)
    {
        using var request = Request(session, HttpMethod.Get, path);
        using var response = await SendAsync(request, cancellationToken);
        var reply = await ReadAsync(response, cancellationToken) as JsonObject;
        RefuseExpired(response, reply);
        if (reply is null && !response.IsSuccessStatusCode) throw new BrokerException($"{DisplayName} could not answer. Try again in a moment.", 502);
        return reply?["result"] as JsonArray ?? [];
    }
}

/// <summary>Wisdom Capital's XTS server (broker/wisdom).</summary>
[BrokerPlugin]
public sealed class WisdomPlugin(IHttpClientFactory http, BrokerSessionFactory sessions, ISymbolMaster symbols, ILogger<WisdomPlugin> logger)
    : XtsPluginBase(http, sessions, symbols, logger)
{
    public override string Name => "wisdom";
    public override string DisplayName => "Wisdom Capital";
    protected override string DefaultBaseUrl => "https://trade.wisdomcapital.in";
}

/// <summary>5paisa's XTS server (broker/fivepaisaxts).</summary>
[BrokerPlugin]
public sealed class FivepaisaXtsPlugin(IHttpClientFactory http, BrokerSessionFactory sessions, ISymbolMaster symbols, ILogger<FivepaisaXtsPlugin> logger)
    : XtsPluginBase(http, sessions, symbols, logger)
{
    public override string Name => "fivepaisaxts";
    public override string DisplayName => "5paisa XTS";
    protected override string DefaultBaseUrl => "https://xtsmum.5paisa.com";
}

/// <summary>IIFL's XTS server (broker/iifl).</summary>
[BrokerPlugin]
public sealed class IiflPlugin(IHttpClientFactory http, BrokerSessionFactory sessions, ISymbolMaster symbols, ILogger<IiflPlugin> logger)
    : XtsPluginBase(http, sessions, symbols, logger)
{
    public override string Name => "iifl";
    public override string DisplayName => "IIFL";
    protected override string DefaultBaseUrl => "https://ttblaze.iifl.com";
}
