using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using EPTradeBot.Core.Brokers;
using Microsoft.Extensions.Logging;
using static EPTradeBot.Infrastructure.Brokers.JsonRead;

namespace EPTradeBot.Infrastructure.Brokers.Upstox;

/// <summary>
/// Upstox (v2 books, v3 orders on the api-hft host, v3 funds and quotes). Port of broker/upstox
/// (api/order_api.py, api/funds.py, api/data.py quotes and depth, mapping/transform_data.py,
/// mapping/order_data.py), held to tools/contract-fixtures/upstox.fixtures.json. Not ported yet,
/// and refused rather than guessed: history, GTT, margin, login, global indicators and the feed.
/// </summary>
[BrokerPlugin]
public sealed class UpstoxPlugin(IHttpClientFactory http, BrokerSessionFactory sessions, ISymbolMaster symbols, ILogger<UpstoxPlugin> logger)
    : IBrokerOrders, IBrokerPortfolio, IBrokerFunds, IBrokerMultiQuotes
{
    private const string Api = "https://api.upstox.com";
    private const string OrderApi = "https://api-hft.upstox.com";

    public string Name => "upstox";
    public string DisplayName => "Upstox";
    public BrokerCapability Capabilities => BrokerCapability.Orders | BrokerCapability.Portfolio | BrokerCapability.Funds | BrokerCapability.Quotes | BrokerCapability.Depth;
    public IReadOnlyList<string> RequiredKeys { get; } = ["ApiKey", "ApiSecret"];

    private static readonly HashSet<string> OpenStatuses =
    [
        "OPEN", "OPEN PENDING", "TRIGGER PENDING", "VALIDATION PENDING", "MODIFY PENDING", "MODIFY VALIDATION PENDING", "CANCEL PENDING",
        "MODIFIED", "NOT MODIFIED", "NOT CANCELLED", "PUT ORDER REQ RECEIVED", "AFTER MARKET ORDER REQ RECEIVED", "MODIFY AFTER MARKET ORDER REQ RECEIVED",
    ];

    /// <summary>normalize_order_status.</summary>
    public static string NormalizeStatus(string? raw)
    {
        var status = (raw ?? "").Trim().ToUpperInvariant().Replace('_', ' ');
        if (status == "COMPLETE") return "complete";
        if (OpenStatuses.Contains(status)) return "open";
        if (status == "REJECTED") return "rejected";
        if (status is "CANCELLED" or "CANCELED" or "CANCELLED AFTER MARKET ORDER") return "cancelled";
        return status.ToLowerInvariant();
    }

    private static string Product(string product) => product is "CNC" or "NRML" ? "D" : "I";

    private static string BookProduct(string exchange, string product) =>
        (exchange is "NSE" or "BSE") && product == "D" ? "CNC"
        : product == "I" ? "MIS"
        : exchange is "NFO" or "MCX" or "BFO" or "CDS" && product == "D" ? "NRML"
        : product;

    // ---- orders ----------------------------------------------------------------------------------------------

    public async Task<BrokerCallResult> PlaceAsync(BrokerSession session, BrokerOrder order, CancellationToken cancellationToken)
    {
        var instrument = await symbols.FindAsync(Name, order.Symbol, order.Exchange, cancellationToken);
        if (instrument is null) return BrokerCallResult.Refused(400, $"{order.Symbol} on {order.Exchange} is not in the Upstox master contract. Check the symbol, or download the master contract again.");

        var type = order.PriceType is "LIMIT" or "SL" or "SL-M" ? order.PriceType : "MARKET";
        var body = new JsonObject
        {
            ["quantity"] = order.Quantity, ["product"] = Product(order.Product), ["validity"] = "DAY",
            ["price"] = type is "LIMIT" or "SL" ? (double)order.Price : "0",
            ["tag"] = "openalgo", ["instrument_token"] = instrument.Token, ["order_type"] = type, ["transaction_type"] = order.Action.ToUpperInvariant(),
            ["disclosed_quantity"] = order.DisclosedQuantity, ["trigger_price"] = type is "SL" or "SL-M" ? (double)order.TriggerPrice : "0",
            ["is_amo"] = "false",
        };

        HttpResponseMessage response;
        using var request = Request(session, HttpMethod.Post, OrderApi, "/v3/order/place", body);
        try
        {
            response = await Client().SendAsync(request, cancellationToken);
        }
        catch (HttpRequestException ex)
        {
            logger.LogWarning(ex, "Upstox order POST got no answer.");
            return new BrokerCallResult(false, 502,
                "The order was sent to Upstox but no reply came back, so it may have been placed. Check the order book on Upstox before trying again.", MayHaveBeenSent: true);
        }

        using (response)
        {
            var reply = await ReadAsync(response, cancellationToken) as JsonObject;
            if (response.IsSuccessStatusCode && Str(reply?["status"]) == "success" && OrderId(reply?["data"]) is { Length: > 0 } id) return BrokerCallResult.Success(id);
            var status = (int)response.StatusCode;
            return BrokerCallResult.Refused(status == 200 ? 500 : status, ErrorMessage(reply, "Failed to place order."));
        }
    }

    public async Task<BrokerCallResult> ModifyAsync(BrokerSession session, BrokerOrderChange change, CancellationToken cancellationToken)
    {
        var body = new JsonObject
        {
            ["quantity"] = change.Quantity, ["validity"] = "DAY", ["price"] = (double)change.Price, ["order_id"] = change.OrderId,
            ["order_type"] = change.PriceType is "LIMIT" or "SL" or "SL-M" ? change.PriceType : "MARKET",
            ["disclosed_quantity"] = change.DisclosedQuantity, ["trigger_price"] = (double)change.TriggerPrice,
        };
        using var request = Request(session, HttpMethod.Put, OrderApi, "/v3/order/modify", body);
        using var response = await SendAsync(request, cancellationToken);
        var reply = await ReadAsync(response, cancellationToken) as JsonObject;
        if (response.IsSuccessStatusCode && Str(reply?["status"]) == "success") return BrokerCallResult.Success(OrderId(reply?["data"]) ?? change.OrderId);
        // OpenAlgo answers "Failed to modify order" and drops Upstox's reason; the reason is passed on here.
        return BrokerCallResult.Refused(400, ErrorMessage(reply, "Failed to modify order"));
    }

    public async Task<BrokerCallResult> CancelAsync(BrokerSession session, string orderId, CancellationToken cancellationToken)
    {
        using var request = Request(session, HttpMethod.Delete, OrderApi, $"/v3/order/cancel?order_id={Uri.EscapeDataString(orderId)}", content: true);
        using var response = await SendAsync(request, cancellationToken);
        var reply = await ReadAsync(response, cancellationToken) as JsonObject;
        if (response.IsSuccessStatusCode && Str(reply?["status"]) == "success") return BrokerCallResult.Success(OrderId(reply?["data"]) ?? orderId);
        return BrokerCallResult.Refused(400, ErrorMessage(reply, "Failed to cancel order"));
    }

    /// <summary>v3 place answers a list of ids (one per slice); modify and cancel a single id.</summary>
    private static string? OrderId(JsonNode? data) =>
        data?["order_ids"] is JsonArray ids ? ids.Count > 0 ? Str(ids[0]) : null : Str(data?["order_ids"]) ?? Str(data?["order_id"]);

    /// <summary>_extract_error: Upstox's errors list flattened to "CODE: message | ...".</summary>
    private static string ErrorMessage(JsonObject? reply, string fallback)
    {
        if (reply?["errors"] is JsonArray { Count: > 0 } errors)
            return string.Join(" | ", errors.OfType<JsonObject>().Select(e =>
                (Str(e["errorCode"]) ?? Str(e["error_code"])) is { } code ? $"{code}: {Str(e["message"]) ?? "Unknown error"}" : Str(e["message"]) ?? "Unknown error"));
        return Str(reply?["message"]) ?? fallback;
    }

    // ---- books -----------------------------------------------------------------------------------------------

    private async Task<(JsonNode? Symbol, string Product)> MapAsync(JsonObject row, CancellationToken cancellationToken)
    {
        var exchange = Str(row["exchange"]) ?? "";
        var record = await symbols.FindByTokenAsync(Name, Str(row["instrument_token"]) ?? "", exchange, cancellationToken);
        return record is null
            ? (Get(row, "tradingsymbol", ""), Str(row["product"]) ?? "")
            : (record.Symbol, BookProduct(exchange, Str(row["product"]) ?? ""));
    }

    public async Task<BrokerBook> GetOrderBookAsync(BrokerSession session, CancellationToken cancellationToken)
    {
        var rows = await ListAsync(session, "/v2/order/retrieve-all", cancellationToken);
        int buy = 0, sell = 0, completed = 0, open = 0, rejected = 0;
        var orders = new JsonArray();
        foreach (var o in rows)
        {
            var (symbol, product) = await MapAsync(o, cancellationToken);
            var side = Str(o["transaction_type"]);
            if (side == "BUY") buy++;
            else if (side == "SELL") sell++;
            var status = NormalizeStatus(Str(o["status"]));
            if (status == "complete") completed++;
            else if (status == "open") open++;
            else if (status == "rejected") rejected++;
            orders.Add(new JsonObject
            {
                ["symbol"] = symbol, ["exchange"] = Get(o, "exchange", ""), ["action"] = Get(o, "transaction_type", ""), ["quantity"] = Get(o, "quantity", 0),
                ["price"] = Get(o, "price", 0.0), ["trigger_price"] = Get(o, "trigger_price", 0.0), ["pricetype"] = Get(o, "order_type", ""),
                ["product"] = product, ["orderid"] = Get(o, "order_id", ""), ["order_status"] = status, ["timestamp"] = Get(o, "order_timestamp", ""),
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
        var rows = await ListAsync(session, "/v2/order/trades/get-trades-for-day", cancellationToken);
        var trades = new JsonArray();
        foreach (var t in rows)
        {
            var (symbol, product) = await MapAsync(t, cancellationToken);
            trades.Add(new JsonObject
            {
                ["symbol"] = symbol, ["exchange"] = Get(t, "exchange", ""), ["product"] = product, ["action"] = Get(t, "transaction_type", ""),
                ["quantity"] = Get(t, "quantity", 0), ["average_price"] = Get(t, "average_price", 0.0),
                ["trade_value"] = Float(t["quantity"]) * Float(t["average_price"]),
                ["orderid"] = Get(t, "order_id", ""), ["timestamp"] = Get(t, "order_timestamp", ""),
            });
        }

        return trades;
    }

    public async Task<JsonArray> GetPositionsAsync(BrokerSession session, CancellationToken cancellationToken)
    {
        var rows = await ListAsync(session, "/v2/portfolio/short-term-positions", cancellationToken);
        var positions = new JsonArray();
        foreach (var p in rows)
        {
            var (symbol, product) = await MapAsync(p, cancellationToken);
            var quantity = Float(p["quantity"]);
            var average = Float(p["average_price"]);
            if (average == 0)
            {
                // Upstox leaves average_price empty on some positions; the side's own price stands in.
                average = quantity > 0 ? Float(p["buy_price"]) is var b && b != 0 ? b : Float(p["day_buy_price"])
                    : quantity < 0 ? Float(p["sell_price"]) is var s && s != 0 ? s : Float(p["day_sell_price"])
                    : 0;
            }

            positions.Add(new JsonObject
            {
                ["symbol"] = symbol, ["exchange"] = Get(p, "exchange", ""), ["product"] = product, ["quantity"] = Get(p, "quantity", 0),
                ["average_price"] = average, ["pnl"] = Get(p, "pnl", 0.0), ["ltp"] = Get(p, "last_price", 0.0),
            });
        }

        return positions;
    }

    public async Task<BrokerBook> GetHoldingsAsync(BrokerSession session, CancellationToken cancellationToken)
    {
        var rows = await ListAsync(session, "/v2/portfolio/long-term-holdings", cancellationToken);
        var holdings = new JsonArray();
        double value = 0, invested = 0, pnl = 0;
        foreach (var h in rows)
        {
            var average = Float(h["average_price"]);
            var last = Float(h["last_price"]);
            var quantity = Float(h["quantity"]);
            // A holding Upstox cannot price reports pnl null; OpenAlgo's sum then failed the whole book. It counts as 0 here.
            var rowPnl = Float(h["pnl"]);
            value += last * quantity;
            invested += average * quantity;
            pnl += rowPnl;
            holdings.Add(new JsonObject
            {
                ["symbol"] = Get(h, "tradingsymbol", ""), ["exchange"] = Get(h, "exchange", ""), ["quantity"] = Get(h, "quantity", 0),
                ["product"] = Str(h["product"]) == "D" ? "CNC" : Get(h, "product", ""),
                ["average_price"] = average, ["ltp"] = last, ["pnl"] = BookFormatting.Round2(rowPnl),
                // No price is not a total loss: OpenAlgo reported -100% for a holding Upstox could not price.
                ["pnlpercent"] = average == 0 || last == 0 ? 0.0 : BookFormatting.Round2((last - average) / average * 100),
            });
        }

        return new BrokerBook(holdings, new JsonObject
        {
            ["totalholdingvalue"] = value, ["totalinvvalue"] = invested, ["totalprofitandloss"] = pnl,
            ["totalpnlpercentage"] = invested != 0 ? pnl / invested * 100 : 0,
        });
    }

    // ---- funds -----------------------------------------------------------------------------------------------

    public async Task<JsonObject> GetFundsAsync(BrokerSession session, CancellationToken cancellationToken)
    {
        using var request = Request(session, HttpMethod.Get, Api, "/v3/user/get-funds-and-margin", contentType: false);
        request.Headers.TryAddWithoutValidation("Api-Version", "3.0");
        using var response = await SendAsync(request, cancellationToken);
        var reply = await ReadAsync(response, cancellationToken) as JsonObject;
        // OpenAlgo shows zero cash outside Upstox's funds hours and on a refused token; both are reported here instead.
        if (!response.IsSuccessStatusCode || Str(reply?["status"]) != "success")
        {
            var codes = (reply?["errors"] as JsonArray ?? []).OfType<JsonObject>().Select(e => Str(e["errorCode"])).ToList();
            if (codes.Contains("UDAPI100072"))
                throw new BrokerException("Upstox reports funds only from 5:30 AM to midnight IST. Try again within those hours.", 503);
            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                throw new BrokerException("Upstox says the access token is invalid or has expired. Log in to Upstox again.", 403);
            throw new BrokerException($"Upstox could not report funds: {ErrorMessage(reply, "try again in a moment.")}");
        }

        var available = reply!["data"]?["available_to_trade"];
        var cash = available?["cash_available_to_trade"];
        var pledge = available?["pledge_available_to_trade"];
        var pledgeUsed = Float(pledge?["margin_used"]?["total"]);

        double realised = 0, unrealised = 0;
        try
        {
            foreach (var p in await ListAsync(session, "/v2/portfolio/short-term-positions", cancellationToken))
            {
                realised += Float(p["realised"]);
                unrealised += Float(p["unrealised"]);
            }
        }
        catch (BrokerException ex)
        {
            logger.LogWarning(ex, "Could not read Upstox positions for P&L.");
        }

        return new JsonObject
        {
            ["availablecash"] = BookFormatting.Fixed2(Float(cash?["total"])),
            ["collateral"] = BookFormatting.Fixed2(Float(pledge?["margin_from_pledge"]?["total"]) - pledgeUsed),
            ["m2munrealized"] = BookFormatting.Fixed2(unrealised), ["m2mrealized"] = BookFormatting.Fixed2(realised),
            ["utiliseddebits"] = BookFormatting.Fixed2(Float(cash?["margin_used"]?["total"]) + pledgeUsed),
        };
    }

    // ---- market data -----------------------------------------------------------------------------------------

    private async Task<JsonObject> QuoteAsync(BrokerSession session, string symbol, string exchange, CancellationToken cancellationToken)
    {
        // The instrument key, trying the index exchange for an index asked for on its cash exchange and the other way round.
        var record = await symbols.FindAsync(Name, symbol, exchange, cancellationToken);
        if (record is null && exchange is "NSE" or "BSE" or "MCX") record = await symbols.FindAsync(Name, symbol, exchange + "_INDEX", cancellationToken);
        if (record is null && exchange.EndsWith("_INDEX", StringComparison.Ordinal)) record = await symbols.FindAsync(Name, symbol, exchange[..^6], cancellationToken);
        if (record is null) throw new BrokerException($"Error fetching quotes: {symbol} on {exchange} is not in the Upstox master contract.");

        return (await FullQuotesAsync(session, [record.Token], cancellationToken)).TryGetValue(record.Token, out var quote)
            ? quote
            : throw new BrokerException($"Upstox has no quote for {symbol} right now.");
    }

    /// <summary>GET /v3/market-quote/quotes for instrument keys; the quotes by instrument key.</summary>
    private async Task<Dictionary<string, JsonObject>> FullQuotesAsync(BrokerSession session, IEnumerable<string> keys, CancellationToken cancellationToken)
    {
        using var request = Request(session, HttpMethod.Get, Api, $"/v3/market-quote/quotes?instrument_key={Uri.EscapeDataString(string.Join(",", keys))}", contentType: false);
        using var response = await SendAsync(request, cancellationToken);
        var reply = await ReadAsync(response, cancellationToken) as JsonObject;
        if (Str(reply?["status"]) != "success")
        {
            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                throw new BrokerException("Upstox says the access token is invalid or has expired. Log in to Upstox again.", 403);
            throw new BrokerException($"Upstox could not send a quote: {ErrorMessage(reply, "no reason given.")}");
        }

        var quotes = new Dictionary<string, JsonObject>();
        foreach (var quote in (reply!["data"] as JsonObject ?? []).Select(kv => kv.Value).OfType<JsonObject>())
        {
            if (Str(quote["instrument_token"]) is { Length: > 0 } key) quotes[key] = quote;
        }

        return quotes;
    }

    /// <summary>Upstox answers up to 500 instrument keys per full-quote call.</summary>
    private const int QuoteBatch = 500;

    public async Task<JsonArray> GetMultiQuotesAsync(BrokerSession session, IReadOnlyList<SymbolRecord> instruments, CancellationToken cancellationToken)
    {
        var results = new JsonArray();
        for (var start = 0; start < instruments.Count; start += QuoteBatch)
        {
            var wanted = new Dictionary<string, SymbolRecord>();
            foreach (var instrument in instruments.Skip(start).Take(QuoteBatch))
            {
                if (instrument.Token.StartsWith("GLOBAL_INDICATOR|", StringComparison.Ordinal))
                {
                    // OpenAlgo prices these through a separate last-price call, which EP Trade bot does not make yet.
                    results.Add(new JsonObject { ["symbol"] = instrument.Symbol, ["exchange"] = instrument.Exchange, ["error"] = "Upstox global indicators are not priced in EP Trade bot yet." });
                    continue;
                }

                wanted[instrument.Token] = instrument;
            }

            if (wanted.Count == 0) continue;
            var quotes = await FullQuotesAsync(session, wanted.Keys, cancellationToken);
            foreach (var (key, instrument) in wanted)
            {
                results.Add(quotes.TryGetValue(key, out var q)
                    ? new JsonObject { ["symbol"] = instrument.Symbol, ["exchange"] = instrument.Exchange, ["data"] = QuoteFields(q) }
                    : new JsonObject { ["symbol"] = instrument.Symbol, ["exchange"] = instrument.Exchange, ["error"] = "No quote data available" });
            }
        }

        return results;
    }

    public async Task<JsonObject> GetQuoteAsync(BrokerSession session, string symbol, string exchange, CancellationToken cancellationToken) =>
        QuoteFields(await QuoteAsync(session, symbol, exchange, cancellationToken));

    private static JsonObject QuoteFields(JsonObject q)
    {
        var ohlc = q["ohlc"];
        var bid = (q["depth"]?["buy"] as JsonArray) is { Count: > 0 } b ? b[0] : null;
        var ask = (q["depth"]?["sell"] as JsonArray) is { Count: > 0 } s ? s[0] : null;
        static JsonNode F(JsonNode? n) => Truthy(n) ? JsonValue.Create(Float(n)) : 0;
        static JsonNode I(JsonNode? n) => Truthy(n) ? JsonValue.Create(Int(n)) : 0;
        return new JsonObject
        {
            ["ask"] = F(ask?["price"]), ["bid"] = F(bid?["price"]), ["high"] = F(ohlc?["high"]), ["low"] = F(ohlc?["low"]), ["ltp"] = F(q["last_price"]),
            ["open"] = F(ohlc?["open"]), ["prev_close"] = F(q["prev_close_price"]), ["volume"] = I(q["volume"]), ["oi"] = I(q["oi"]),
        };
    }

    public async Task<JsonObject> GetDepthAsync(BrokerSession session, string symbol, string exchange, CancellationToken cancellationToken)
    {
        var q = await QuoteAsync(session, symbol, exchange, cancellationToken);
        var ohlc = q["ohlc"] as JsonObject;
        static JsonArray Side(JsonNode? side) => new((side as JsonArray ?? []).OfType<JsonObject>()
            .Select(o => (JsonNode?)new JsonObject { ["price"] = Get(o, "price", 0), ["quantity"] = Get(o, "quantity", 0) }).ToArray());
        return new JsonObject
        {
            ["asks"] = Side(q["depth"]?["sell"]), ["bids"] = Side(q["depth"]?["buy"]),
            ["high"] = Get(ohlc, "high", 0), ["low"] = Get(ohlc, "low", 0), ["ltp"] = Get(q, "last_price", 0), ["ltq"] = Get(q, "last_quantity", 0),
            ["oi"] = Get(q, "oi", 0), ["open"] = Get(ohlc, "open", 0), ["prev_close"] = Get(q, "prev_close_price", 0),
            ["totalbuyqty"] = Get(q, "total_buy_quantity", 0), ["totalsellqty"] = Get(q, "total_sell_quantity", 0), ["volume"] = Get(q, "volume", 0),
        };
    }

    // ---- plumbing --------------------------------------------------------------------------------------------

    private HttpClient Client() => http.CreateClient(Name);

    private HttpRequestMessage Request(BrokerSession session, HttpMethod method, string host, string pathAndQuery, JsonObject? body = null,
        bool content = false, bool contentType = true)
    {
        var baseUrl = host == OrderApi ? sessions.BaseUrl(Name + "_orders", OrderApi) : sessions.BaseUrl(Name, Api);
        var request = new HttpRequestMessage(method, baseUrl + pathAndQuery);
        request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {session.AuthToken}");
        request.Headers.TryAddWithoutValidation("Accept", "application/json");
        if (body is not null || content || (contentType && method != HttpMethod.Get))
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
            throw new BrokerException("Upstox did not answer. Check your connection, then try again.", 502, ex);
        }
    }

    private async Task<List<JsonObject>> ListAsync(BrokerSession session, string path, CancellationToken cancellationToken)
    {
        using var request = Request(session, HttpMethod.Get, Api, path);
        using var response = await SendAsync(request, cancellationToken);
        var reply = await ReadAsync(response, cancellationToken) as JsonObject;
        if (response.IsSuccessStatusCode && Str(reply?["status"]) == "success") return (reply!["data"] as JsonArray ?? []).OfType<JsonObject>().ToList();
        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            throw new BrokerException("Upstox says the access token is invalid or has expired. Log in to Upstox again.", 403);
        throw new BrokerException($"Upstox could not answer: {ErrorMessage(reply, "try again in a moment.")}");
    }
}
