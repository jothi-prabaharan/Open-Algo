using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using EPTradeBot.Core.Brokers;
using EPTradeBot.Core.Brokers.Dhan;
using EPTradeBot.Core.Calendar;
using EPTradeBot.Core.Time;
using Microsoft.Extensions.Logging;
using static EPTradeBot.Infrastructure.Brokers.JsonRead;

namespace EPTradeBot.Infrastructure.Brokers.Dhan;

/// <summary>Dhan v2 (broker/dhan). Request and response mapping held to tools/contract-fixtures/dhan.fixtures.json.</summary>
[BrokerPlugin]
public sealed class DhanPlugin(IHttpClientFactory http, BrokerSessionFactory sessions, ISymbolMaster symbols, IMarketSessions calendar, TimeProvider clock, ILogger<DhanPlugin> logger)
    : DhanPluginBase(http, sessions, symbols, calendar, clock, logger), IBrokerLoginStart
{
    public LoginMethod LoginMethod => LoginMethod.Redirect;

    public IReadOnlyList<string> LoginFields { get; } = [];

    /// <summary>Dhan's page cannot be worked out without asking Dhan, so the address comes from <see cref="StartAsync"/>.</summary>
    public string? LoginUrl(IReadOnlyDictionary<string, string> keys, string state) => null;

    public Task<LoginStart> StartAsync(IReadOnlyDictionary<string, string> keys, string? clientId, string state, CancellationToken cancellationToken) =>
        ConsentAsync(keys, clientId, cancellationToken);

    public Task<BrokerLoginResult> LoginAsync(IReadOnlyDictionary<string, string> keys, IReadOnlyDictionary<string, string> input, CancellationToken cancellationToken) =>
        SignInAsync(keys, input, cancellationToken);

    public override string Name => "dhan";
    public override string DisplayName => "Dhan";
    protected override string DefaultBaseUrl => "https://api.dhan.co";
    protected override bool Sandbox => false;
}

/// <summary>
/// Dhan's sandbox (broker/dhan_sandbox). Held to dhan_sandbox.fixtures.json. Its quotes come
/// from today's one-minute candles, because the sandbox has no market feed; unlike the
/// Python plugin, no price, depth or open interest is ever invented when the sandbox has none.
/// </summary>
[BrokerPlugin]
public sealed class DhanSandboxPlugin(IHttpClientFactory http, BrokerSessionFactory sessions, ISymbolMaster symbols, IMarketSessions calendar, TimeProvider clock, ILogger<DhanSandboxPlugin> logger)
    : DhanPluginBase(http, sessions, symbols, calendar, clock, logger)
{
    public override string Name => "dhan_sandbox";
    public override string DisplayName => "Dhan Sandbox";
    protected override string DefaultBaseUrl => "https://sandbox.dhan.co";
    protected override bool Sandbox => true;
}

/// <summary>
/// The Dhan family. Port of broker/dhan and broker/dhan_sandbox: order_api.py, funds.py,
/// data.py (quotes, depth), mapping/transform_data.py and mapping/order_data.py.
/// Not ported yet, and refused rather than guessed: GTT and the feed. Sign-in is the live Dhan's only.
/// </summary>
public abstract partial class DhanPluginBase(IHttpClientFactory http, BrokerSessionFactory sessions, ISymbolMaster symbols, IMarketSessions calendar, TimeProvider clock, ILogger logger)
    : IBrokerOrders, IBrokerPortfolio, IBrokerFunds, IBrokerMultiQuotes, IBrokerHistory, IBrokerMargin
{
    public abstract string Name { get; }
    public abstract string DisplayName { get; }
    protected abstract string DefaultBaseUrl { get; }

    /// <summary>The sandbox variant: client-id on every call, fuller order payloads, more order states, UTC times, quotes from candles.</summary>
    protected abstract bool Sandbox { get; }

    public BrokerCapability Capabilities => BrokerCapability.Orders | BrokerCapability.Portfolio | BrokerCapability.Funds | BrokerCapability.Quotes | BrokerCapability.Depth | BrokerCapability.History | BrokerCapability.Margin;
    public IReadOnlyList<string> RequiredKeys { get; } = ["ClientId"];

    // ---- orders ----------------------------------------------------------------------------------------------

    public async Task<BrokerCallResult> PlaceAsync(BrokerSession session, BrokerOrder order, CancellationToken cancellationToken)
    {
        var client = ClientId(session);
        if (Sandbox && client is null) return BrokerCallResult.Refused(400, NoClientId);
        var instrument = await symbols.FindAsync(Name, order.Symbol, order.Exchange, cancellationToken);
        if (instrument is null) return BrokerCallResult.Refused(400, UnknownSymbol(order.Symbol, order.Exchange));
        if (DhanRules.Segment(order.Exchange) is not { } segment) return BrokerCallResult.Refused(400, $"{DisplayName} does not take orders on {order.Exchange}.");

        var body = new JsonObject
        {
            ["dhanClientId"] = client,
            ["transactionType"] = order.Action.ToUpperInvariant(),
            ["exchangeSegment"] = segment,
            ["productType"] = DhanRules.Product(order.Product),
            ["orderType"] = DhanRules.OrderType(order.PriceType),
            ["validity"] = "DAY",
            ["securityId"] = instrument.Token,
            ["quantity"] = order.Quantity,
        };

        var stop = order.PriceType is "SL" or "SL-M";
        if (stop && order.TriggerPrice <= 0) return BrokerCallResult.Refused(500, NoTrigger);
        if (Sandbox)
        {
            body["disclosedQuantity"] = order.DisclosedQuantity;
            body["price"] = (double)order.Price;
            body["triggerPrice"] = (double)order.TriggerPrice;
            body["afterMarketOrder"] = false;
            // A short reference per order, as the sandbox plugin sends.
            body["correlationId"] = Guid.NewGuid().ToString("N")[..8];
            body["amoTime"] = "OPEN";
        }
        else
        {
            if (order.PriceType is "LIMIT" or "SL") body["price"] = (double)order.Price;
            if (order.DisclosedQuantity > 0) body["disclosedQuantity"] = order.DisclosedQuantity;
            if (stop) body["triggerPrice"] = (double)order.TriggerPrice;
            if (order.PriceType == "SL-M")
            {
                if (DhanRules.ProtectedLimit(order.Symbol, order.Action, (double)order.TriggerPrice, instrument.TickSize ?? 0) is not { } limit)
                    return BrokerCallResult.Refused(500, NoTick(order.Symbol, order.Exchange));
                body["orderType"] = "STOP_LOSS";
                body["price"] = limit;
            }
        }

        using var request = Request(session, HttpMethod.Post, "/v2/orders", body, clientHeader: client is not null);
        HttpResponseMessage response;
        try
        {
            response = await Client().SendAsync(request, cancellationToken);
        }
        catch (HttpRequestException ex)
        {
            logger.LogWarning(ex, "{Broker} order POST got no answer.", DisplayName);
            return new BrokerCallResult(false, 502, UnansweredOrder, MayHaveBeenSent: true);
        }

        using (response)
        {
            var reply = await ReadAsync(response, cancellationToken) as JsonObject;
            var status = (int)response.StatusCode;
            if (status is 200 or 201 && Str(reply?["orderId"]) is { Length: > 0 } id) return BrokerCallResult.Success(id);
            return BrokerCallResult.Refused(status == 200 ? 500 : status, Reason(reply, "Failed to place order"));
        }
    }

    public async Task<BrokerCallResult> ModifyAsync(BrokerSession session, BrokerOrderChange change, CancellationToken cancellationToken)
    {
        var client = ClientId(session);
        var stop = change.PriceType is "SL" or "SL-M";
        var body = new JsonObject
        {
            ["dhanClientId"] = client,
            ["orderId"] = change.OrderId,
            ["orderType"] = DhanRules.OrderType(change.PriceType),
            ["quantity"] = change.Quantity,
            ["validity"] = "DAY",
        };
        if (Sandbox)
        {
            if (change.PriceType != "MARKET") body["price"] = (double)change.Price;
            if (change.DisclosedQuantity > 0) body["disclosedQuantity"] = change.DisclosedQuantity;
            if (stop && change.TriggerPrice > 0) body["triggerPrice"] = (double)change.TriggerPrice;
        }
        else
        {
            body["legName"] = "ENTRY_LEG";
            if (change.PriceType is "LIMIT" or "SL") body["price"] = (double)change.Price;
            if (change.DisclosedQuantity > 0) body["disclosedQuantity"] = change.DisclosedQuantity;
            if (stop)
            {
                if (change.TriggerPrice <= 0) return BrokerCallResult.Refused(500, NoTrigger);
                body["triggerPrice"] = (double)change.TriggerPrice;
            }

            if (change.PriceType == "SL-M")
            {
                var instrument = await symbols.FindAsync(Name, change.Symbol, change.Exchange, cancellationToken);
                if (DhanRules.ProtectedLimit(change.Symbol, change.Action, (double)change.TriggerPrice, instrument?.TickSize ?? 0) is not { } limit)
                    return BrokerCallResult.Refused(500, NoTick(change.Symbol, change.Exchange));
                body["orderType"] = "STOP_LOSS";
                body["price"] = limit;
            }
        }

        using var request = Request(session, HttpMethod.Put, $"/v2/orders/{Uri.EscapeDataString(change.OrderId)}", body, clientHeader: Sandbox && client is not null);
        using var response = await SendAsync(request, cancellationToken);
        var reply = await ReadAsync(response, cancellationToken) as JsonObject;
        if (response.IsSuccessStatusCode && Str(reply?["orderId"]) is { Length: > 0 } id) return BrokerCallResult.Success(id);
        return BrokerCallResult.Refused(response.IsSuccessStatusCode ? 500 : (int)response.StatusCode, Reason(reply, "Failed to modify order"));
    }

    public async Task<BrokerCallResult> CancelAsync(BrokerSession session, string orderId, CancellationToken cancellationToken)
    {
        using var request = Request(session, HttpMethod.Delete, $"/v2/orders/{Uri.EscapeDataString(orderId)}", content: true, clientHeader: Sandbox && ClientId(session) is not null);
        using var response = await SendAsync(request, cancellationToken);
        var reply = await ReadAsync(response, cancellationToken) as JsonObject;
        // OpenAlgo reports any reply as cancelled, even Dhan's refusal; a refused cancel is reported here.
        if (response.IsSuccessStatusCode && Str(reply?["errorType"]) is null) return BrokerCallResult.Success(orderId);
        return BrokerCallResult.Refused(response.IsSuccessStatusCode ? 500 : (int)response.StatusCode, Reason(reply, "Failed to cancel order"));
    }

    // ---- books -----------------------------------------------------------------------------------------------

    /// <summary>map_order_data: OpenAlgo symbol and exchange; product renamed only when the symbol is known.</summary>
    private async Task<(string Symbol, string Exchange, string Product)> MapRowAsync(JsonObject row, CancellationToken cancellationToken)
    {
        var exchange = DhanRules.Exchange(Str(row["exchangeSegment"]) ?? "");
        var product = Str(row["productType"]) ?? "";
        var token = Str(row["securityId"]) ?? "";
        var record = token.Length == 0 ? null : await symbols.FindByTokenAsync(Name, token, exchange, cancellationToken);
        if (record is not null) return (record.Symbol, exchange, DhanRules.BookProduct(exchange, product));
        var fallback = Str(row["tradingSymbol"]);
        return (Sandbox ? fallback ?? "" : string.IsNullOrEmpty(fallback) ? token : fallback, exchange, product);
    }

    private string MapStatus(string status) => status switch
    {
        "TRADED" => "complete",
        "PENDING" => "open",
        "REJECTED" => "rejected",
        "CANCELLED" => "cancelled",
        "TRANSIT" or "PART_TRADED" when Sandbox => "open",
        "EXPIRED" when Sandbox => "cancelled",
        _ => status,
    };

    /// <summary>The sandbox reports updateTime in UTC with no zone; the live API in IST.</summary>
    private JsonNode? Time(JsonNode? value)
    {
        if (!Sandbox || Str(value) is not { Length: > 0 } text) return value?.DeepClone() ?? "";
        return DateTime.TryParseExact(text, "yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture, DateTimeStyles.None, out var utc)
            ? utc.Add(Ist.Offset).ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)
            : value!.DeepClone();
    }

    public async Task<BrokerBook> GetOrderBookAsync(BrokerSession session, CancellationToken cancellationToken)
    {
        var rows = await GetListAsync(session, "/v2/orders", cancellationToken);
        int buy = 0, sell = 0, completed = 0, open = 0, rejected = 0;
        var orders = new JsonArray();
        foreach (var o in rows.OfType<JsonObject>())
        {
            var (symbol, exchange, product) = await MapRowAsync(o, cancellationToken);
            var side = Str(o["transactionType"]);
            if (side == "BUY") buy++;
            else if (side == "SELL") sell++;
            var status = MapStatus(Str(o["orderStatus"]) ?? "");
            if (status == "complete") completed++;
            else if (status == "open") open++;
            else if (status == "rejected") rejected++;
            orders.Add(new JsonObject
            {
                ["symbol"] = symbol, ["exchange"] = exchange, ["action"] = Get(o, "transactionType", ""),
                ["quantity"] = Get(o, "quantity", 0), ["price"] = Get(o, "price", 0.0), ["trigger_price"] = Get(o, "triggerPrice", 0.0),
                ["pricetype"] = DhanRules.PriceType(Str(o["orderType"]) ?? ""), ["product"] = product,
                ["orderid"] = Get(o, "orderId", ""), ["order_status"] = status, ["timestamp"] = Time(o["updateTime"]),
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
        var rows = await GetListAsync(session, "/v2/trades", cancellationToken);
        var trades = new JsonArray();
        foreach (var t in rows.OfType<JsonObject>())
        {
            var (symbol, exchange, product) = await MapRowAsync(t, cancellationToken);
            trades.Add(new JsonObject
            {
                ["symbol"] = symbol, ["exchange"] = exchange, ["product"] = product, ["action"] = Get(t, "transactionType", ""),
                ["quantity"] = Get(t, "tradedQuantity", 0), ["average_price"] = Get(t, "tradedPrice", 0.0),
                ["trade_value"] = Float(t["tradedQuantity"]) * Float(t["tradedPrice"]),
                ["orderid"] = Get(t, "orderId", ""), ["timestamp"] = Time(t["updateTime"]),
            });
        }

        return trades;
    }

    public async Task<JsonArray> GetPositionsAsync(BrokerSession session, CancellationToken cancellationToken)
    {
        var rows = await GetListAsync(session, "/v2/positions", cancellationToken);
        var mapped = new List<(JsonObject Row, string Symbol, string Exchange, string Product)>();
        foreach (var p in rows.OfType<JsonObject>())
        {
            var (symbol, exchange, product) = await MapRowAsync(p, cancellationToken);
            mapped.Add((p, symbol, exchange, product));
        }

        var ltps = Sandbox
            ? new Dictionary<string, double>()
            : await LtpsAsync(session, mapped.Where(m => m.Symbol.Length > 0 && m.Exchange.Length > 0).Select(m => (m.Symbol, m.Exchange)), cancellationToken);

        var positions = new JsonArray();
        foreach (var (p, symbol, exchange, product) in mapped)
        {
            double ltp;
            if (Sandbox)
            {
                // The sandbox carries a price on the row; failing that, the cost price.
                ltp = new[] { "ltp", "lastTradedPrice", "lastPrice", "closePrice" }.Select(k => Float(p[k])).FirstOrDefault(v => v > 0);
                if (ltp <= 0) ltp = Float(p["costPrice"]);
            }
            else
            {
                ltp = ltps.GetValueOrDefault($"{exchange}:{symbol}");
            }

            positions.Add(new JsonObject
            {
                ["symbol"] = symbol, ["exchange"] = exchange, ["product"] = product,
                ["quantity"] = Get(p, "netQty", 0), ["average_price"] = Get(p, "costPrice", 0.0),
                ["ltp"] = BookFormatting.Round2(ltp),
                ["pnl"] = BookFormatting.Round2(Float(p["realizedProfit"]) + Float(p["unrealizedProfit"])),
            });
        }

        return positions;
    }

    public async Task<BrokerBook> GetHoldingsAsync(BrokerSession session, CancellationToken cancellationToken)
    {
        using var request = Request(session, HttpMethod.Get, "/v2/holdings", clientHeader: Sandbox && ClientId(session) is not null);
        using var response = await SendAsync(request, cancellationToken);
        var reply = await ReadAsync(response, cancellationToken);
        if (reply is JsonObject error)
        {
            // Dhan answers an empty demat account with an error; that is an empty book, not a failure.
            if (Str(error["errorCode"]) == "DHOLDING_ERROR" || Str(error["internalErrorCode"]) == "DH-1111" || Str(error["internalErrorMessage"]) == "No holdings available")
                return EmptyHoldings();
            throw new BrokerException($"{DisplayName} could not answer: {Reason(error, "try again in a moment.")}");
        }

        if (reply is not JsonArray rows) return EmptyHoldings();
        var holdings = new JsonArray();
        if (Sandbox)
        {
            double value = 0;
            foreach (var h in rows.OfType<JsonObject>())
            {
                value += Float(h["avgCostPrice"]) * Float(h["totalQty"]);
                holdings.Add(new JsonObject
                {
                    ["symbol"] = Get(h, "tradingSymbol", ""), ["exchange"] = Get(h, "exchange", ""), ["quantity"] = Get(h, "totalQty", 0),
                    ["product"] = "CNC", ["pnl"] = 0.0, ["pnlpercent"] = 0.0,
                });
            }

            return new BrokerBook(holdings, new JsonObject
            {
                ["totalholdingvalue"] = value, ["totalinvvalue"] = value, ["totalprofitandloss"] = 0, ["totalpnlpercentage"] = 0,
            });
        }

        // Dhan reports every holding on exchange ALL; the listing exchange is found from the security id (NSE, then BSE).
        var resolved = new List<(JsonObject Row, string Symbol, string Exchange)>();
        foreach (var h in rows.OfType<JsonObject>())
        {
            var token = Str(h["securityId"]) ?? "";
            string? symbol = null, exchange = null;
            foreach (var candidate in new[] { "NSE", "BSE" })
            {
                if (token.Length == 0) break;
                if (await symbols.FindByTokenAsync(Name, token, candidate, cancellationToken) is { } record)
                {
                    (symbol, exchange) = (record.Symbol, candidate);
                    break;
                }
            }

            resolved.Add((h, symbol ?? Str(h["tradingSymbol"]) ?? "", exchange ?? "NSE"));
        }

        var ltps = await LtpsAsync(session, resolved.Where(r => r.Symbol.Length > 0).Select(r => (r.Symbol, r.Exchange)), cancellationToken);
        double invested = 0, held = 0;
        foreach (var (h, symbol, exchange) in resolved)
        {
            var qty = Int(h["totalQty"]);
            var avg = Float(h["avgCostPrice"]);
            var ltp = ltps.GetValueOrDefault($"{exchange}:{symbol}");
            invested += avg * qty;
            held += (ltp != 0 ? ltp : avg) * qty;
            var priced = ltp > 0 && avg > 0;
            holdings.Add(new JsonObject
            {
                ["symbol"] = symbol, ["exchange"] = exchange, ["quantity"] = qty, ["product"] = "CNC",
                ["average_price"] = BookFormatting.Round2(avg), ["ltp"] = BookFormatting.Round2(ltp),
                ["pnl"] = priced ? BookFormatting.Round2((ltp - avg) * qty) : 0.0,
                ["pnlpercent"] = priced ? BookFormatting.Round2((ltp - avg) / avg * 100) : 0.0,
            });
        }

        var pnl = held - invested;
        return new BrokerBook(holdings, new JsonObject
        {
            ["totalholdingvalue"] = held, ["totalinvvalue"] = invested, ["totalprofitandloss"] = pnl,
            ["totalpnlpercentage"] = invested != 0 ? pnl / invested * 100 : 0,
        });
    }

    private static BrokerBook EmptyHoldings() => new([], new JsonObject
    {
        ["totalholdingvalue"] = 0, ["totalinvvalue"] = 0, ["totalprofitandloss"] = 0, ["totalpnlpercentage"] = 0,
    });

    // ---- funds -----------------------------------------------------------------------------------------------

    public async Task<JsonObject> GetFundsAsync(BrokerSession session, CancellationToken cancellationToken)
    {
        using var request = Request(session, HttpMethod.Get, "/v2/fundlimit", clientHeader: Sandbox && ClientId(session) is not null);
        using var response = await SendAsync(request, cancellationToken);
        var funds = await ReadAsync(response, cancellationToken) as JsonObject;
        // OpenAlgo shows zero cash when Dhan refuses; a refusal is reported here so zero is never mistaken for a balance.
        if (funds is null || !response.IsSuccessStatusCode || Str(funds["errorType"]) is not null || Str(funds["status"]) is "error" or "failed")
        {
            if (Str(funds?["errorType"]) == "Invalid_Authentication" || response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                throw new BrokerException($"{DisplayName} says the access token is invalid or has expired. Log in to {DisplayName} again.", 403);
            throw new BrokerException($"{DisplayName} could not report funds: {Reason(funds, "try again in a moment.")}");
        }

        double realised = 0, unrealised = 0;
        using (var positions = Request(session, HttpMethod.Get, "/v2/positions", clientHeader: Sandbox && ClientId(session) is not null))
        using (var reply = await SendAsync(positions, cancellationToken))
        {
            if (await ReadAsync(reply, cancellationToken) is JsonArray rows)
            {
                foreach (var p in rows.OfType<JsonObject>())
                {
                    realised += Float(p["realizedProfit"]);
                    unrealised += Float(p["unrealizedProfit"]);
                }
            }
        }

        return new JsonObject
        {
            ["availablecash"] = BookFormatting.Fixed2(Float(funds["availabelBalance"]) - Float(funds["collateralAmount"])),
            ["collateral"] = BookFormatting.Fixed2(Float(funds["collateralAmount"])),
            ["m2munrealized"] = BookFormatting.Fixed2(unrealised),
            ["m2mrealized"] = BookFormatting.Fixed2(realised),
            ["utiliseddebits"] = BookFormatting.Fixed2(Float(funds["utilizedAmount"])),
        };
    }

    // ---- market data -----------------------------------------------------------------------------------------

    public async Task<JsonObject> GetQuoteAsync(BrokerSession session, string symbol, string exchange, CancellationToken cancellationToken)
    {
        if (Sandbox)
        {
            var c = await CandleQuoteAsync(session, symbol, exchange, cancellationToken);
            return new JsonObject
            {
                ["ltp"] = c.Ltp, ["open"] = c.Open, ["high"] = c.High, ["low"] = c.Low, ["volume"] = c.Volume,
                ["oi"] = 0, ["bid"] = 0, ["ask"] = 0, ["prev_close"] = 0,
            };
        }

        var q = await FeedQuoteAsync(session, symbol, exchange, cancellationToken);
        var ohlc = q["ohlc"];
        var buy = q["depth"]?["buy"] as JsonArray;
        var sell = q["depth"]?["sell"] as JsonArray;
        return new JsonObject
        {
            ["ltp"] = LastPrice(q), ["open"] = Float(ohlc?["open"]), ["high"] = Float(ohlc?["high"]), ["low"] = Float(ohlc?["low"]),
            ["volume"] = Int(q["volume"]), ["oi"] = Int(Truthy(q["oi"]) ? q["oi"] : q["open_interest"]),
            ["bid"] = buy is { Count: > 0 } ? Float(buy[0]?["price"]) : 0, ["ask"] = sell is { Count: > 0 } ? Float(sell[0]?["price"]) : 0,
            ["prev_close"] = Float(ohlc?["close"]),
        };
    }

    public async Task<JsonObject> GetDepthAsync(BrokerSession session, string symbol, string exchange, CancellationToken cancellationToken)
    {
        static JsonArray Empty() => new(Enumerable.Range(0, 5).Select(_ => (JsonNode?)new JsonObject { ["price"] = 0, ["quantity"] = 0 }).ToArray());
        if (Sandbox)
        {
            // The sandbox has no order book; only the candle-derived prices are real.
            var c = await CandleQuoteAsync(session, symbol, exchange, cancellationToken);
            return new JsonObject
            {
                ["bids"] = Empty(), ["asks"] = Empty(), ["high"] = c.High, ["low"] = c.Low, ["ltp"] = c.Ltp, ["ltq"] = 0,
                ["open"] = c.Open, ["prev_close"] = 0, ["oi"] = 0, ["totalbuyqty"] = 0, ["totalsellqty"] = 0, ["volume"] = c.Volume,
            };
        }

        var q = await FeedQuoteAsync(session, symbol, exchange, cancellationToken);
        static (JsonArray Levels, long Total) Side(JsonArray? side)
        {
            var levels = new JsonArray();
            long total = 0;
            for (var i = 0; i < 5; i++)
            {
                if (side is not null && i < side.Count)
                {
                    var qty = Int(side[i]?["quantity"]);
                    total += qty;
                    levels.Add(new JsonObject { ["price"] = Float(side[i]?["price"]), ["quantity"] = qty });
                }
                else
                {
                    levels.Add(new JsonObject { ["price"] = 0, ["quantity"] = 0 });
                }
            }

            return (levels, total);
        }

        var (bids, buyTotal) = Side(q["depth"]?["buy"] as JsonArray);
        var (asks, sellTotal) = Side(q["depth"]?["sell"] as JsonArray);
        var ohlc = q["ohlc"];
        return new JsonObject
        {
            ["bids"] = bids, ["asks"] = asks, ["ltp"] = Float(q["last_price"]), ["ltq"] = Int(q["last_quantity"]), ["volume"] = Int(q["volume"]),
            ["open"] = Float(ohlc?["open"]), ["high"] = Float(ohlc?["high"]), ["low"] = Float(ohlc?["low"]), ["prev_close"] = Float(ohlc?["close"]),
            ["oi"] = Int(q["oi"]), ["totalbuyqty"] = buyTotal, ["totalsellqty"] = sellTotal,
        };
    }

    /// <summary>Dhan answers up to 1000 instruments per market-feed call; OpenAlgo waits a second between calls.</summary>
    private const int QuoteBatch = 1000;

    public async Task<JsonArray> GetMultiQuotesAsync(BrokerSession session, IReadOnlyList<SymbolRecord> instruments, CancellationToken cancellationToken)
    {
        var results = new JsonArray();
        if (Sandbox)
        {
            // The sandbox has no batch quote, so each symbol is asked for in turn, 150 ms apart as in OpenAlgo.
            for (var i = 0; i < instruments.Count; i++)
            {
                if (i > 0) await Task.Delay(TimeSpan.FromMilliseconds(150), cancellationToken);
                var instrument = instruments[i];
                try
                {
                    var q = await GetQuoteAsync(session, instrument.Symbol, instrument.Exchange, cancellationToken);
                    results.Add(new JsonObject
                    {
                        ["symbol"] = instrument.Symbol, ["exchange"] = instrument.Exchange,
                        ["data"] = new JsonObject
                        {
                            ["bid"] = q["bid"]?.DeepClone(), ["ask"] = q["ask"]?.DeepClone(), ["open"] = q["open"]?.DeepClone(), ["high"] = q["high"]?.DeepClone(),
                            ["low"] = q["low"]?.DeepClone(), ["ltp"] = q["ltp"]?.DeepClone(), ["prev_close"] = q["prev_close"]?.DeepClone(),
                            ["volume"] = q["volume"]?.DeepClone(), ["oi"] = q["oi"]?.DeepClone(),
                        },
                    });
                }
                catch (BrokerException ex)
                {
                    // OpenAlgo leaves a symbol it could not price out of the answer; here it is named with the reason.
                    results.Add(new JsonObject { ["symbol"] = instrument.Symbol, ["exchange"] = instrument.Exchange, ["error"] = ex.Message });
                }
            }

            return results;
        }

        for (var start = 0; start < instruments.Count; start += QuoteBatch)
        {
            if (start > 0) await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
            var request = new JsonObject();
            var wanted = new Dictionary<string, SymbolRecord>();
            foreach (var instrument in instruments.Skip(start).Take(QuoteBatch))
            {
                if (DhanRules.DataSegment(instrument.Exchange) is not { } segment)
                {
                    results.Add(new JsonObject { ["symbol"] = instrument.Symbol, ["exchange"] = instrument.Exchange, ["error"] = $"{DisplayName} has no market data for {instrument.Exchange}." });
                    continue;
                }

                if (request[segment] is not JsonArray ids) request[segment] = ids = [];
                ids.Add(long.Parse(instrument.Token, CultureInfo.InvariantCulture));
                wanted.TryAdd($"{segment}:{instrument.Token}", instrument);
            }

            if (request.Count == 0) continue;
            var data = await MarketFeedAsync(session, request, cancellationToken);
            foreach (var (key, instrument) in wanted)
            {
                var parts = key.Split(':');
                if (data[parts[0]]?[parts[1]] is not JsonObject { Count: > 0 } q)
                {
                    results.Add(new JsonObject { ["symbol"] = instrument.Symbol, ["exchange"] = instrument.Exchange, ["error"] = "No quote data available" });
                    continue;
                }

                var ohlc = q["ohlc"];
                var buy = q["depth"]?["buy"] as JsonArray;
                var sell = q["depth"]?["sell"] as JsonArray;
                results.Add(new JsonObject
                {
                    ["symbol"] = instrument.Symbol, ["exchange"] = instrument.Exchange,
                    ["data"] = new JsonObject
                    {
                        ["bid"] = buy is { Count: > 0 } ? Float(buy[0]?["price"]) : 0, ["ask"] = sell is { Count: > 0 } ? Float(sell[0]?["price"]) : 0,
                        ["open"] = Float(ohlc?["open"]), ["high"] = Float(ohlc?["high"]), ["low"] = Float(ohlc?["low"]), ["ltp"] = LastPrice(q),
                        ["prev_close"] = Float(ohlc?["close"]), ["volume"] = Int(q["volume"]), ["oi"] = Int(Truthy(q["oi"]) ? q["oi"] : q["open_interest"]),
                    },
                });
            }
        }

        return results;
    }

    private static double LastPrice(JsonObject quote) => Float(Truthy(quote["last_price"]) ? quote["last_price"] : quote["lastPrice"]);

    private async Task<JsonObject> FeedQuoteAsync(BrokerSession session, string symbol, string exchange, CancellationToken cancellationToken)
    {
        var instrument = await symbols.FindAsync(Name, symbol, exchange, cancellationToken)
            ?? throw new BrokerException($"Error fetching quotes: {symbol} on {exchange} is not in the {DisplayName} master contract.");
        var segment = DhanRules.DataSegment(exchange) ?? throw new BrokerException($"Error fetching quotes: {DisplayName} has no market data for {exchange}.");
        var data = await MarketFeedAsync(session, new JsonObject { [segment] = new JsonArray(long.Parse(instrument.Token, CultureInfo.InvariantCulture)) }, cancellationToken);
        return data[segment]?[instrument.Token] as JsonObject is { Count: > 0 } quote
            ? quote
            : throw new BrokerException($"{DisplayName} has no quote for {symbol} right now.");
    }

    /// <summary>POST /v2/marketfeed/quote. A failed reply names its reason (OpenAlgo's error_mapping).</summary>
    private async Task<JsonObject> MarketFeedAsync(BrokerSession session, JsonObject request, CancellationToken cancellationToken)
    {
        using var message = Request(session, HttpMethod.Post, "/v2/marketfeed/quote", request, clientHeader: true, dataClientId: true);
        using var response = await SendAsync(message, cancellationToken);
        var reply = await ReadAsync(response, cancellationToken) as JsonObject ?? throw new BrokerException($"{DisplayName} sent a reply EP Trade bot could not read.");
        if (Str(reply["status"]) == "failed")
        {
            var code = (reply["data"] as JsonObject)?.Select(kv => kv.Key).FirstOrDefault() ?? "unknown";
            throw new BrokerException(code switch
            {
                "805" => $"{DisplayName} is limiting requests. Wait a moment, then try again.",
                "806" or "820" or "821" => $"{DisplayName} says market data is not subscribed for this account. Subscribe to {DisplayName}'s data APIs, then try again.",
                "810" => $"{DisplayName} does not recognise the client id. Check the client id on the account.",
                "401" => $"{DisplayName} says the access token is invalid or has expired. Log in to {DisplayName} again.",
                _ => $"{DisplayName} could not send market data (code {code}).",
            });
        }

        return reply["data"] as JsonObject ?? [];
    }

    /// <summary>Last prices by "EXCHANGE:SYMBOL" for books, in one market-feed call. Unreadable prices are 0, as in OpenAlgo.</summary>
    private async Task<Dictionary<string, double>> LtpsAsync(BrokerSession session, IEnumerable<(string Symbol, string Exchange)> wanted, CancellationToken cancellationToken)
    {
        var request = new JsonObject();
        var keys = new Dictionary<string, string>();
        foreach (var (symbol, exchange) in wanted)
        {
            if (DhanRules.DataSegment(exchange) is not { } segment) continue;
            if (await symbols.FindAsync(Name, symbol, exchange, cancellationToken) is not { } record) continue;
            if (request[segment] is not JsonArray ids) request[segment] = ids = [];
            ids.Add(long.Parse(record.Token, CultureInfo.InvariantCulture));
            keys[$"{segment}:{record.Token}"] = $"{exchange}:{symbol}";
        }

        var ltps = new Dictionary<string, double>();
        if (request.Count == 0) return ltps;
        try
        {
            var data = await MarketFeedAsync(session, request, cancellationToken);
            foreach (var (key, name) in keys)
            {
                var parts = key.Split(':');
                if (data[parts[0]]?[parts[1]] is JsonObject quote && quote.Count > 0) ltps[name] = LastPrice(quote);
            }
        }
        catch (Exception ex) when (ex is BrokerException or HttpRequestException or FormatException)
        {
            logger.LogWarning(ex, "Could not price {Broker} positions.", DisplayName);
        }

        return ltps;
    }

    private sealed record Candles(double Ltp, double Open, double High, double Low, long Volume);

    /// <summary>The sandbox's quote: today's one-minute candles. No candles is an error, never an invented price.</summary>
    private async Task<Candles> CandleQuoteAsync(BrokerSession session, string symbol, string exchange, CancellationToken cancellationToken)
    {
        var instrument = await symbols.FindAsync(Name, symbol, exchange, cancellationToken)
            ?? throw new BrokerException($"Error fetching quotes: {symbol} on {exchange} is not in the {DisplayName} master contract.");
        var segment = DhanRules.DataSegment(exchange) ?? throw new BrokerException($"Error fetching quotes: {DisplayName} has no market data for {exchange}.");
        var today = Ist.FromUtc(DateTime.UtcNow).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var body = new JsonObject
        {
            ["securityId"] = instrument.Token, ["exchangeSegment"] = segment, ["instrument"] = DhanRules.ChartInstrument(exchange, symbol),
            ["interval"] = "1", ["fromDate"] = today, ["toDate"] = today,
        };
        using var message = Request(session, HttpMethod.Post, "/v2/charts/intraday", body, clientHeader: true, dataClientId: true);
        using var response = await SendAsync(message, cancellationToken);
        var reply = await ReadAsync(response, cancellationToken) as JsonObject;
        var closes = reply?["close"] as JsonArray;
        if (!response.IsSuccessStatusCode || closes is not { Count: > 0 })
            throw new BrokerException($"{DisplayName} has no price for {symbol} today. The sandbox only has prices while its market is open.");

        var highs = (reply!["high"] as JsonArray ?? []).Where(Truthy).Select(n => Float(n)).ToList();
        var lows = (reply["low"] as JsonArray ?? []).Where(Truthy).Select(n => Float(n)).Where(v => v > 0).ToList();
        var opens = reply["open"] as JsonArray;
        return new Candles(
            Truthy(closes[^1]) ? Float(closes[^1]) : 0,
            opens is { Count: > 0 } && Truthy(opens[0]) ? Float(opens[0]) : 0,
            highs.Count > 0 ? highs.Max() : 0,
            lows.Count > 0 ? lows.Min() : 0,
            (reply["volume"] as JsonArray ?? []).Where(Truthy).Sum(n => Int(n)));
    }

    // ---- history ---------------------------------------------------------------------------------------------

    // timeframe_map of broker/dhan/api/data.py (the sandbox's is the same): Dhan's minutes, "D" for daily.
    private static readonly Dictionary<string, string> Resolutions = new() { ["1m"] = "1", ["5m"] = "5", ["15m"] = "15", ["25m"] = "25", ["1h"] = "60", ["D"] = "D" };

    public IReadOnlyList<string> HistoryTimeframes { get; } = [.. Resolutions.Keys];

    // Index series share the session of their cash exchange; GIFT NIFTY trades its own hours and is left alone.
    private static string SessionExchange(string exchange) => exchange switch { "NSE_INDEX" => "NSE", "BSE_INDEX" => "BSE", "MCX_INDEX" => "MCX", _ => exchange };

    private static bool Weekend(DateOnly d) => d.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday;

    public async Task<JsonArray> GetHistoryAsync(BrokerSession session, SymbolRecord instrument, string interval, DateOnly start, DateOnly end, CancellationToken cancellationToken)
    {
        if (!Resolutions.TryGetValue(interval, out var resolution))
            throw new BrokerException($"{DisplayName} does not offer {interval} candles. Choose one of: {string.Join(", ", Resolutions.Keys)}.", 400);
        var segment = DhanRules.DataSegment(instrument.Exchange) ?? throw new BrokerException($"{DisplayName} has no candles for {instrument.Exchange}.", 400);
        var chartInstrument = DhanRules.ChartInstrument(instrument.Exchange, instrument.Symbol);

        // The window moves to trading days: a start on a weekend to the Monday after, an end on one to the Friday before.
        while (Weekend(start)) start = start.AddDays(1);
        while (Weekend(end)) end = end.AddDays(-1);
        // One day is asked as that day to the next.
        if (start == end) end = end.AddDays(1);

        var today = DateOnly.FromDateTime(Ist.FromUtc(clock.GetUtcNow().UtcDateTime));
        var candles = new SortedDictionary<long, JsonObject>();
        void Add(JsonObject reply, bool daily)
        {
            var times = reply["timestamp"] as JsonArray ?? [];
            for (var i = 0; i < times.Count; i++)
            {
                double At(string field) => reply[field] is JsonArray a && i < a.Count && Truthy(a[i]) ? Float(a[i]) : 0;
                var stamp = DhanStamp((long)Float(times[i]), daily);
                // Two candles with one timestamp: the first one received is kept.
                candles.TryAdd(stamp, new JsonObject
                {
                    ["timestamp"] = stamp, ["open"] = At("open"), ["high"] = At("high"), ["low"] = At("low"), ["close"] = At("close"),
                    ["volume"] = (long)At("volume"), ["oi"] = (long)At("open_interest"),
                });
            }
        }

        JsonObject Body(string from, string to, bool intraday)
        {
            var body = new JsonObject { ["securityId"] = instrument.Token, ["exchangeSegment"] = segment, ["instrument"] = chartInstrument };
            if (intraday) body["interval"] = resolution;
            body["fromDate"] = from;
            body["toDate"] = to;
            body["oi"] = true;
            // Dhan itself sends the expiry code on every call. The sandbox sends it on a daily request for equity only, and never on an intraday one.
            if (intraday ? !Sandbox : !Sandbox || chartInstrument == "EQUITY") body["expiryCode"] = 0;
            return body;
        }

        string Day(DateOnly d) => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        if (interval == "D")
        {
            // The end is made inclusive by asking to the day after it.
            Add(await ChartsAsync(session, "/v2/charts/historical", Body(Day(start), Day(end.AddDays(1)), false), cancellationToken), daily: true);
        }
        else if (start == end.AddDays(-1))
        {
            Add(await ChartsAsync(session, "/v2/charts/intraday", Body(Day(start), Day(end), true), cancellationToken), daily: false);
        }
        else
        {
            var fetched = new List<(DateOnly From, DateOnly To, int Count)>();
            // Dhan answers 90 days of intraday candles a request; its sandbox, as OpenAlgo asks it, 5.
            var span = Sandbox ? 5 : 90;
            for (var from = start; from < end;)
            {
                var to = from.AddDays(span) < end ? from.AddDays(span) : end;
                var chunkFrom = from;
                from = to;
                // A chunk is skipped when it holds no trading day (the sandbox: when neither of its ends is one).
                var trades = Sandbox ? !Weekend(chunkFrom) || !Weekend(to) : Enumerable.Range(0, to.DayNumber - chunkFrom.DayNumber + 1).Any(n => !Weekend(chunkFrom.AddDays(n)));
                if (!trades) continue;

                // Dhan tries a chunk three times; an empty answer is suspect and tried again too. The sandbox asks once.
                JsonObject? reply = null;
                for (var attempt = 0; ; attempt++)
                {
                    try
                    {
                        reply = await ChartsAsync(session, "/v2/charts/intraday", Body(Day(chunkFrom), Day(to), true), cancellationToken);
                        if (!Sandbox && (reply["timestamp"] as JsonArray ?? []).Count == 0 && attempt < 2)
                        {
                            await Task.Delay(sessions.RetryBackoff(Name, TimeSpan.FromSeconds(2 * (1 << attempt))), cancellationToken);
                            continue;
                        }

                        break;
                    }
                    catch (BrokerException ex) when (!Sandbox && attempt < 2 && ex.HttpStatus != 403)
                    {
                        await Task.Delay(sessions.RetryBackoff(Name, TimeSpan.FromSeconds(2 * (1 << attempt))), cancellationToken);
                    }
                }

                Add(reply, daily: false);
                fetched.Add((chunkFrom, to, (reply["timestamp"] as JsonArray ?? []).Count));
            }

            // An empty chunk between chunks that had candles is a bad answer, not a quiet market: a history with a hole is not returned.
            if (!Sandbox && fetched.FindIndex(f => f.Count > 0) is var first and >= 0)
            {
                var last = fetched.FindLastIndex(f => f.Count > 0);
                var holes = fetched.Where((f, i) => f.Count == 0 && i > first && i < last).ToList();
                if (holes.Count > 0)
                    throw new BrokerException($"Dhan sent no candles for {string.Join(", ", holes.Select(h => $"{Day(h.From)} to {Day(h.To)}"))} while the days around them had some, so no history is returned with a gap in it. Try again.");
            }
        }

        // A daily request that includes today has no candle for today until the close: it is built from the quote.
        if (interval == "D" && Day(start).CompareTo(Day(today)) <= 0 && Day(today).CompareTo(Day(end)) <= 0)
        {
            try
            {
                var q = await GetQuoteAsync(session, instrument.Symbol, instrument.Exchange, cancellationToken);
                if (Float(q["ltp"]) > 0)
                {
                    var stamp = (long)(new DateTimeOffset(today.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero).ToUnixTimeSeconds());
                    candles.TryAdd(stamp, new JsonObject
                    {
                        ["timestamp"] = stamp, ["open"] = Float(q["open"]), ["high"] = Float(q["high"]), ["low"] = Float(q["low"]), ["close"] = Float(q["ltp"]),
                        ["volume"] = (long)Float(q["volume"]), ["oi"] = (long)Float(q["oi"]),
                    });
                }
            }
            catch (BrokerException ex)
            {
                logger.LogWarning(ex, "Today's daily candle could not be built from the quote.");
            }
        }

        var rows = candles.Values.ToList();
        if (interval != "D" && !Sandbox && !(instrument.Exchange == "NSE_INDEX" && instrument.Symbol == "GIFTNIFTY"))
            rows = await WithoutBarsAfterCloseAsync(rows, SessionExchange(instrument.Exchange), cancellationToken);
        return new JsonArray(rows.Select(r => (JsonNode?)r).ToArray());
    }

    /// <summary>
    /// After the close Dhan appends one flat, zero-volume bar stamped at the request time to NSE and BSE index series.
    /// No session contains it, so a bar that starts at or after the close of its day is dropped. A day with no session in the calendar is left as it is.
    /// </summary>
    private async Task<List<JsonObject>> WithoutBarsAfterCloseAsync(List<JsonObject> rows, string exchange, CancellationToken cancellationToken)
    {
        var closes = new Dictionary<DateOnly, long?>();
        var kept = new List<JsonObject>();
        foreach (var row in rows)
        {
            var day = DateOnly.FromDateTime(Ist.FromUtc(DateTimeOffset.FromUnixTimeSeconds(row["timestamp"]!.GetValue<long>()).UtcDateTime));
            if (!closes.TryGetValue(day, out var close))
            {
                var ends = (await calendar.SessionsAsync(day, cancellationToken)).Where(s => s.Exchange == exchange && s.EndTime is not null).Select(s => s.EndTime!.Value).ToList();
                closes[day] = close = ends.Count > 0 ? ends.Max() : null;
            }

            if (close is null || row["timestamp"]!.GetValue<long>() * 1000 < close) kept.Add(row);
        }

        return kept;
    }

    /// <summary>
    /// Dhan's epoch seconds as OpenAlgo stamps them with its server in India: an intraday candle keeps its instant, and a daily
    /// candle is stamped at 00:00 UTC of its India date, like every other broker's daily candle.
    /// </summary>
    private static long DhanStamp(long epoch, bool daily)
    {
        if (!daily) return epoch;
        var istDate = DateOnly.FromDateTime(Ist.FromUtc(DateTimeOffset.FromUnixTimeSeconds(epoch).UtcDateTime));
        return new DateTimeOffset(istDate.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero).ToUnixTimeSeconds();
    }

    /// <summary>
    /// POST a /v2/charts endpoint. Dhan's rate-limit error (805) is tried again three times (2, 4 and 8 seconds); every other
    /// failed answer is an error with the reason OpenAlgo gives for it.
    /// </summary>
    private async Task<JsonObject> ChartsAsync(BrokerSession session, string path, JsonObject body, CancellationToken cancellationToken)
    {
        for (var attempt = 0; ; attempt++)
        {
            using var message = Request(session, HttpMethod.Post, path, body, clientHeader: true, dataClientId: true);
            using var response = await SendAsync(message, cancellationToken);
            var reply = await ReadAsync(response, cancellationToken) as JsonObject;
            if (Str(reply?["status"]) == "failed")
            {
                var data = reply!["data"] as JsonObject;
                var code = data?.FirstOrDefault().Key ?? "unknown";
                if (code == "805" && attempt < 3)
                {
                    await Task.Delay(sessions.RetryBackoff(Name, TimeSpan.FromSeconds(2 * (1 << attempt))), cancellationToken);
                    continue;
                }

                throw new BrokerException(code switch
                {
                    "805" => $"{DisplayName} is limiting requests. Wait a moment, then try again.",
                    "806" or "820" or "821" => $"{DisplayName} needs its market data service. Subscribe to it in your {DisplayName} account.",
                    "810" => $"{DisplayName} says the client id is wrong. Check the account's client id.",
                    "401" => $"{DisplayName} says the access token is invalid or has expired. Log in to {DisplayName} again.",
                    _ => $"{DisplayName} could not send the candles: {Str(data?[code]) ?? "no reason given."}",
                }, code is "401" or "810" ? 403 : code == "805" ? 429 : 500);
            }

            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                throw new BrokerException($"{DisplayName} says the access token is invalid or has expired. Log in to {DisplayName} again.", 403);
            return reply ?? throw new BrokerException($"{DisplayName} sent a reply EP Trade bot could not read. Try again in a moment.");
        }
    }

    // ---- margin ----------------------------------------------------------------------------------------------

    /// <summary>
    /// One position goes to Dhan's single-order calculator; two or more go to its multi-order calculator, which includes open positions
    /// and orders and takes the benefit of hedged legs. The sandbox calculator is a different flow, not ported yet.
    /// </summary>
    public async Task<JsonObject> CalculateMarginAsync(BrokerSession session, IReadOnlyList<MarginLeg> legs, CancellationToken cancellationToken)
    {
        if (Sandbox) throw new BrokerException("Margin calculation on Dhan Sandbox is not supported in EP Trade bot yet.", 501);
        var client = ClientId(session) ?? throw new BrokerException("Could not determine the Dhan client id. Set the client id on the account, then try again.", 400);
        var scripts = new List<JsonObject>();
        foreach (var l in legs)
        {
            if (DhanRules.Segment(l.Instrument.Exchange) is not { } segment) throw new BrokerException($"{DisplayName} has no margin calculation for {l.Instrument.Exchange}.", 400);
            var script = new JsonObject
            {
                ["dhanClientId"] = client, ["exchangeSegment"] = segment, ["transactionType"] = l.Action, ["quantity"] = l.Quantity,
                ["productType"] = l.Product switch { "CNC" => "CNC", "NRML" => "MARGIN", _ => "INTRADAY" }, ["securityId"] = l.Instrument.Token, ["price"] = (double)l.Price,
            };
            if (l.TriggerPrice > 0) script["triggerPrice"] = (double)l.TriggerPrice;
            scripts.Add(script);
        }

        var multi = scripts.Count > 1;
        var body = multi
            ? new JsonObject { ["dhanClientId"] = client, ["includePosition"] = true, ["includeOrder"] = true, ["scripList"] = new JsonArray(scripts.Select(x => (JsonNode?)x).ToArray()) }
            : scripts[0];
        using var request = Request(session, HttpMethod.Post, multi ? "/v2/margincalculator/multi" : "/v2/margincalculator", body, clientHeader: true);
        using var response = await SendAsync(request, cancellationToken);
        var reply = await ReadAsync(response, cancellationToken) as JsonObject
            ?? throw new BrokerException($"{DisplayName} sent a reply EP Trade bot could not read. Try again in a moment.", 502);
        var status = (Str(reply["status"]) ?? "").ToLowerInvariant();
        if (Truthy(reply["errorType"]) || status is "error" or "failed" or "failure")
        {
            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                throw new BrokerException($"{DisplayName} says the access token is invalid or has expired. Log in to {DisplayName} again.", 403);
            var reason = Str(reply["errorMessage"]) ?? Str(reply["message"]) ?? Str(reply["errors"]) ?? Str(reply["error"]) ?? (multi ? "Dhan margin API returned an error" : "Failed to calculate margin");
            throw new BrokerException($"{DisplayName} could not work out the margin: {reason}", response.IsSuccessStatusCode ? 400 : (int)response.StatusCode);
        }

        double Pick(params string[] names) => names.Select(n => reply[n]).FirstOrDefault(v => v is not null && Str(v) is { Length: > 0 }) is { } v ? Float(v) : 0;
        return multi
            ? new JsonObject { ["total_margin_required"] = Pick("total_margin", "totalMargin"), ["span_margin"] = Pick("span_margin", "spanMargin"), ["exposure_margin"] = Pick("exposure_margin", "exposureMargin", "exposure") }
            : new JsonObject { ["total_margin_required"] = Float(reply["totalMargin"]), ["span_margin"] = Float(reply["spanMargin"]), ["exposure_margin"] = Float(reply["exposureMargin"]) };
    }

    // ---- plumbing --------------------------------------------------------------------------------------------

    private string NoClientId => $"{DisplayName} needs the account's client id. Set the client id on the account, then try again.";
    private const string NoTrigger = "A stop-loss order needs a trigger price above zero.";
    private string UnansweredOrder => $"The order was sent to {DisplayName} but no reply came back, so it may have been placed. Check the order book on {DisplayName} before trying again.";

    private string UnknownSymbol(string symbol, string exchange) =>
        $"{symbol} on {exchange} is not in the {DisplayName} master contract. Check the symbol, or download the master contract again.";

    private string NoTick(string symbol, string exchange) =>
        $"The tick size of {symbol} on {exchange} is not in the {DisplayName} master contract, so a protective price for the stop-loss-market order cannot be set. Download the master contract again, or use a stop-loss limit order.";

    /// <summary>The client id: the account's, else the configured key.</summary>
    private static string? ClientId(BrokerSession session) =>
        !string.IsNullOrWhiteSpace(session.ClientId) ? session.ClientId : session.Key("ClientId");

    private static string Reason(JsonObject? reply, string fallback) =>
        Str(reply?["errorMessage"]) ?? Str(reply?["message"]) ?? fallback;

    private HttpClient Client() => http.CreateClient(Name);

    private HttpRequestMessage Request(BrokerSession session, HttpMethod method, string path, JsonObject? body = null, bool content = false,
        bool clientHeader = false, bool dataClientId = false)
    {
        var request = new HttpRequestMessage(method, sessions.BaseUrl(Name, DefaultBaseUrl) + path);
        request.Headers.TryAddWithoutValidation("access-token", session.AuthToken);
        var client = ClientId(session) ?? (dataClientId ? session.Key("ApiKey") : null);
        if (clientHeader && client is not null) request.Headers.TryAddWithoutValidation("client-id", client);
        request.Headers.TryAddWithoutValidation("Accept", "application/json");
        if (body is not null || content || method != HttpMethod.Get)
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

    private async Task<JsonArray> GetListAsync(BrokerSession session, string path, CancellationToken cancellationToken)
    {
        using var request = Request(session, HttpMethod.Get, path, clientHeader: Sandbox && ClientId(session) is not null);
        using var response = await SendAsync(request, cancellationToken);
        var reply = await ReadAsync(response, cancellationToken);
        if (reply is JsonArray rows) return rows;
        if (reply is null && response.IsSuccessStatusCode) return [];
        var error = reply as JsonObject;
        if (Str(error?["errorType"]) == "Invalid_Authentication" || response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            throw new BrokerException($"{DisplayName} says the access token is invalid or has expired. Log in to {DisplayName} again.", 403);
        throw new BrokerException($"{DisplayName} could not answer: {Reason(error, "try again in a moment.")}");
    }
}
