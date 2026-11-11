using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using EPTradeBot.Core.Brokers;
using Microsoft.Extensions.Logging;
using static EPTradeBot.Infrastructure.Brokers.JsonRead;

namespace EPTradeBot.Infrastructure.Brokers.Paytm;

/// <summary>
/// Paytm Money (developer.paytmmoney.com). Port of OpenAlgo's broker/paytm: api/order_api.py, api/funds.py, api/data.py (quotes,
/// depth, multiquotes), mapping/order_data.py and mapping/transform_data.py, held to tools/contract-fixtures/paytm.fixtures.json.
/// Paytm Money has no candle history and no position-level margin calculator, and OpenAlgo says so too. Not ported yet: login,
/// GTT and the Paytm Money feed.
/// </summary>
[BrokerPlugin]
public sealed partial class PaytmPlugin(IHttpClientFactory http, BrokerSessionFactory sessions, ISymbolMaster symbols, ILogger<PaytmPlugin> logger)
    : IBrokerOrders, IBrokerPortfolio, IBrokerFunds, IBrokerMultiQuotes, IBrokerLogin
{
    private const string DefaultBaseUrl = "https://developer.paytmmoney.com";

    public string Name => "paytm";
    public string DisplayName => "Paytm Money";
    public BrokerCapability Capabilities => BrokerCapability.Orders | BrokerCapability.Portfolio | BrokerCapability.Funds | BrokerCapability.Quotes | BrokerCapability.Depth;

    /// <summary>ApiKey and ApiSecret exchange the login's request token for the access token.</summary>
    public IReadOnlyList<string> RequiredKeys { get; } = ["ApiKey", "ApiSecret"];

    // ---- vocabulary ------------------------------------------------------------------------------------------

    /// <summary>map_exchange: Paytm Money lists derivatives under their cash exchange.</summary>
    private static string PaytmExchange(string exchange) => exchange switch { "NSE" => "NSE", "BSE" => "BSE", "NFO" => "NSE", "BFO" => "BSE", _ => "EXCHANGE" };

    /// <summary>reverse_map_product_type. OpenAlgo has no entry for NRML and sends nothing; Paytm Money's "M" (margin) is NRML.</summary>
    private static string PaytmProduct(string product) => product switch { "CNC" => "C", "MIS" => "I", "NRML" or "MARGIN" => "M", _ => "I" };

    private static string PaytmOrderType(string priceType) => priceType switch { "LIMIT" => "LMT", "SL" => "SL", "SL-M" => "SLM", _ => "MKT" };

    private static bool Derivative(string? instrument) => instrument is not null && (instrument.Contains("OPT", StringComparison.Ordinal) || instrument.Contains("FUT", StringComparison.Ordinal));

    /// <summary>A row's OpenAlgo exchange: Paytm Money's NSE or BSE, or NFO or BFO when the instrument is an option or future.</summary>
    private static string OpenAlgoExchange(string? exchange, string? instrument) => exchange switch
    {
        "NSE" when Derivative(instrument) => "NFO",
        "BSE" when Derivative(instrument) => "BFO",
        _ => exchange ?? "",
    };

    // ---- orders ----------------------------------------------------------------------------------------------

    public async Task<BrokerCallResult> PlaceAsync(BrokerSession session, BrokerOrder order, CancellationToken cancellationToken)
    {
        var instrument = await symbols.FindAsync(Name, order.Symbol, order.Exchange, cancellationToken);
        if (instrument is null) return BrokerCallResult.Refused(400, $"{order.Symbol} on {order.Exchange} is not in the {DisplayName} master contract. Check the symbol, or download the master contract again.");

        var payload = new JsonObject
        {
            ["security_id"] = instrument.Token,
            ["exchange"] = PaytmExchange(order.Exchange),
            ["txn_type"] = order.Action.Equals("BUY", StringComparison.OrdinalIgnoreCase) ? "B" : "S",
            ["order_type"] = PaytmOrderType(order.PriceType),
            ["quantity"] = order.Quantity.ToString(CultureInfo.InvariantCulture),
            ["product"] = PaytmProduct(order.Product),
            ["price"] = Num(order.Price),
            ["validity"] = "DAY",
            ["segment"] = order.Exchange is "NSE" or "BSE" ? "E" : "D",
            ["source"] = "M",
        };
        // OpenAlgo leaves the trigger price out of every order, so a stop order reaches Paytm Money without one.
        if (order.PriceType is "SL" or "SL-M") payload["trigger_price"] = Num(order.TriggerPrice);

        // An order is sent once: a retry after a lost answer could place it twice.
        using var request = Request(session, HttpMethod.Post, "/orders/v1/place/regular", payload);
        HttpResponseMessage response;
        try
        {
            response = await Client().SendAsync(request, cancellationToken);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning(ex, "Paytm Money did not answer the order.");
            return new BrokerCallResult(false, 502, $"The order was sent to {DisplayName} but no reply came back, so it may have been placed. Check the order book on {DisplayName} before trying again.", MayHaveBeenSent: true);
        }

        using (response)
        {
            var body = await ReadJsonAsync(response, cancellationToken);
            if (Str(body?["status"]) == "success" && (body!["data"] as JsonArray)?.FirstOrDefault() is JsonObject first && Str(first["order_no"]) is { Length: > 0 } id)
                return BrokerCallResult.Success(id);
            return BrokerCallResult.Refused(500, Str(body?["message"]) ?? "Failed to place order");
        }
    }

    public async Task<BrokerCallResult> ModifyAsync(BrokerSession session, BrokerOrderChange change, CancellationToken cancellationToken)
    {
        var book = await GetAsync(session, "/orders/v1/user/orders", cancellationToken);
        var order = (book["data"] as JsonArray ?? []).OfType<JsonObject>().FirstOrDefault(o => Str(o["order_no"]) == change.OrderId);
        if (order is null) return BrokerCallResult.Refused(404, $"Order {change.OrderId} not found");
        var status = Str(order["status"]) ?? "";
        if (!new[] { "OPEN", "TRIGGER PENDING", "MODIFIED", "PENDING" }.Contains(status.ToUpperInvariant()))
            return BrokerCallResult.Refused(400, $"Order {change.OrderId} cannot be modified. Current status: {status}");

        var market = change.PriceType == "MARKET";
        var payload = new JsonObject
        {
            ["order_no"] = change.OrderId,
            ["exchange"] = order["exchange"]?.DeepClone(),
            ["segment"] = order["segment"]?.DeepClone(),
            ["security_id"] = order["security_id"]?.DeepClone(),
            ["quantity"] = change.Quantity > 0 ? change.Quantity.ToString(CultureInfo.InvariantCulture) : order["quantity"]?.DeepClone(),
            ["price"] = market ? "0" : change.Price != 0 ? Num(change.Price) : order["price"]?.DeepClone(),
            ["trigger_price"] = change.TriggerPrice != 0 ? Num(change.TriggerPrice) : order["trigger_price"]?.DeepClone() ?? "0",
            ["validity"] = "DAY",
            ["product"] = PaytmProduct(change.Product),
            ["order_type"] = market ? "MKT" : order["order_type"]?.DeepClone(),
            ["txn_type"] = order["txn_type"]?.DeepClone(),
            ["source"] = "N",
            ["off_mkt_flag"] = order["off_mkt_flag"]?.DeepClone() ?? "N",
            ["serial_no"] = order["serial_no"]?.DeepClone(),
            ["group_id"] = order["group_id"]?.DeepClone(),
        };
        using var request = Request(session, HttpMethod.Post, "/orders/v1/modify/regular", payload);
        using var response = await SendAsync(request, cancellationToken);
        var body = await ReadJsonAsync(response, cancellationToken);
        if (Str(body?["status"]) == "success")
            return BrokerCallResult.Success(Str((body!["data"] as JsonArray)?.FirstOrDefault()?["order_no"]) ?? change.OrderId);
        return BrokerCallResult.Refused(500, Str(body?["message"]) ?? "Failed to modify order");
    }

    public async Task<BrokerCallResult> CancelAsync(BrokerSession session, string orderId, CancellationToken cancellationToken)
    {
        var book = await GetAsync(session, "/orders/v1/user/orders", cancellationToken);
        var order = (book["data"] as JsonArray ?? []).OfType<JsonObject>().FirstOrDefault(o => Str(o["order_no"]) == orderId);
        if (order is null) return BrokerCallResult.Refused(404, $"Order {orderId} not found");
        // OpenAlgo cancels only an order whose status is "Pending" and fails for any other, a resting "Open" order included; it lets the
        // same states be changed that it lets be cancelled here. A finished order is refused with its state.
        if (!new[] { "OPEN", "TRIGGER PENDING", "MODIFIED", "PENDING" }.Contains((Str(order["status"]) ?? "").ToUpperInvariant())) return BrokerCallResult.Refused(400, $"Order {orderId} is {Str(order["display_status"] ?? order["status"])?.ToLowerInvariant()}, so it cannot be cancelled.");

        var payload = new JsonObject
        {
            ["order_no"] = orderId, ["source"] = "N", ["txn_type"] = order["txn_type"]?.DeepClone(), ["exchange"] = order["exchange"]?.DeepClone(),
            ["segment"] = order["segment"]?.DeepClone(), ["product"] = order["product"]?.DeepClone(), ["security_id"] = order["security_id"]?.DeepClone(),
            ["quantity"] = order["quantity"]?.DeepClone(), ["validity"] = order["validity"]?.DeepClone(), ["order_type"] = order["order_type"]?.DeepClone(),
            ["price"] = order["price"]?.DeepClone(), ["off_mkt_flag"] = order["off_mkt_flag"]?.DeepClone(), ["mkt_type"] = order["mkt_type"]?.DeepClone(),
            ["serial_no"] = order["serial_no"]?.DeepClone(), ["group_id"] = order["group_id"]?.DeepClone(),
        };
        using var request = Request(session, HttpMethod.Post, "/orders/v1/cancel/regular", payload);
        using var response = await SendAsync(request, cancellationToken);
        var body = await ReadJsonAsync(response, cancellationToken);
        if (Truthy(body?["status"]) && Str(body?["status"]) != "error")
            return BrokerCallResult.Success(Str((body!["data"] as JsonArray)?.FirstOrDefault()?["order_no"]) ?? orderId);
        return BrokerCallResult.Refused(500, Str(body?["message"]) ?? "Failed to cancel order");
    }

    // ---- books -----------------------------------------------------------------------------------------------

    private async Task<string?> SymbolOfAsync(string? token, string? exchange, string? instrument, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(token)) return null;
        var found = await symbols.FindByTokenAsync(Name, token, OpenAlgoExchange(exchange, instrument), cancellationToken);
        return found?.Symbol;
    }

    public async Task<BrokerBook> GetOrderBookAsync(BrokerSession session, CancellationToken cancellationToken)
    {
        var raw = await GetAsync(session, "/orders/v1/user/orders", cancellationToken);
        int buy = 0, sell = 0, completed = 0, open = 0, rejected = 0;
        var orders = new JsonArray();
        foreach (var o in (raw["data"] as JsonArray ?? []).OfType<JsonObject>())
        {
            var side = Str(o["txn_type"]);
            if (side == "B") buy++;
            else if (side == "S") sell++;
            var display = Str(o["display_status"]) ?? "";
            if (display == "Successful") completed++;
            else if (display == "Pending") open++;
            else if (display == "Rejected") rejected++;

            var exchange = OpenAlgoExchange(Str(o["exchange"]), Str(o["instrument"]));
            orders.Add(new JsonObject
            {
                ["symbol"] = await SymbolOfAsync(Str(o["security_id"]), Str(o["exchange"]), Str(o["instrument"]), cancellationToken),
                ["exchange"] = exchange,
                ["action"] = side == "B" ? "BUY" : side == "S" ? "SELL" : side ?? "",
                ["quantity"] = Copy(o["quantity"], 0),
                ["price"] = Copy(o["price"], 0.0),
                ["trigger_price"] = Copy(o["trigger_price"], 0.0),
                ["pricetype"] = (Str(o["display_order_type"]) ?? "").ToUpperInvariant(),
                ["product"] = OrderProduct(Str(o["exchange"]), Str(o["product"])),
                ["orderid"] = Copy(o["order_no"], ""),
                ["order_status"] = display switch
                {
                    "Successful" => "complete", "Rejected" => "rejected", "Pending" => "trigger pending", "Open" => "open", "Cancelled" => "cancelled",
                    _ => display.ToLowerInvariant(),
                },
                ["timestamp"] = Copy(o["order_date_time"], ""),
            });
        }

        return new BrokerBook(orders, new JsonObject
        {
            ["total_buy_orders"] = buy, ["total_sell_orders"] = sell, ["total_completed_orders"] = completed,
            ["total_open_orders"] = open, ["total_rejected_orders"] = rejected,
        });
    }

    /// <summary>map_order_data: a cash order in "C" is CNC; "I" and "M" are MIS; anything else keeps Paytm Money's letter.</summary>
    private static string OrderProduct(string? exchange, string? product) => product switch
    {
        "C" when exchange is "NSE" or "BSE" => "CNC",
        "I" or "M" => "MIS",
        _ => product ?? "",
    };

    /// <summary>
    /// Paytm Money has no trade book of its own, so OpenAlgo lists every order of the day as a trade. EP Trade bot lists the orders
    /// that traded something, because a pending order is not a trade.
    /// </summary>
    public async Task<JsonArray> GetTradeBookAsync(BrokerSession session, CancellationToken cancellationToken)
    {
        var raw = await GetAsync(session, "/orders/v1/user/orders", cancellationToken);
        var trades = new JsonArray();
        foreach (var t in (raw["data"] as JsonArray ?? []).OfType<JsonObject>())
        {
            var average = Float(t["avg_traded_price"]);
            if (average <= 0) continue;
            var side = Str(t["txn_type"]);
            trades.Add(new JsonObject
            {
                ["symbol"] = await SymbolOfAsync(Str(t["security_id"]), Str(t["exchange"]), Str(t["instrument"]), cancellationToken),
                ["exchange"] = OpenAlgoExchange(Str(t["exchange"]), Str(t["instrument"])),
                ["product"] = OrderProduct(Str(t["exchange"]), Str(t["product"])),
                ["action"] = side == "B" ? "BUY" : side == "S" ? "SELL" : side ?? "",
                ["quantity"] = Copy(t["quantity"], 0),
                ["average_price"] = average,
                ["trade_value"] = Float(t["remaining_quantity"]) * average,
                ["orderid"] = Copy(t["order_no"], ""),
                ["timestamp"] = Copy(t["order_date_time"], ""),
            });
        }

        return trades;
    }

    public async Task<JsonArray> GetPositionsAsync(BrokerSession session, CancellationToken cancellationToken)
    {
        var raw = await GetAsync(session, "/orders/v1/position", cancellationToken);
        var positions = new JsonArray();
        foreach (var p in (raw["data"] as JsonArray ?? []).OfType<JsonObject>())
        {
            // The price shown is the one on the side the position is held: the buy average of a long, the sell average of a short.
            var average = Str(p["display_pos_type"]) switch { "B" => Float(p["buy_avg"]), "S" => Float(p["sell_avg"]), _ => Float(p["net_avg"]) };
            positions.Add(new JsonObject
            {
                ["symbol"] = await SymbolOfAsync(Str(p["security_id"]), Str(p["exchange"]), Str(p["instrument"]), cancellationToken) ?? Str(p["security_id"]),
                ["exchange"] = OpenAlgoExchange(Str(p["exchange"]), Str(p["instrument"])),
                // Paytm Money's "M" is margin, which is NRML (OpenAlgo calls it MARGIN, which no order can then be matched to).
                ["product"] = Str(p["product"]) switch { "C" => "CNC", "M" => "NRML", _ => "MIS" },
                ["quantity"] = Copy(p["net_qty"], "0"),
                ["average_price"] = BookFormatting.Fixed2(average),
                ["ltp"] = Copy(p["last_traded_price"], "0.00"),
                ["pnl"] = Copy(p["net_val"], "0.00"),
            });
        }

        return positions;
    }

    public async Task<BrokerBook> GetHoldingsAsync(BrokerSession session, CancellationToken cancellationToken)
    {
        var raw = await GetAsync(session, "/holdings/v1/get-user-holdings-data", cancellationToken);
        var list = raw["data"] switch { JsonArray a => a, JsonObject o when o["results"] is JsonArray r => r, _ => [] };
        double invested = 0, current = 0, pnlTotal = 0;
        var holdings = new JsonArray();
        foreach (var h in list.OfType<JsonObject>())
        {
            var exchange = "NSE";
            var token = Str(h["nse_security_id"]) ?? "";
            if ((token.Length == 0 && Str(h["bse_security_id"]) is { Length: > 0 }) || Str(h["exchange"]) == "BSE")
            {
                exchange = "BSE";
                token = Str(h["bse_security_id"]) ?? "";
            }

            var symbol = await SymbolOfAsync(token, exchange, null, cancellationToken);
            symbol ??= Str(h[exchange == "NSE" ? "nse_symbol" : "bse_symbol"]);
            var average = Float(Truthy(h["cost_price"]) ? h["cost_price"] : h["avg_price"]);
            var last = Float(Truthy(h["last_traded_price"]) ? h["last_traded_price"] : h["ltp"]);
            var quantity = Float(h["quantity"]);
            var pnl = (last - average) * quantity;
            invested += average * quantity;
            current += last * quantity;
            pnlTotal += BookFormatting.Round2(pnl);
            holdings.Add(new JsonObject
            {
                ["symbol"] = string.IsNullOrEmpty(symbol) ? "Unknown" : symbol,
                ["exchange"] = exchange,
                ["quantity"] = Copy(h["quantity"], 0),
                ["avg_price"] = average,
                ["ltp"] = last,
                ["pnl"] = BookFormatting.Round2(pnl),
                ["product"] = "CNC",
                ["pnlpercent"] = average > 0 ? BookFormatting.Round2((last - average) / average * 100) : 0.0,
            });
        }

        return new BrokerBook(holdings, new JsonObject
        {
            ["totalholdingvalue"] = BookFormatting.Round2(current), ["totalinvvalue"] = BookFormatting.Round2(invested),
            ["totalprofitandloss"] = BookFormatting.Round2(pnlTotal),
            ["totalpnlpercentage"] = BookFormatting.Round2(invested > 0 ? pnlTotal / invested * 100 : 0),
            ["total_holdings"] = holdings.Count,
        });
    }

    // ---- funds -----------------------------------------------------------------------------------------------

    public async Task<JsonObject> GetFundsAsync(BrokerSession session, CancellationToken cancellationToken)
    {
        var funds = await GetAsync(session, "/accounts/v1/funds/summary?config=true", cancellationToken);
        var summary = funds["data"]?["funds_summary"] as JsonObject ?? [];
        var positions = await GetAsync(session, "/orders/v1/position", cancellationToken);
        double realised = 0, unrealised = 0;
        foreach (var p in (positions["data"] as JsonArray ?? []).OfType<JsonObject>())
        {
            realised += Float(p["realised_profit"]);
            unrealised += Float(p["unrealised_profit"]);
        }

        static string Two(double v) => BookFormatting.Fixed2(v);
        return new JsonObject
        {
            ["availablecash"] = Two(Float(summary["available_cash"])), ["collateral"] = Two(Float(summary["collaterals"])),
            ["m2munrealized"] = Two(unrealised), ["m2mrealized"] = Two(realised), ["utiliseddebits"] = Two(Float(summary["utilised_amount"])),
        };
    }

    // ---- quotes and depth ------------------------------------------------------------------------------------

    /// <summary>The "exchange:token:type" Paytm Money's price API names an instrument by.</summary>
    private static string Pref(SymbolRecord instrument)
    {
        var requestExchange = instrument.Exchange is "NFO" or "NSE_INDEX" ? "NSE" : instrument.Exchange is "BFO" or "BSE_INDEX" ? "BSE" : instrument.Exchange;
        string type;
        if (instrument.Exchange is "NSE_INDEX" or "BSE_INDEX") type = "INDEX";
        else
        {
            var parts = instrument.BrokerSymbol.Split('-');
            type = parts.Length > 2 ? parts[^1] is "CE" or "PE" ? "OPTION" : parts[^1].Contains("FUT", StringComparison.Ordinal) ? "FUTURE" : "EQUITY" : "EQUITY";
        }

        return $"{requestExchange}:{instrument.Token}:{type}";
    }

    private async Task<JsonObject> LiveQuoteAsync(BrokerSession session, string symbol, string exchange, string what, CancellationToken cancellationToken)
    {
        var instrument = await symbols.FindAsync(Name, symbol, exchange, cancellationToken)
            ?? throw new BrokerException($"Error fetching {what}: {symbol} on {exchange} is not in the {DisplayName} master contract.");
        var reply = await GetAsync(session, $"/data/v1/price/live?mode=FULL&pref={Uri.EscapeDataString(Pref(instrument))}", cancellationToken);
        return (reply["data"] as JsonArray)?.FirstOrDefault() as JsonObject is { Count: > 0 } quote
            ? quote
            : throw new BrokerException($"{DisplayName} has no {what} for {symbol} right now.");
    }

    public async Task<JsonObject> GetQuoteAsync(BrokerSession session, string symbol, string exchange, CancellationToken cancellationToken)
    {
        var quote = await LiveQuoteAsync(session, symbol, exchange, "quote", cancellationToken);
        return QuoteFields(quote);
    }

    private static JsonObject QuoteFields(JsonObject quote) => new()
    {
        // Paytm Money's price API carries no best bid or ask.
        ["ask"] = 0, ["bid"] = 0,
        ["high"] = Copy(quote["ohlc"]?["high"], 0), ["low"] = Copy(quote["ohlc"]?["low"], 0), ["ltp"] = Copy(quote["last_price"], 0),
        ["open"] = Copy(quote["ohlc"]?["open"], 0), ["prev_close"] = Copy(quote["ohlc"]?["close"], 0),
        ["volume"] = Copy(quote["volume_traded"], 0), ["oi"] = Copy(quote["oi"], 0),
    };

    public async Task<JsonObject> GetDepthAsync(BrokerSession session, string symbol, string exchange, CancellationToken cancellationToken)
    {
        var quote = await LiveQuoteAsync(session, symbol, exchange, "market depth", cancellationToken);
        var depth = quote["depth"] as JsonObject;
        var sell = depth?["sell"] as JsonArray ?? [];
        var buy = depth?["buy"] as JsonArray ?? [];
        JsonArray Levels(JsonArray source) => new(Enumerable.Range(0, 5).Select(i => (JsonNode?)new JsonObject
        {
            ["price"] = i < source.Count ? Copy(source[i]?["price"], 0) : 0, ["quantity"] = i < source.Count ? Copy(source[i]?["quantity"], 0) : 0,
        }).ToArray());
        return new JsonObject
        {
            ["asks"] = Levels(sell), ["bids"] = Levels(buy),
            ["high"] = Copy(quote["ohlc"]?["high"], 0), ["low"] = Copy(quote["ohlc"]?["low"], 0), ["ltp"] = Copy(quote["last_price"], 0), ["ltq"] = Copy(quote["last_quantity"], 0),
            ["oi"] = Copy(quote["oi"], 0), ["open"] = Copy(quote["ohlc"]?["open"], 0), ["prev_close"] = Copy(quote["ohlc"]?["close"], 0),
            ["totalbuyqty"] = buy.Sum(o => Int(o?["quantity"])), ["totalsellqty"] = sell.Sum(o => Int(o?["quantity"])),
            ["volume"] = Copy(quote["volume"], 0),
        };
    }

    /// <summary>Paytm Money answers up to 100 instruments per price call; OpenAlgo waits 100 ms between calls.</summary>
    private const int QuoteBatch = 100;

    public async Task<JsonArray> GetMultiQuotesAsync(BrokerSession session, IReadOnlyList<SymbolRecord> instruments, CancellationToken cancellationToken)
    {
        var results = new JsonArray();
        for (var start = 0; start < instruments.Count; start += QuoteBatch)
        {
            if (start > 0) await Task.Delay(TimeSpan.FromMilliseconds(100), cancellationToken);
            var batch = instruments.Skip(start).Take(QuoteBatch).ToList();
            JsonArray data;
            try
            {
                var reply = await GetAsync(session, $"/data/v1/price/live?mode=FULL&pref={Uri.EscapeDataString(string.Join(",", batch.Select(Pref)))}", cancellationToken);
                data = reply["data"] as JsonArray is { Count: > 0 } d ? d : throw new BrokerException(Str(reply["message"]) ?? "No data received");
            }
            catch (BrokerException ex)
            {
                foreach (var i in batch) results.Add(new JsonObject { ["symbol"] = i.Symbol, ["exchange"] = i.Exchange, ["error"] = ex.Message });
                continue;
            }

            var byToken = new Dictionary<string, JsonObject>();
            foreach (var q in data.OfType<JsonObject>()) if (Str(q["security_id"]) is { Length: > 0 } id) byToken[id] = q;
            foreach (var i in batch)
            {
                results.Add(byToken.TryGetValue(i.Token, out var quote)
                    ? new JsonObject { ["symbol"] = i.Symbol, ["exchange"] = i.Exchange, ["data"] = QuoteFields(quote) }
                    : new JsonObject { ["symbol"] = i.Symbol, ["exchange"] = i.Exchange, ["error"] = "No data received" });
            }
        }

        return results;
    }

    // ---- plumbing --------------------------------------------------------------------------------------------

    private static string Num(decimal value) => value.ToString(CultureInfo.InvariantCulture);

    private static JsonNode? Copy(JsonNode? node, JsonNode? fallback) => node is null ? fallback?.DeepClone() : node.DeepClone();

    private HttpClient Client() => http.CreateClient(Name);

    private HttpRequestMessage Request(BrokerSession session, HttpMethod method, string pathAndQuery, JsonObject? body = null)
    {
        var request = new HttpRequestMessage(method, sessions.BaseUrl(Name, DefaultBaseUrl) + pathAndQuery);
        request.Headers.TryAddWithoutValidation("x-jwt-token", session.AuthToken);
        request.Headers.TryAddWithoutValidation("Accept", "application/json");
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
            throw new BrokerException($"{DisplayName} did not answer. Check your connection, then try again.", 502, ex);
        }
    }

    /// <summary>
    /// A read. A server error or a lost answer is tried again (three tries, two seconds apart), as OpenAlgo does; anything below 500
    /// is Paytm Money's answer and is read as it stands.
    /// </summary>
    private async Task<JsonObject> GetAsync(BrokerSession session, string pathAndQuery, CancellationToken cancellationToken)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                using var request = Request(session, HttpMethod.Get, pathAndQuery);
                using var response = await Client().SendAsync(request, cancellationToken);
                var body = await ReadJsonAsync(response, cancellationToken);
                if ((int)response.StatusCode >= 500) throw new HttpRequestException($"HTTP {(int)response.StatusCode}");
                if (!response.IsSuccessStatusCode)
                {
                    throw new BrokerException(response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden
                        ? $"{DisplayName} says the session has expired. Log in to {DisplayName} again."
                        : $"{DisplayName} could not answer: {Str(body?["message"]) ?? "try again in a moment."}", response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden ? 403 : 500);
                }

                return body ?? throw new BrokerException($"{DisplayName} sent a reply EP Trade bot could not read. Try again in a moment.");
            }
            catch (HttpRequestException ex) when (attempt < 2)
            {
                logger.LogWarning(ex, "{Broker} read failed (attempt {Attempt}).", DisplayName, attempt + 1);
                await Task.Delay(sessions.RetryBackoff(Name, TimeSpan.FromSeconds(2)), cancellationToken);
            }
            catch (HttpRequestException ex)
            {
                throw new BrokerException($"{DisplayName} did not answer. Check your connection, then try again.", 502, ex);
            }
        }
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
}
