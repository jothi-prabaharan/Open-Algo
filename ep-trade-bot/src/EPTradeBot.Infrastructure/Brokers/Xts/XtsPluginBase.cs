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
/// Quotes, depth and several quotes (api/data.py) come from the market-data service. History is the ohlc service. Not ported yet, and refused rather than guessed: margin, the feed, and today's daily candle from a quote.
/// </summary>
public abstract partial class XtsPluginBase(IHttpClientFactory http, BrokerSessionFactory sessions, ISymbolMaster symbols, ILogger logger)
    : IBrokerOrders, IBrokerPortfolio, IBrokerFunds, IBrokerLogin, IBrokerMultiQuotes, IBrokerHistory
{
    protected IHttpClientFactory Http => http;

    protected BrokerSessionFactory Sessions => sessions;

    public abstract string Name { get; }
    public abstract string DisplayName { get; }

    /// <summary>The address of the broker's XTS server, without the /interactive path.</summary>
    protected abstract string DefaultBaseUrl { get; }

    /// <summary>Where the market-data service lives under the address: most XTS servers use /apimarketdata, some /apibinarymarketdata.</summary>
    protected virtual string MarketDataPath => "/apimarketdata";

    /// <summary>RMoney's several-quotes answer carries the best bid and ask sizes and the open interest (a second call); the others carry neither.</summary>
    protected virtual bool RichMultiQuotes => false;

    /// <summary>The price type and price an order is sent with. Most brokers send what was asked; iBulls turns a market order into a limit order.</summary>
    protected virtual Task<(string PriceType, decimal Price)> PriceAsync(BrokerSession session, BrokerOrder order, CancellationToken cancellationToken) =>
        Task.FromResult((order.PriceType, order.Price));

    public virtual BrokerCapability Capabilities => BrokerCapability.Orders | BrokerCapability.Portfolio | BrokerCapability.Funds | BrokerCapability.Quotes | BrokerCapability.Depth | BrokerCapability.History | BrokerCapability.Streaming;
    public IReadOnlyList<string> RequiredKeys { get; } = ["ApiKey", "ApiSecret", "ApiKeyMarket", "ApiSecretMarket"];

    private static readonly Dictionary<string, string> Segments = new()
    {
        ["NSE"] = "NSECM", ["BSE"] = "BSECM", ["NFO"] = "NSEFO", ["BFO"] = "BSEFO", ["MCX"] = "MCXFO", ["CDS"] = "NSECD",
    };

    private static readonly Dictionary<string, string> Exchanges = Segments.ToDictionary(kv => kv.Value, kv => kv.Key);

    protected static string OrderType(string priceType) => priceType switch { "LIMIT" => "LIMIT", "SL" => "STOPLIMIT", "SL-M" => "STOPMARKET", _ => "MARKET" };

    private static string BookPriceType(string orderType) => orderType switch { "Limit" => "LIMIT", "Market" => "MARKET", "StopLimit" => "SL", "StopMarket" => "SL-M", _ => orderType };

    private static string BookStatus(string status) => status switch { "Filled" => "complete", "Rejected" => "rejected", "Cancelled" => "cancelled", "New" => "open", "Trigger Pending" => "trigger pending", _ => status };

    // ---- orders ----------------------------------------------------------------------------------------------

    public async Task<BrokerCallResult> PlaceAsync(BrokerSession session, BrokerOrder order, CancellationToken cancellationToken)
    {
        var instrument = await symbols.FindAsync(Name, order.Symbol, order.Exchange, cancellationToken);
        if (instrument is null) return BrokerCallResult.Refused(400, UnknownSymbol(order.Symbol, order.Exchange));
        if (!Segments.TryGetValue(order.Exchange, out var segment)) return BrokerCallResult.Refused(400, $"{DisplayName} does not take orders on {order.Exchange}.");
        var (priceType, price) = await PriceAsync(session, order, cancellationToken);
        return await SendOrderAsync(session, new JsonObject
        {
            ["exchangeSegment"] = segment,
            ["exchangeInstrumentID"] = instrument.Token,
            ["productType"] = order.Product,
            ["orderType"] = OrderType(priceType),
            ["orderSide"] = order.Action.ToUpperInvariant(),
            ["timeInForce"] = "DAY",
            ["disclosedQuantity"] = order.DisclosedQuantity,
            ["orderQuantity"] = order.Quantity,
            ["limitPrice"] = (double)price,
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
        if ((reply?["result"] is JsonArray direct ? direct : (reply?["result"] as JsonObject)?["positionList"]) is not JsonArray rows) return positions;
        foreach (var p in rows.OfType<JsonObject>())
        {
            var (symbol, exchange) = await MapRowAsync(p, p.ContainsKey("ExchangeInstrumentID") ? "ExchangeInstrumentID" : "ExchangeInstrumentId", cancellationToken);
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
        var limits = (BalanceEntry(list)?["limitObject"] as JsonObject)?["RMSSubLimits"] as JsonObject;
        string Field(string name) => Str(limits?[name])?.Equals("nan", StringComparison.OrdinalIgnoreCase) == true ? "0.00" : BookFormatting.Fixed2(Float(limits?[name]));
        return new JsonObject
        {
            ["availablecash"] = Field("netMarginAvailable"), ["collateral"] = Field("collateral"), ["m2munrealized"] = Field("UnrealizedMTM"),
            ["m2mrealized"] = Field("RealizedMTM"), ["utiliseddebits"] = Field("marginUtilized"),
        };
    }


    // ---- market data -----------------------------------------------------------------------------------------

    protected static readonly Dictionary<string, int> DataSegments = new()
    {
        ["NSE"] = 1, ["NSE_INDEX"] = 1, ["NFO"] = 2, ["CDS"] = 3, ["BSE"] = 11, ["BSE_INDEX"] = 11, ["BFO"] = 12, ["MCX"] = 51,
    };

    /// <summary>POST /apimarketdata/instruments/quotes with the feed token. The quotes of the answer, or the reason the broker gave.</summary>
    private async Task<(List<JsonObject> Quotes, string? Refusal)> QuotesAsync(BrokerSession session, JsonArray instruments, int code, CancellationToken cancellationToken)
    {
        var body = new JsonObject { ["instruments"] = instruments, ["xtsMessageCode"] = code, ["publishFormat"] = "JSON" };
        using var request = new HttpRequestMessage(HttpMethod.Post, sessions.BaseUrl(Name, DefaultBaseUrl) + MarketDataPath + "/instruments/quotes");
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

    /// <summary>The open interest of a batch from a second call; a refusal leaves every figure at 0, and only a figure that is not 0 is set.</summary>
    private async Task MergeOpenInterestAsync(BrokerSession session, List<(SymbolRecord Record, int Segment)> chunk, JsonArray results, CancellationToken cancellationToken)
    {
        var (interest, refusal) = await QuotesAsync(session, new JsonArray(chunk.Select(c => Instrument(c.Record, c.Segment)[0]!.DeepClone()).ToArray()), 1510, cancellationToken);
        if (refusal is not null) return;
        var open = new Dictionary<string, double>();
        foreach (var q in interest) open[$"{Str(q["ExchangeSegment"])}_{Str(q["ExchangeInstrumentID"])}"] = Float(q["OpenInterest"]);
        foreach (var (record, segment) in chunk)
        {
            if (!open.TryGetValue($"{segment}_{record.Token}", out var oi) || oi == 0) continue;
            foreach (var r in results.OfType<JsonObject>().Where(r => Str(r["symbol"]) == record.Symbol && Str(r["exchange"]) == record.Exchange))
                r["data"]!["oi"] = oi;
        }
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
                var data = QuoteFrom(q, 0);
                if (RichMultiQuotes)
                {
                    var touch = q["Touchline"] as JsonObject;
                    data["ask_qty"] = Get(touch?["AskInfo"] as JsonObject, "Size", 0);
                    data["bid_qty"] = Get(touch?["BidInfo"] as JsonObject, "Size", 0);
                }

                results.Add(new JsonObject { ["symbol"] = match.Record.Symbol, ["exchange"] = match.Record.Exchange, ["data"] = data });
            }

            if (RichMultiQuotes) await MergeOpenInterestAsync(session, chunk, results, cancellationToken);
        }

        foreach (var node in results) skipped.Add(node!.DeepClone());
        return skipped;
    }


    // ---- history ---------------------------------------------------------------------------------------------

    private static readonly Dictionary<string, (string Compression, int Minutes)> Compressions = new()
    {
        ["1s"] = ("1", 0), ["1m"] = ("60", 1), ["2m"] = ("120", 2), ["3m"] = ("180", 3), ["5m"] = ("300", 5), ["10m"] = ("600", 10),
        ["15m"] = ("900", 15), ["30m"] = ("1800", 30), ["60m"] = ("3600", 60), ["D"] = ("D", 0),
    };

    private static readonly Dictionary<string, string> HistorySegments = new(Segments) { ["NSE_INDEX"] = "NSECM", ["BSE_INDEX"] = "BSECM" };

    public IReadOnlyList<string> HistoryTimeframes { get; } = [.. Compressions.Keys];

    public async Task<JsonArray> GetHistoryAsync(BrokerSession session, SymbolRecord instrument, string interval, DateOnly start, DateOnly end, CancellationToken cancellationToken)
    {
        if (!Compressions.TryGetValue(interval, out var compression))
            throw new BrokerException($"{DisplayName} does not offer {interval} candles. Choose one of: {string.Join(", ", Compressions.Keys)}.", 400);
        if (!HistorySegments.TryGetValue(instrument.Exchange, out var segment)) throw new BrokerException($"{DisplayName} has no candles for {instrument.Exchange}.", 400);

        var daily = interval == "D";
        var rows = new SortedDictionary<long, (double Open, double High, double Low, double Close, long Volume)>();
        for (var from = start; from <= end;)
        {
            // The service answers a week at a time; each request covers whole days.
            var to = from.AddDays(6) < end ? from.AddDays(6) : end;
            var query = $"exchangeSegment={segment}&exchangeInstrumentID={Uri.EscapeDataString(instrument.Token)}"
                + $"&startTime={Uri.EscapeDataString(Stamp(from, "000000"))}&endTime={Uri.EscapeDataString(Stamp(to, "235959"))}&compressionValue={compression.Compression}";
            using var request = new HttpRequestMessage(HttpMethod.Get, sessions.BaseUrl(Name, DefaultBaseUrl) + MarketDataPath + "/instruments/ohlc?" + query);
            request.Headers.TryAddWithoutValidation("authorization", string.IsNullOrEmpty(session.FeedToken) ? session.AuthToken : session.FeedToken);
            using var response = await SendAsync(request, cancellationToken);
            var reply = await ReadAsync(response, cancellationToken) as JsonObject;
            if (Str(reply?["type"]) != "success")
                throw new BrokerException($"{DisplayName} could not send the candles for {from:yyyy-MM-dd} to {to:yyyy-MM-dd}: {Reason(reply, "no reason was given.")}");

            foreach (var row in (Str((reply!["result"] as JsonObject)?["dataReponse"]) ?? "").Trim().Split(',', StringSplitOptions.RemoveEmptyEntries))
            {
                var f = row.Split('|');
                if (f.Length < 6) continue;
                if (!long.TryParse(f[0], System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var stamp)
                    || !TryNumber(f[1], out var open) || !TryNumber(f[2], out var high) || !TryNumber(f[3], out var low) || !TryNumber(f[4], out var close)
                    || !long.TryParse(f[5], System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var volume)) continue;
                // Two candles with one timestamp: the first one received is kept.
                rows.TryAdd(stamp, (open, high, low, close, volume));
            }

            from = to.AddDays(1);
        }

        var candles = new JsonArray();
        foreach (var (stamp, c) in rows)
        {
            // Daily candles sit at midnight of their date; intraday ones are moved from XTS's clock to IST and start on the candle boundary.
            var shifted = daily ? stamp - stamp % 86400 : stamp - 19800;
            if (!daily && compression.Minutes > 0) shifted -= shifted % (compression.Minutes * 60L);
            candles.Add(new JsonObject { ["timestamp"] = shifted, ["open"] = c.Open, ["high"] = c.High, ["low"] = c.Low, ["close"] = c.Close, ["volume"] = c.Volume });
        }

        return candles;
    }

    private static string Stamp(DateOnly day, string time) => day.ToString("MMM dd yyyy", System.Globalization.CultureInfo.InvariantCulture) + " " + time;

    private static bool TryNumber(string text, out double value) =>
        double.TryParse(text, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out value);

    /// <summary>Which balance entry the funds come from: the first, unless a broker names the one that covers everything.</summary>
    protected virtual JsonNode? BalanceEntry(JsonArray list) => list[0];

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

    // Instruments of one mode are subscribed in requests of up to fifty.
    public override EPTradeBot.Core.Streaming.Feeds.IFeedCodec CreateFeedCodec() => new EPTradeBot.Core.Streaming.Feeds.XtsFeedCodec(Name, batch: 50);
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

/// <summary>
/// iBulls Securities' XTS server (broker/ibulls). A market order is turned into a limit order priced 0.1 percent through the book (bid plus 0.1 percent
/// to buy, ask minus 0.1 percent to sell) so that it fills like a market order without taking any price. If no quote can be had the order goes as a market order.
/// </summary>
[BrokerPlugin]
public sealed class IbullsPlugin(IHttpClientFactory http, BrokerSessionFactory sessions, ISymbolMaster symbols, ILogger<IbullsPlugin> logger)
    : XtsPluginBase(http, sessions, symbols, logger)
{
    private readonly ILogger log = logger;

    public override string Name => "ibulls";
    public override string DisplayName => "iBulls";
    protected override string DefaultBaseUrl => "https://xts.ibullssecurities.com";
    protected override string MarketDataPath => "/apibinarymarketdata";

    protected override async Task<(string PriceType, decimal Price)> PriceAsync(BrokerSession session, BrokerOrder order, CancellationToken cancellationToken)
    {
        if (order.PriceType != "MARKET" || string.IsNullOrEmpty(session.FeedToken)) return (order.PriceType, order.Price);
        try
        {
            var quote = await GetQuoteAsync(session, order.Symbol, order.Exchange, cancellationToken);
            if (order.Action.Equals("BUY", StringComparison.OrdinalIgnoreCase))
            {
                var bid = JsonRead.Float(quote["bid"]);
                if (bid > 0) return ("LIMIT", (decimal)BookFormatting.Round2(bid * 1.001));
            }
            else if (order.Action.Equals("SELL", StringComparison.OrdinalIgnoreCase))
            {
                var ask = JsonRead.Float(quote["ask"]);
                if (ask > 0) return ("LIMIT", (decimal)BookFormatting.Round2(ask * 0.999));
            }
        }
        catch (Exception ex) when (ex is BrokerException or HttpRequestException)
        {
            log.LogWarning(ex, "Could not price the iBulls market order from a quote; it goes as a market order.");
        }

        return (order.PriceType, order.Price);
    }
}

/// <summary>Jainam's XTS server (broker/jainamxts). Its interactive sign-in carries an access token with the fixed value "jainamxts" in place of a source.</summary>
[BrokerPlugin]
public sealed class JainamXtsPlugin(IHttpClientFactory http, BrokerSessionFactory sessions, ISymbolMaster symbols, ILogger<JainamXtsPlugin> logger)
    : XtsPluginBase(http, sessions, symbols, logger)
{
    public override string Name => "jainamxts";
    public override string DisplayName => "Jainam";
    protected override string DefaultBaseUrl => "https://jtrade.jainam.in:5000";
    protected override string MarketDataPath => "/apibinarymarketdata";
    protected override string SocketPath => "/apibinarymarketdata/socket.io";

    // A tick goes to every mode the instrument is subscribed in, whatever its message code.
    public override EPTradeBot.Core.Streaming.Feeds.IFeedCodec CreateFeedCodec() => new EPTradeBot.Core.Streaming.Feeds.XtsFeedCodec(Name, routesByInstrument: true);

    protected override JsonObject SessionBody(string appKey, string secretKey, IReadOnlyDictionary<string, string> input) =>
        new() { ["appKey"] = appKey, ["secretKey"] = secretKey, ["accessToken"] = "jainamxts" };
}

/// <summary>
/// CompositEdge's XTS server (broker/compositedge). The trader signs in on CompositEdge's page, which sends the session back with an access token that is
/// exchanged for the interactive session. The two sessions must name the same account, or the sign-in is refused.
/// </summary>
[BrokerPlugin]
public sealed class CompositEdgePlugin(IHttpClientFactory http, BrokerSessionFactory sessions, ISymbolMaster symbols, ILogger<CompositEdgePlugin> logger)
    : XtsPluginBase(http, sessions, symbols, logger)
{
    public override string Name => "compositedge";
    public override string DisplayName => "CompositEdge";
    protected override string DefaultBaseUrl => "https://xts.compositedge.com";
    protected override bool RequiresSameAccount => true;

    public override LoginMethod LoginMethod => LoginMethod.Redirect;

    public override string? LoginUrl(IReadOnlyDictionary<string, string> keys, string state) => ThirdPartyUrl(keys, state);

    /// <summary>The page sends the session back as JSON holding the access token; a bare token is taken as it is.</summary>
    protected override JsonObject SessionBody(string appKey, string secretKey, IReadOnlyDictionary<string, string> input)
    {
        var given = input.TryGetValue("request_token", out var t) ? t.Trim() : "";
        if (given.StartsWith('{') || given.StartsWith('"'))
        {
            try
            {
                var node = JsonNode.Parse(given);
                if (node is JsonValue v && v.TryGetValue<string>(out var inner)) node = JsonNode.Parse(inner);
                if (node is JsonObject session && JsonRead.Str(session["accessToken"]) is { Length: > 0 } token) given = token;
            }
            catch (System.Text.Json.JsonException)
            {
            }
        }

        return new JsonObject { ["appKey"] = appKey, ["secretKey"] = secretKey, ["accessToken"] = given };
    }
}

/// <summary>
/// RMoney's XTS server (broker/rmoney). The trader signs in on RMoney's page, which posts back a session holding the interactive token itself; no exchange is needed.
/// RMoney is asked who the token belongs to (the user id posted beside the token is only a label), and that account must be the market-data keys' account.
/// </summary>
[BrokerPlugin]
public sealed class RmoneyPlugin(IHttpClientFactory http, BrokerSessionFactory sessions, ISymbolMaster symbols, ILogger<RmoneyPlugin> logger)
    : XtsPluginBase(http, sessions, symbols, logger), IBrokerMargin
{
    // Instruments of one mode are subscribed in requests of up to fifty, with ids sent as numbers; LTP is asked for as a touchline.
    public override EPTradeBot.Core.Streaming.Feeds.IFeedCodec CreateFeedCodec() => new EPTradeBot.Core.Streaming.Feeds.XtsFeedCodec(Name, batch: 50, rmoney: true);

    protected override bool StampsQuoteCode => true;

    public override string Name => "rmoney";
    public override string DisplayName => "RMoney";
    protected override string DefaultBaseUrl => "https://xts.rmoneyindia.co.in:3000";
    protected override string MarketDataPath => "/apibinarymarketdata";
    protected override bool RichMultiQuotes => true;

    public override BrokerCapability Capabilities => base.Capabilities | BrokerCapability.Margin;

    /// <summary>The margin of a basket (POST /interactive/orders/margindetails): what is required, what is available, and any shortfall.</summary>
    public async Task<JsonObject> CalculateMarginAsync(BrokerSession session, IReadOnlyList<MarginLeg> legs, CancellationToken cancellationToken)
    {
        var portfolio = new JsonArray();
        foreach (var l in legs)
        {
            if (!DataSegments.TryGetValue(l.Instrument.Exchange, out var segment) || l.Instrument.Exchange.EndsWith("_INDEX", StringComparison.Ordinal))
                throw new BrokerException($"{DisplayName} has no margin calculation for {l.Instrument.Exchange}.", 400);
            portfolio.Add(new JsonObject
            {
                ["exchange"] = segment, ["exchangeInstrumentId"] = long.Parse(l.Instrument.Token, System.Globalization.CultureInfo.InvariantCulture),
                ["productType"] = l.Product, ["orderType"] = OrderType(l.PriceType), ["orderSide"] = l.Action.ToUpperInvariant(),
                ["quantity"] = l.Quantity, ["price"] = (double)l.Price, ["stopPrice"] = (double)l.TriggerPrice, ["orderSessionType"] = 1,
            });
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, Sessions.BaseUrl(Name, DefaultBaseUrl) + "/interactive/orders/margindetails");
        request.Headers.TryAddWithoutValidation("authorization", session.AuthToken);
        request.Content = new StringContent(new JsonObject { ["portfolio"] = portfolio }.ToJsonString(), System.Text.Encoding.UTF8, "application/json");
        HttpResponseMessage response;
        try
        {
            response = await Http.CreateClient(Name).SendAsync(request, cancellationToken);
        }
        catch (HttpRequestException ex)
        {
            throw new BrokerException($"{DisplayName} did not answer. Check your connection, then try again.", 502, ex);
        }

        using (response)
        {
            var reply = await JsonRead.ReadAsync(response, cancellationToken) as JsonObject
                ?? throw new BrokerException($"{DisplayName} sent a reply EP Trade bot could not read. Try again in a moment.", 502);
            if (response.StatusCode is System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden)
                throw new BrokerException($"{DisplayName} says the session has expired. Log in to {DisplayName} again.", 403);
            if (JsonRead.Str(reply["type"]) != "success")
                throw new BrokerException($"{DisplayName} could not work out the margin: {(JsonRead.Str(reply["description"]) is { Length: > 0 } d ? d : "no reason was given.")}", response.IsSuccessStatusCode ? 400 : (int)response.StatusCode);
            if ((reply["result"] as JsonObject)?["brokerageDeatils"] is not JsonObject { Count: > 0 } details)
                throw new BrokerException($"{DisplayName} sent no margin details. Try again in a moment.", 502);
            return new JsonObject
            {
                ["total_margin_required"] = JsonRead.Float(details["MarginRequired"]), ["margin_available"] = JsonRead.Float(details["MarginAvailable"]),
                ["margin_shortfall"] = JsonRead.Float(details["MarginShortfall"]), ["is_valid"] = JsonRead.Truthy(details["IsValid"]),
                ["error_message"] = JsonRead.Get(details, "ErrorMessage", ""),
            };
        }
    }

    public override LoginMethod LoginMethod => LoginMethod.Redirect;

    public override string? LoginUrl(IReadOnlyDictionary<string, string> keys, string state) => ThirdPartyUrl(keys, state);

    /// <summary>The balance entry for everything ("ALL|ALL|ALL"), else the first.</summary>
    protected override JsonNode? BalanceEntry(JsonArray list) =>
        list.FirstOrDefault(e => JsonRead.Str((e as JsonObject)?["limitHeader"]) == "ALL|ALL|ALL") ?? list[0];

    public override async Task<BrokerLoginResult> LoginAsync(IReadOnlyDictionary<string, string> keys, IReadOnlyDictionary<string, string> input, CancellationToken cancellationToken)
    {
        var token = SessionToken(input.TryGetValue("request_token", out var given) ? given.Trim() : "");
        if (token.Length == 0) return BrokerLoginResult.Refused("RMoney did not send the sign-in back. Start the sign-in again.");

        var account = await AccountOfAsync(token, cancellationToken);
        if (account is null)
            return BrokerLoginResult.Refused("RMoney did not confirm which account this login is for, so it was not stored. Log in again.", 409);

        // The feed token is wanted but not required: a refused market-data sign-in leaves the session without one.
        string? feed = null, feedUser = null;
        if (keys.TryGetValue("ApiKeyMarket", out var marketKey) && keys.TryGetValue("ApiSecretMarket", out var marketSecret))
            (feed, _, feedUser) = await TokenAsync(MarketDataPath + "/auth/login", KeyBody(marketKey.Trim(), marketSecret.Trim()), cancellationToken);

        if (!string.IsNullOrWhiteSpace(feedUser) && !string.Equals(account.Trim(), feedUser.Trim(), StringComparison.OrdinalIgnoreCase))
            return BrokerLoginResult.Refused("This RMoney sign-in is for a different account than the market data keys saved for RMoney. Sign in with that account, or check the keys under Broker Configuration.", 409);
        return BrokerLoginResult.Success(token, feed);
    }

    /// <summary>The page posts the session as JSON (sometimes a JSON string holding JSON) with the token in it; a bare token is taken as given.</summary>
    private static string SessionToken(string given)
    {
        if (!(given.StartsWith('{') || given.StartsWith('"'))) return given;
        try
        {
            var node = JsonNode.Parse(given);
            if (node is JsonValue v && v.TryGetValue<string>(out var inner)) node = JsonNode.Parse(inner);
            return JsonRead.Str((node as JsonObject)?["token"]) ?? "";
        }
        catch (System.Text.Json.JsonException)
        {
            return "";
        }
    }

    private async Task<string?> AccountOfAsync(string token, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, Sessions.BaseUrl(Name, DefaultBaseUrl) + "/interactive/user/profile");
        request.Headers.TryAddWithoutValidation("authorization", token);
        HttpResponseMessage response;
        try
        {
            response = await Http.CreateClient(Name).SendAsync(request, cancellationToken);
        }
        catch (HttpRequestException)
        {
            return null;
        }

        using (response)
        {
            if (!response.IsSuccessStatusCode) return null;
            var body = await JsonRead.ReadAsync(response, cancellationToken) as JsonObject;
            if (JsonRead.Str(body?["type"]) != "success" || body?["result"] is not JsonObject result) return null;
            foreach (var key in new[] { "ClientId", "ClientID", "clientId", "clientID" })
                if (JsonRead.Str(result[key])?.Trim() is { Length: > 0 } id) return id;
            return null;
        }
    }
}
