using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using EPTradeBot.Core.Brokers;
using EPTradeBot.Core.Brokers.Gtt;
using EPTradeBot.Core.Brokers.Zerodha;
using Microsoft.Extensions.Logging;

namespace EPTradeBot.Infrastructure.Brokers.Zerodha;

/// <summary>
/// Zerodha Kite Connect v3. Port of OpenAlgo's broker/zerodha (api/order_api.py,
/// api/funds.py, api/data.py quotes and depth, mapping/order_data.py,
/// mapping/transform_data.py). Request and response mapping is held to fixtures
/// recorded from the Python plugin (tools/contract-fixtures/zerodha.fixtures.json).
/// <para>
/// Not ported yet, and refused rather than guessed: MCX (Kite counts MCX in
/// contracts and OpenAlgo in units; the conversion in mcx_contract_size.py is not
/// ported, so an MCX order or an MCX row in a book is refused), history, GTT,
/// margin, login and streaming.
/// </para>
/// </summary>
[BrokerPlugin]
public sealed partial class ZerodhaPlugin(
    IHttpClientFactory httpClients,
    BrokerSessionFactory sessions,
    ISymbolMaster symbols,
    ILogger<ZerodhaPlugin> logger) : IBrokerOrders, IBrokerPortfolio, IBrokerFunds, IBrokerMultiQuotes, IBrokerHistory, IBrokerMargin, IBrokerGtt
{
    public const string BrokerName = "zerodha";
    private const string DefaultBaseUrl = "https://api.kite.trade";

    public string Name => BrokerName;
    public string DisplayName => "Zerodha";
    public BrokerCapability Capabilities => BrokerCapability.Orders | BrokerCapability.Portfolio | BrokerCapability.Funds | BrokerCapability.Quotes | BrokerCapability.Depth | BrokerCapability.History | BrokerCapability.Margin | BrokerCapability.Gtt;
    public IReadOnlyList<string> RequiredKeys { get; } = ["ApiKey", "ApiSecret"];

    private static readonly Dictionary<string, string> StatusMap = new(StringComparer.Ordinal)
    {
        ["COMPLETE"] = "complete", ["REJECTED"] = "rejected", ["CANCELLED"] = "cancelled", ["OPEN"] = "open",
        ["UPDATE"] = "open", ["TRIGGER PENDING"] = "trigger pending", ["VALIDATION PENDING"] = "open",
        ["PUT ORDER REQ RECEIVED"] = "open", ["OPEN PENDING"] = "open", ["MODIFY VALIDATION PENDING"] = "open",
        ["MODIFY PENDING"] = "open", ["CANCEL PENDING"] = "open", ["AMO REQ RECEIVED"] = "open",
    };

    /// <summary>map_order_status: one Kite status, no state carried between orders.</summary>
    public static string MapOrderStatus(string? status)
    {
        var raw = (status ?? "").Trim().ToUpperInvariant();
        return StatusMap.TryGetValue(raw, out var mapped) ? mapped : raw.Length == 0 ? "open" : raw.ToLowerInvariant();
    }

    // ---- orders ----------------------------------------------------------------------------------------------

    public async Task<BrokerCallResult> PlaceAsync(BrokerSession session, BrokerOrder order, CancellationToken cancellationToken)
    {
        var instrument = await symbols.FindAsync(BrokerName, order.Symbol, order.Exchange, cancellationToken);
        if (instrument is null) return BrokerCallResult.Refused(400, UnknownSymbol(order.Symbol, order.Exchange));
        int quantity, disclosed;
        try
        {
            quantity = KiteQuantity(order.Quantity, instrument, "Quantity");
            disclosed = KiteQuantity(order.DisclosedQuantity, instrument, "Disclosed quantity");
        }
        catch (McxQuantityException ex)
        {
            return BrokerCallResult.Refused(400, ex.Message);
        }


        var tag = "openalgo" + Convert.ToHexStringLower(System.Security.Cryptography.RandomNumberGenerator.GetBytes(6));
        var form = new List<KeyValuePair<string, string>>
        {
            new("tradingsymbol", instrument.BrokerSymbol),
            new("exchange", order.Exchange),
            new("transaction_type", order.Action.ToUpperInvariant()),
            new("order_type", order.PriceType),
            new("quantity", quantity.ToString(CultureInfo.InvariantCulture)),
            new("product", order.Product),
            new("price", Num(order.Price)),
            new("trigger_price", Num(order.TriggerPrice)),
            new("disclosed_quantity", disclosed.ToString(CultureInfo.InvariantCulture)),
            new("validity", "DAY"),
            new("market_protection", "-1"),
            new("tag", tag),
        };

        using var request = Request(session, HttpMethod.Post, "/orders/regular");
        request.Content = new FormUrlEncodedContent(form);
        HttpResponseMessage response;
        try
        {
            response = await Client().SendAsync(request, cancellationToken);
        }
        catch (HttpRequestException ex)
        {
            // The request may have reached Kite. Look for it by its tag before reporting.
            logger.LogWarning(ex, "Kite order POST got no answer; looking for tag {Tag} in the order book.", tag);
            var recovered = await RecoverByTagAsync(session, tag, cancellationToken);
            return recovered is not null
                ? BrokerCallResult.Success(recovered)
                : new BrokerCallResult(false, 502, UnansweredOrder, MayHaveBeenSent: true);
        }

        using (response)
        {
            var body = await ReadJsonAsync(response, cancellationToken);
            if (Str(body?["status"]) == "success" && Str(body?["data"]?["order_id"]) is { } id) return BrokerCallResult.Success(id);
            var status = (int)response.StatusCode;
            return BrokerCallResult.Refused(status == 200 ? 500 : status, Str(body?["message"]) ?? "Failed to place order");
        }
    }

    public async Task<BrokerCallResult> ModifyAsync(BrokerSession session, BrokerOrderChange change, CancellationToken cancellationToken)
    {
        var instrument = await symbols.FindAsync(BrokerName, change.Symbol, change.Exchange, cancellationToken);
        int quantity, disclosed;
        try
        {
            quantity = instrument is null ? change.Quantity : KiteQuantity(change.Quantity, instrument, "Quantity");
            disclosed = instrument is null ? change.DisclosedQuantity : KiteQuantity(change.DisclosedQuantity, instrument, "Disclosed quantity");
        }
        catch (McxQuantityException ex)
        {
            return BrokerCallResult.Refused(400, ex.Message);
        }

        var form = new List<KeyValuePair<string, string>>
        {
            new("order_type", change.PriceType is "MARKET" or "LIMIT" or "SL" or "SL-M" ? change.PriceType : "MARKET"),
            new("quantity", quantity.ToString(CultureInfo.InvariantCulture)),
            new("price", change.Price != 0 ? Num(change.Price) : "0"),
            new("disclosed_quantity", disclosed != 0 ? disclosed.ToString(CultureInfo.InvariantCulture) : "0"),
            new("validity", "DAY"),
        };
        if (change.TriggerPrice != 0) form.Add(new("trigger_price", Num(change.TriggerPrice)));

        using var request = Request(session, HttpMethod.Put, $"/orders/regular/{Uri.EscapeDataString(change.OrderId)}");
        request.Content = new FormUrlEncodedContent(form);
        using var response = await SendAsync(request, cancellationToken);
        var body = await ReadJsonAsync(response, cancellationToken);
        if ((Str(body?["status"]) == "success" || Str(body?["message"]) == "SUCCESS") && Str(body?["data"]?["order_id"]) is { } id)
            return BrokerCallResult.Success(id);
        return BrokerCallResult.Refused((int)response.StatusCode, Str(body?["message"]) ?? "Failed to modify order");
    }

    public async Task<BrokerCallResult> CancelAsync(BrokerSession session, string orderId, CancellationToken cancellationToken)
    {
        using var request = Request(session, HttpMethod.Delete, $"/orders/regular/{Uri.EscapeDataString(orderId)}");
        using var response = await SendAsync(request, cancellationToken);
        var body = await ReadJsonAsync(response, cancellationToken);
        if (!response.IsSuccessStatusCode)
            return BrokerCallResult.Refused(500, $"Failed to cancel order: {Str(body?["message"]) ?? "the broker refused the request"}");
        if (Truthy(body?["status"]) && Str(body?["data"]?["order_id"]) is { } id) return BrokerCallResult.Success(id);
        return BrokerCallResult.Refused((int)response.StatusCode, Str(body?["message"]) ?? "Failed to cancel order");
    }

    private async Task<string?> RecoverByTagAsync(BrokerSession session, string tag, CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow.AddSeconds(2);
        while (true)
        {
            try
            {
                var book = await GetAsync(session, "/orders", cancellationToken);
                if (Str(book["status"]) == "success" && book["data"] is JsonArray rows)
                {
                    foreach (var row in rows.OfType<JsonObject>())
                        if (Str(row["tag"]) == tag && Str(row["order_id"]) is { Length: > 0 } id) return id;
                }
            }
            catch (Exception ex) when (ex is BrokerException or HttpRequestException)
            {
                logger.LogWarning("Could not read Kite's order book while recovering tag {Tag}.", tag);
            }

            if (DateTime.UtcNow >= deadline) return null;
            await Task.Delay(250, cancellationToken);
        }
    }

    // ---- books -----------------------------------------------------------------------------------------------

    public async Task<BrokerBook> GetOrderBookAsync(BrokerSession session, CancellationToken cancellationToken)
    {
        var raw = await GetAsync(session, "/orders", cancellationToken);
        var rows = raw["data"] as JsonArray ?? [];
        int buy = 0, sell = 0, completed = 0, open = 0, rejected = 0;
        var orders = new JsonArray();
        foreach (var o in rows.OfType<JsonObject>())
        {
            var side = Str(o["transaction_type"]);
            if (side == "BUY") buy++;
            else if (side == "SELL") sell++;
            var status = MapOrderStatus(Str(o["status"]));
            if (status == "complete") completed++;
            else if (status is "open" or "trigger pending") open++;
            else if (status == "rejected") rejected++;

            orders.Add(new JsonObject
            {
                ["symbol"] = await OpenAlgoSymbolAsync(o, cancellationToken),
                ["exchange"] = Copy(o["exchange"], ""),
                ["action"] = Copy(o["transaction_type"], ""),
                ["quantity"] = await UnitsAsync(o, o["quantity"], cancellationToken) ?? 0,
                ["price"] = Copy(o["price"], 0.0),
                ["trigger_price"] = Copy(o["trigger_price"], 0.0),
                ["pricetype"] = Copy(o["order_type"], ""),
                ["product"] = Copy(o["product"], ""),
                ["orderid"] = Copy(o["order_id"], ""),
                // Trigger-pending stop orders are live and can be changed, so the book shows them open.
                ["order_status"] = status == "trigger pending" ? "open" : status,
                ["timestamp"] = Copy(o["order_timestamp"], ""),
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
        var raw = await GetAsync(session, "/trades", cancellationToken);
        var rows = raw["data"] as JsonArray ?? [];
        var trades = new JsonArray();
        foreach (var t in rows.OfType<JsonObject>())
        {
            var units = await UnitsAsync(t, t["quantity"], cancellationToken);
            var quantity = BookFormatting.Number(units) ?? 0;
            var average = BookFormatting.Number(t["average_price"]) ?? 0;
            // The rupee value of the trade: contracts times the quotation multiplier times the price (units times price off MCX).
            var tradeValue = Str(t["exchange"]) == "MCX" ? await McxTradeValueAsync(t, quantity, average, cancellationToken) : quantity * average;
            var fill = t["fill_timestamp"];
            trades.Add(new JsonObject
            {
                ["symbol"] = await OpenAlgoSymbolAsync(t, cancellationToken),
                ["exchange"] = Copy(t["exchange"], ""),
                ["product"] = Copy(t["product"], ""),
                ["action"] = Copy(t["transaction_type"], ""),
                ["quantity"] = units ?? 0,
                ["average_price"] = Copy(t["average_price"], 0.0),
                ["trade_value"] = tradeValue,
                ["orderid"] = Copy(t["order_id"], ""),
                ["tradeid"] = Copy(t["trade_id"], ""),
                // fill_timestamp is the trade's own time; order_timestamp is shared by every fill of an order.
                ["timestamp"] = Truthy(fill) ? fill!.DeepClone() : Copy(t["order_timestamp"], ""),
            });
        }

        return trades;
    }

    public async Task<JsonArray> GetPositionsAsync(BrokerSession session, CancellationToken cancellationToken)
    {
        var raw = await GetAsync(session, "/portfolio/positions", cancellationToken);
        var rows = raw["data"]?["net"] as JsonArray ?? [];
        var positions = new JsonArray();
        foreach (var p in rows.OfType<JsonObject>())
        {
            positions.Add(new JsonObject
            {
                ["symbol"] = await OpenAlgoSymbolAsync(p, cancellationToken),
                ["exchange"] = Copy(p["exchange"], ""),
                ["product"] = Copy(p["product"], ""),
                ["quantity"] = await UnitsAsync(p, p["quantity"], cancellationToken, strict: true) ?? "0",
                ["pnl"] = BookFormatting.Round2(BookFormatting.Number(p["pnl"]) ?? 0),
                ["average_price"] = BookFormatting.Fixed2(BookFormatting.Number(p["average_price"]) ?? 0),
                ["ltp"] = BookFormatting.Round2(BookFormatting.Number(p["last_price"]) ?? 0),
            });
        }

        return positions;
    }

    public async Task<BrokerBook> GetHoldingsAsync(BrokerSession session, CancellationToken cancellationToken)
    {
        var raw = await GetAsync(session, "/portfolio/holdings", cancellationToken);
        var rows = raw["data"] as JsonArray ?? [];
        double holdingValue = 0, investedValue = 0, pnl = 0;
        var holdings = new JsonArray();
        foreach (var h in rows.OfType<JsonObject>())
        {
            var quantity = (long)Math.Truncate(Float(h["quantity"]));
            var average = Float(h["average_price"]);
            var last = Float(h["last_price"]);
            holdingValue += last * quantity;
            investedValue += average * quantity;
            pnl += Float(h["pnl"]);
            // A missing price coerces to 0; reporting that as a -100% loss would be a fabricated wipeout.
            var percent = average == 0 || last == 0 ? 0.0 : BookFormatting.Round2((last - average) / average * 100);
            holdings.Add(new JsonObject
            {
                ["symbol"] = Copy(h["tradingsymbol"], ""),
                ["exchange"] = Copy(h["exchange"], ""),
                ["quantity"] = quantity,
                // Holdings sit in the demat account, so CNC whatever Kite labels them.
                ["product"] = "CNC",
                ["average_price"] = average,
                ["ltp"] = last,
                ["pnl"] = BookFormatting.Round2(Float(h["pnl"])),
                ["pnlpercent"] = percent,
            });
        }

        return new BrokerBook(holdings, new JsonObject
        {
            ["totalholdingvalue"] = holdingValue,
            ["totalinvvalue"] = investedValue,
            ["totalprofitandloss"] = pnl,
            ["totalpnlpercentage"] = investedValue != 0 ? pnl / investedValue * 100 : 0,
        });
    }

    // ---- funds -----------------------------------------------------------------------------------------------

    public async Task<JsonObject> GetFundsAsync(BrokerSession session, CancellationToken cancellationToken)
    {
        JsonObject margins;
        try
        {
            margins = await GetAsync(session, "/user/margins", cancellationToken);
        }
        catch (BrokerException ex)
        {
            logger.LogError(ex, "Error fetching Kite margin data.");
            return [];
        }

        if (Str(margins["status"]) == "error" || margins["data"] is not JsonObject data
            || data["equity"] is not JsonObject equity || data["commodity"] is not JsonObject commodity)
            return [];

        var used = Float(commodity["utilised"]?["debits"]) + Float(equity["utilised"]?["debits"]);
        var collateral = Float(commodity["available"]?["collateral"]) + Float(equity["available"]?["collateral"]);
        // Kite's available.cash has been seen reporting 0 for a funded account; net = cash + collateral - debits is reliable.
        var available = Float(commodity["net"]) + Float(equity["net"]) + used - collateral;

        double realised = 0, unrealised = 0;
        try
        {
            var book = await GetAsync(session, "/portfolio/positions", cancellationToken);
            if (Str(book["status"]) == "success" && book["data"]?["net"] is JsonArray net)
            {
                var open = new List<JsonObject>();
                foreach (var p in net.OfType<JsonObject>())
                {
                    if (Float(p["quantity"]) == 0) realised += Float(p["sell_value"]) - Float(p["buy_value"]);
                    else open.Add(p);
                }

                if (open.Count > 0)
                {
                    var query = string.Join("&", open.Select(p => $"i={Str(p["exchange"])}:{Str(p["tradingsymbol"])}"));
                    var ltp = await GetAsync(session, $"/quote/ltp?{query}", cancellationToken);
                    var map = new Dictionary<string, double>(StringComparer.Ordinal);
                    if (Str(ltp["status"]) == "success" && ltp["data"] is JsonObject quotes)
                        foreach (var (k, v) in quotes) map[k] = Float(v?["last_price"]);
                    foreach (var p in open)
                    {
                        var key = $"{Str(p["exchange"])}:{Str(p["tradingsymbol"])}";
                        var multiplier = Float(p["multiplier"], 1.0);
                        if (multiplier == 0) multiplier = 1.0;
                        var mark = map.TryGetValue(key, out var m) ? m : Float(p["last_price"]);
                        unrealised += (mark - Float(p["average_price"])) * Float(p["quantity"]) * multiplier;
                    }
                }
            }
        }
        catch (Exception ex) when (ex is BrokerException or HttpRequestException or JsonException)
        {
            logger.LogError(ex, "Error fetching Kite positions for P&L.");
        }

        return new JsonObject
        {
            ["availablecash"] = BookFormatting.Fixed2(available),
            ["collateral"] = BookFormatting.Fixed2(collateral),
            ["m2munrealized"] = BookFormatting.Fixed2(unrealised),
            ["m2mrealized"] = BookFormatting.Fixed2(realised),
            ["utiliseddebits"] = BookFormatting.Fixed2(used),
        };
    }

    // ---- market data -----------------------------------------------------------------------------------------

    public async Task<JsonObject> GetQuoteAsync(BrokerSession session, string symbol, string exchange, CancellationToken cancellationToken)
    {
        return QuoteFields(await RawQuoteAsync(session, symbol, exchange, cancellationToken));
    }

    private static JsonObject QuoteFields(JsonObject quote)
    {
        var buy = quote["depth"]?["buy"] as JsonArray;
        var sell = quote["depth"]?["sell"] as JsonArray;
        var bestBid = buy is { Count: > 0 } ? buy[0] : null;
        var bestAsk = sell is { Count: > 0 } ? sell[0] : null;
        var ohlc = quote["ohlc"];
        return new JsonObject
        {
            ["ask"] = Copy(bestAsk?["price"], 0), ["bid"] = Copy(bestBid?["price"], 0),
            ["ask_qty"] = Copy(bestAsk?["quantity"], 0), ["bid_qty"] = Copy(bestBid?["quantity"], 0),
            ["high"] = Copy(ohlc?["high"], 0), ["low"] = Copy(ohlc?["low"], 0), ["ltp"] = Copy(quote["last_price"], 0),
            ["open"] = Copy(ohlc?["open"], 0), ["prev_close"] = Copy(ohlc?["close"], 0),
            ["volume"] = Copy(quote["volume"], 0), ["oi"] = Copy(quote["oi"], 0),
        };
    }

    public async Task<JsonObject> GetDepthAsync(BrokerSession session, string symbol, string exchange, CancellationToken cancellationToken)
    {
        var quote = await RawQuoteAsync(session, symbol, exchange, cancellationToken);
        var buy = quote["depth"]?["buy"] as JsonArray ?? [];
        var sell = quote["depth"]?["sell"] as JsonArray ?? [];
        static JsonArray Levels(JsonArray side)
        {
            var levels = new JsonArray();
            for (var i = 0; i < 5; i++)
                levels.Add(i < side.Count
                    ? new JsonObject { ["price"] = Copy(side[i]?["price"], 0), ["quantity"] = Copy(side[i]?["quantity"], 0) }
                    : new JsonObject { ["price"] = 0, ["quantity"] = 0 });
            return levels;
        }

        var ohlc = quote["ohlc"];
        return new JsonObject
        {
            ["asks"] = Levels(sell), ["bids"] = Levels(buy),
            ["high"] = Copy(ohlc?["high"], 0), ["low"] = Copy(ohlc?["low"], 0), ["ltp"] = Copy(quote["last_price"], 0),
            ["ltq"] = Copy(quote["last_quantity"], 0), ["oi"] = Copy(quote["oi"], 0),
            ["open"] = Copy(ohlc?["open"], 0), ["prev_close"] = Copy(ohlc?["close"], 0),
            ["totalbuyqty"] = buy.Sum(o => (long)Float(o?["quantity"])), ["totalsellqty"] = sell.Sum(o => (long)Float(o?["quantity"])),
            ["volume"] = Copy(quote["volume"], 0),
        };
    }

    private async Task<JsonObject> RawQuoteAsync(BrokerSession session, string symbol, string exchange, CancellationToken cancellationToken)
    {
        var instrument = await symbols.FindAsync(BrokerName, symbol, exchange, cancellationToken)
            ?? throw new BrokerException($"Error fetching quotes: Could not find exchange token for {exchange}:{symbol}");
        var key = $"{KiteExchange(exchange, instrument)}:{instrument.BrokerSymbol}";
        var raw = await GetAsync(session, $"/quote?i={Uri.EscapeDataString(key)}", cancellationToken);
        return raw["data"]?[key] as JsonObject is { Count: > 0 } quote
            ? quote
            : throw new BrokerException("Error fetching quotes: No quote data found");
    }

    /// <summary>The exchange Kite's quote API names an instrument by (indices on their cash exchange).</summary>
    private static string KiteExchange(string exchange, SymbolRecord instrument) => exchange switch
    {
        "NSE_INDEX" => "NSE",
        "BSE_INDEX" => "BSE",
        "MCX_INDEX" => "MCX",
        "GLOBAL_INDEX" => string.IsNullOrEmpty(instrument.BrokerExchange) || instrument.BrokerExchange == "GLOBAL_INDEX" ? "GLOBAL" : instrument.BrokerExchange,
        _ => exchange,
    };

    /// <summary>Kite answers up to 500 instruments per /quote call; OpenAlgo waits a second between calls.</summary>
    private const int QuoteBatch = 500;

    public async Task<JsonArray> GetMultiQuotesAsync(BrokerSession session, IReadOnlyList<SymbolRecord> instruments, CancellationToken cancellationToken)
    {
        var results = new JsonArray();
        for (var start = 0; start < instruments.Count; start += QuoteBatch)
        {
            if (start > 0) await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
            // One entry per Kite key: an instrument asked for twice is answered once, as OpenAlgo's map does.
            var batch = new Dictionary<string, SymbolRecord>();
            foreach (var instrument in instruments.Skip(start).Take(QuoteBatch))
                batch.TryAdd($"{KiteExchange(instrument.Exchange, instrument)}:{instrument.BrokerSymbol}", instrument);

            var query = string.Join("&", batch.Keys.Select(k => "i=" + Uri.EscapeDataString(k)));
            var data = (await GetAsync(session, "/quote?" + query, cancellationToken))["data"] as JsonObject ?? [];
            foreach (var (key, instrument) in batch)
            {
                if (data[key] is not JsonObject { Count: > 0 } quote)
                {
                    results.Add(new JsonObject { ["symbol"] = instrument.Symbol, ["exchange"] = instrument.Exchange, ["error"] = "No quote data available" });
                    continue;
                }

                results.Add(new JsonObject { ["symbol"] = instrument.Symbol, ["exchange"] = instrument.Exchange, ["data"] = QuoteFields(quote) });
            }
        }

        return results;
    }

    // ---- history ---------------------------------------------------------------------------------------------

    // timeframe_map of broker/zerodha/api/data.py, in its order. "1h" is the alias of "60m" OpenAlgo keeps for scans.
    private static readonly Dictionary<string, string> Resolutions = new()
    {
        ["1m"] = "minute", ["3m"] = "3minute", ["5m"] = "5minute", ["10m"] = "10minute", ["15m"] = "15minute", ["30m"] = "30minute",
        ["60m"] = "60minute", ["1h"] = "60minute", ["D"] = "day",
    };

    public IReadOnlyList<string> HistoryTimeframes { get; } = [.. Resolutions.Keys];

    public async Task<JsonArray> GetHistoryAsync(BrokerSession session, SymbolRecord instrument, string interval, DateOnly start, DateOnly end, CancellationToken cancellationToken)
    {
        if (!Resolutions.TryGetValue(interval, out var resolution))
            throw new BrokerException($"Zerodha does not offer {interval} candles. Choose one of: {string.Join(", ", Resolutions.Keys.Where(k => k != "1h"))}.", 400);

        // Kite's limits on one request: 2000 days of daily candles, 60 days of anything shorter.
        var chunkDays = resolution == "day" ? 2000 : 60;
        var instrumentToken = instrument.Token.Split("::::")[0];
        var candles = new SortedDictionary<long, JsonObject>();
        for (var from = start; from <= end;)
        {
            var to = from.AddDays(chunkDays - 1) < end ? from.AddDays(chunkDays - 1) : end;
            // The "+" is Kite's own spelling of the space between date and time; it is sent as is.
            var query = $"from={from:yyyy-MM-dd}+00:00:00&to={to:yyyy-MM-dd}+23:59:59&oi=1";
            var reply = await GetAsync(session, $"/instruments/historical/{instrumentToken}/{resolution}?{query}", cancellationToken);
            if (Str(reply["status"]) != "success")
                throw new BrokerException($"Error fetching historical data: Error from Zerodha API: {Str(reply["message"]) ?? "Unknown error"}");
            foreach (var row in reply["data"]?["candles"] as JsonArray ?? [])
            {
                if (row is not JsonArray c || c.Count < 7) continue;
                var stamp = KiteEpoch(Str(c[0]) ?? "") + (interval == "D" ? 19_800 : 0);
                // Two candles with one timestamp: the first one received is kept, as OpenAlgo's drop_duplicates keeps it.
                candles.TryAdd(stamp, new JsonObject
                {
                    ["timestamp"] = stamp, ["open"] = Float(c[1]), ["high"] = Float(c[2]), ["low"] = Float(c[3]), ["close"] = Float(c[4]),
                    ["volume"] = (long)Float(c[5]), ["oi"] = (long)Float(c[6]),
                });
            }

            from = to.AddDays(1);
        }

        return new JsonArray(candles.Values.Select(v => (JsonNode?)v).ToArray());
    }

    /// <summary>Epoch seconds of Kite's "2026-11-03T09:15:00+0530".</summary>
    private static long KiteEpoch(string text)
    {
        var local = DateTime.ParseExact(text[..19], "yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture);
        var sign = text.Length > 19 && text[19] == '-' ? -1 : 1;
        var offset = text.Length >= 24 ? TimeSpan.FromHours(int.Parse(text[20..22], CultureInfo.InvariantCulture)) + TimeSpan.FromMinutes(int.Parse(text[22..24], CultureInfo.InvariantCulture)) : TimeSpan.Zero;
        return new DateTimeOffset(local, sign * offset).ToUnixTimeSeconds();
    }

    // ---- margin ----------------------------------------------------------------------------------------------

    /// <summary>
    /// Kite's margin calculation: one position goes to /margins/orders, several to /margins/basket (which takes the benefit of
    /// hedged legs, with open positions considered). Answers the basket's final figures, or the sum of the orders'.
    /// </summary>
    public async Task<JsonObject> CalculateMarginAsync(BrokerSession session, IReadOnlyList<MarginLeg> legs, CancellationToken cancellationToken)
    {
        int Contracts(MarginLeg l)
        {
            try
            {
                return KiteQuantity(l.Quantity, l.Instrument, "Quantity");
            }
            catch (McxQuantityException ex)
            {
                throw new BrokerException(ex.Message, 400);
            }
        }

        var payload = new JsonArray(legs.Select(l => (JsonNode?)new JsonObject
        {
            ["exchange"] = l.Instrument.Exchange, ["tradingsymbol"] = l.Instrument.BrokerSymbol, ["transaction_type"] = l.Action, ["variety"] = "regular",
            ["product"] = l.Product, ["order_type"] = l.PriceType, ["quantity"] = Contracts(l), ["price"] = (double)l.Price, ["trigger_price"] = (double)l.TriggerPrice,
        }).ToArray());

        using var request = Request(session, HttpMethod.Post, legs.Count > 1 ? "/margins/basket?consider_positions=true" : "/margins/orders");
        request.Content = new StringContent(payload.ToJsonString(), System.Text.Encoding.UTF8);
        request.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
        using var response = await SendAsync(request, cancellationToken);
        var reply = await ReadJsonAsync(response, cancellationToken)
            ?? throw new BrokerException("Zerodha sent a reply EP Trade bot could not read. Try again in a moment.");
        if (Str(reply["status"]) != "success")
        {
            var type = Str(reply["error_type"]);
            var message = Str(reply["message"]) ?? "Failed to calculate margin";
            throw new BrokerException($"Zerodha could not work out the margin: {(type is null ? "" : type + ": ")}{message}", (int)response.StatusCode is >= 400 and < 600 ? (int)response.StatusCode : 500);
        }

        double total = 0, span = 0, exposure = 0;
        if (reply["data"] is JsonObject data && data["final"] is JsonObject final)
        {
            total = Float(final["total"]);
            span = Float(final["span"]);
            exposure = Float(final["exposure"]);
        }
        else if (reply["data"] is JsonArray orders)
        {
            foreach (var order in orders.OfType<JsonObject>())
            {
                total += Float(order["total"]);
                span += Float(order["span"]);
                exposure += Float(order["exposure"]);
            }
        }

        return new JsonObject { ["total_margin_required"] = total, ["span_margin"] = span, ["exposure_margin"] = exposure };
    }

    // ---- plumbing --------------------------------------------------------------------------------------------

    private const string UnansweredOrder =
        "The order was sent to Zerodha but no reply came back, so it may have been placed. Check the order book on Kite before trying again.";

    private static string UnknownSymbol(string symbol, string exchange) =>
        $"{symbol} on {exchange} is not in the Zerodha master contract. Check the symbol, or download the master contract again.";

    /// <summary>
    /// OpenAlgo units to the quantity Kite takes. Kite counts MCX in contracts; every other exchange is unchanged.
    /// </summary>
    private static int KiteQuantity(int quantity, SymbolRecord instrument, string field) =>
        instrument.Exchange == "MCX" ? McxContracts.ToKite(quantity, instrument.Symbol, instrument.LotSize, field) : quantity;

    /// <summary>A quantity read from Kite, in OpenAlgo units. MCX rows are multiplied by their lot; with <paramref name="strict"/> an MCX contract whose size cannot be known is refused.</summary>
    private async Task<JsonNode?> UnitsAsync(JsonObject row, JsonNode? value, CancellationToken cancellationToken, bool strict = false)
    {
        if (value is null) return null;
        if (Str(row["exchange"]) != "MCX") return value.DeepClone();
        var tradingSymbol = Str(row["tradingsymbol"]);
        var record = await symbols.FindByBrokerSymbolAsync(BrokerName, tradingSymbol ?? "", "MCX", cancellationToken);
        var lot = record?.LotSize ?? 0;
        // A position decides whether to trade: reading 30 held contracts as 30 units against a 30 unit target would call it matched.
        if (strict && McxContracts.UnitsPerContract(tradingSymbol, lot) is null)
            throw new BrokerException($"Cannot read the open position in {tradingSymbol}: MCX has revised its contract size and the master contract has no row for this expiry. Re-download the master contract, then retry.");
        return McxContracts.FromKite((long)Float(value), tradingSymbol, lot);
    }

    private async Task<double> McxTradeValueAsync(JsonObject trade, double units, double price, CancellationToken cancellationToken)
    {
        var tradingSymbol = Str(trade["tradingsymbol"]);
        var lot = (await symbols.FindByBrokerSymbolAsync(BrokerName, tradingSymbol ?? "", "MCX", cancellationToken))?.LotSize ?? 0;
        var size = McxContracts.UnitsPerContract(tradingSymbol, lot) ?? 1;
        return units / size * McxContracts.PriceMultiplier(tradingSymbol, lot) * price;
    }

    private async Task<JsonNode?> OpenAlgoSymbolAsync(JsonObject row, CancellationToken cancellationToken)
    {
        var brokerSymbol = Str(row["tradingsymbol"]);
        if (string.IsNullOrEmpty(brokerSymbol)) return row["tradingsymbol"]?.DeepClone();
        var record = await symbols.FindByBrokerSymbolAsync(BrokerName, brokerSymbol, Str(row["exchange"]) ?? "", cancellationToken);
        return record?.Symbol;
    }

    private HttpClient Client() => httpClients.CreateClient(BrokerName);

    private HttpRequestMessage Request(BrokerSession session, HttpMethod method, string pathAndQuery)
    {
        var request = new HttpRequestMessage(method, sessions.BaseUrl(BrokerName, DefaultBaseUrl) + pathAndQuery);
        request.Headers.TryAddWithoutValidation("X-Kite-Version", "3");
        request.Headers.TryAddWithoutValidation("Authorization", $"token {session.AuthToken}");
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
            throw new BrokerException("Zerodha did not answer. Check your connection, then try again.", 502, ex);
        }
    }

    private async Task<JsonObject> GetAsync(BrokerSession session, string pathAndQuery, CancellationToken cancellationToken)
    {
        using var request = Request(session, HttpMethod.Get, pathAndQuery);
        using var response = await SendAsync(request, cancellationToken);
        var body = await ReadJsonAsync(response, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var message = Str(body?["message"]);
            throw new BrokerException(response.StatusCode == HttpStatusCode.Forbidden
                ? $"Zerodha refused the request: {message ?? "your session may have expired. Log in to Zerodha again."}"
                : $"Zerodha could not answer: {message ?? "try again in a moment."}");
        }

        return body ?? throw new BrokerException("Zerodha sent a reply EP Trade bot could not read.");
    }

    private static async Task<JsonObject?> ReadJsonAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        try
        {
            var text = await response.Content.ReadAsStringAsync(cancellationToken);
            return string.IsNullOrWhiteSpace(text) ? null : JsonNode.Parse(text) as JsonObject;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string Num(decimal value) => value.ToString(CultureInfo.InvariantCulture);

    internal static string? Str(JsonNode? node) =>
        node is JsonValue v && v.TryGetValue<JsonElement>(out var e) ? e.ValueKind switch
        {
            JsonValueKind.String => e.GetString(),
            JsonValueKind.Number => e.GetRawText(),
            _ => null,
        } : node is JsonValue s && s.TryGetValue<string>(out var str) ? str : null;

    /// <summary>Python truthiness of a JSON value.</summary>
    private static bool Truthy(JsonNode? node) => node switch
    {
        null => false,
        JsonValue v when v.TryGetValue<JsonElement>(out var e) => e.ValueKind switch
        {
            JsonValueKind.String => e.GetString()!.Length > 0,
            JsonValueKind.Number => e.GetDouble() != 0,
            JsonValueKind.True => true,
            _ => false,
        },
        JsonArray a => a.Count > 0,
        JsonObject o => o.Count > 0,
        _ => true,
    };

    /// <summary>Python's float(x) with a default for null or text that is not a number.</summary>
    private static double Float(JsonNode? node, double fallback = 0)
    {
        if (BookFormatting.Number(node) is { } n) return n;
        return Str(node) is { } s && double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : fallback;
    }

    /// <summary>dict.get(key, default): the value as given, or the default when the key is absent.</summary>
    private static JsonNode? Copy(JsonNode? node, JsonNode? fallback) => node is null ? fallback : node.DeepClone();
}
