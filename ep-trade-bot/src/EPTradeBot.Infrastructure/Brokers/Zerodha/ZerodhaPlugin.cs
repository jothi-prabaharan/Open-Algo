using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using EPTradeBot.Core.Brokers;
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
public sealed class ZerodhaPlugin(
    IHttpClientFactory httpClients,
    BrokerSessionFactory sessions,
    ISymbolMaster symbols,
    ILogger<ZerodhaPlugin> logger) : IBrokerOrders, IBrokerPortfolio, IBrokerFunds, IBrokerQuotes
{
    public const string BrokerName = "zerodha";
    private const string DefaultBaseUrl = "https://api.kite.trade";

    public string Name => BrokerName;
    public string DisplayName => "Zerodha";
    public BrokerCapability Capabilities => BrokerCapability.Orders | BrokerCapability.Portfolio | BrokerCapability.Funds | BrokerCapability.Quotes | BrokerCapability.Depth;
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
        if (order.Exchange == "MCX") return BrokerCallResult.Refused(400, McxRefusal);
        var instrument = await symbols.FindAsync(BrokerName, order.Symbol, order.Exchange, cancellationToken);
        if (instrument is null) return BrokerCallResult.Refused(400, UnknownSymbol(order.Symbol, order.Exchange));

        var tag = "openalgo" + Convert.ToHexStringLower(System.Security.Cryptography.RandomNumberGenerator.GetBytes(6));
        var form = new List<KeyValuePair<string, string>>
        {
            new("tradingsymbol", instrument.BrokerSymbol),
            new("exchange", order.Exchange),
            new("transaction_type", order.Action.ToUpperInvariant()),
            new("order_type", order.PriceType),
            new("quantity", order.Quantity.ToString(CultureInfo.InvariantCulture)),
            new("product", order.Product),
            new("price", Num(order.Price)),
            new("trigger_price", Num(order.TriggerPrice)),
            new("disclosed_quantity", order.DisclosedQuantity.ToString(CultureInfo.InvariantCulture)),
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
        if (change.Exchange == "MCX") return BrokerCallResult.Refused(400, McxRefusal);
        var form = new List<KeyValuePair<string, string>>
        {
            new("order_type", change.PriceType is "MARKET" or "LIMIT" or "SL" or "SL-M" ? change.PriceType : "MARKET"),
            new("quantity", change.Quantity.ToString(CultureInfo.InvariantCulture)),
            new("price", change.Price != 0 ? Num(change.Price) : "0"),
            new("disclosed_quantity", change.DisclosedQuantity != 0 ? change.DisclosedQuantity.ToString(CultureInfo.InvariantCulture) : "0"),
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
        RefuseMcx(rows);
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
                ["quantity"] = Copy(o["quantity"], 0),
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
        RefuseMcx(rows);
        var trades = new JsonArray();
        foreach (var t in rows.OfType<JsonObject>())
        {
            var quantity = BookFormatting.Number(t["quantity"]) ?? 0;
            var average = BookFormatting.Number(t["average_price"]) ?? 0;
            var fill = t["fill_timestamp"];
            trades.Add(new JsonObject
            {
                ["symbol"] = await OpenAlgoSymbolAsync(t, cancellationToken),
                ["exchange"] = Copy(t["exchange"], ""),
                ["product"] = Copy(t["product"], ""),
                ["action"] = Copy(t["transaction_type"], ""),
                ["quantity"] = Copy(t["quantity"], 0),
                ["average_price"] = Copy(t["average_price"], 0.0),
                ["trade_value"] = quantity * average,
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
        RefuseMcx(rows);
        var positions = new JsonArray();
        foreach (var p in rows.OfType<JsonObject>())
        {
            positions.Add(new JsonObject
            {
                ["symbol"] = await OpenAlgoSymbolAsync(p, cancellationToken),
                ["exchange"] = Copy(p["exchange"], ""),
                ["product"] = Copy(p["product"], ""),
                ["quantity"] = Copy(p["quantity"], "0"),
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
        var quote = await RawQuoteAsync(session, symbol, exchange, cancellationToken);
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
        var kiteExchange = exchange switch
        {
            "NSE_INDEX" => "NSE",
            "BSE_INDEX" => "BSE",
            "MCX_INDEX" => "MCX",
            "GLOBAL_INDEX" => string.IsNullOrEmpty(instrument.BrokerExchange) || instrument.BrokerExchange == "GLOBAL_INDEX" ? "GLOBAL" : instrument.BrokerExchange,
            _ => exchange,
        };
        var key = $"{kiteExchange}:{instrument.BrokerSymbol}";
        var raw = await GetAsync(session, $"/quote?i={Uri.EscapeDataString(key)}", cancellationToken);
        return raw["data"]?[key] as JsonObject is { Count: > 0 } quote
            ? quote
            : throw new BrokerException("Error fetching quotes: No quote data found");
    }

    // ---- plumbing --------------------------------------------------------------------------------------------

    private const string McxRefusal = "MCX orders through Zerodha are not supported in EP Trade bot yet. Place this order on Kite directly.";
    private const string UnansweredOrder =
        "The order was sent to Zerodha but no reply came back, so it may have been placed. Check the order book on Kite before trying again.";

    private static string UnknownSymbol(string symbol, string exchange) =>
        $"{symbol} on {exchange} is not in the Zerodha master contract. Check the symbol, or download the master contract again.";

    private static void RefuseMcx(JsonArray rows)
    {
        if (rows.OfType<JsonObject>().Any(r => Str(r["exchange"]) == "MCX"))
            throw new BrokerException("This Zerodha account holds MCX orders or positions, which EP Trade bot cannot read yet (Kite counts MCX in contracts). Use Kite for this account until MCX support arrives.");
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
