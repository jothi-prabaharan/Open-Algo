using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using EPTradeBot.Core.Brokers;
using static EPTradeBot.Infrastructure.Brokers.JsonRead;

namespace EPTradeBot.Infrastructure.Brokers.Rupeezy;

/// <summary>
/// Rupeezy (Vortex API, vortex-api.rupeezy.in/v2). Port of broker/rupeezy: api/order_api.py, api/funds.py, mapping/exchange.py, mapping/order_data.py and
/// mapping/transform_data.py. A ticker is "NSE:SBIN" (the first three letters of the exchange, a colon, the broker symbol); MCX orders and books count lots, not
/// units. Not ported yet: quotes and depth, history, margin, GTT, sign-in and the feed.
/// </summary>
[BrokerPlugin]
public sealed class RupeezyPlugin(IHttpClientFactory http, BrokerSessionFactory sessions, ISymbolMaster symbols) : IBrokerOrders, IBrokerPortfolio, IBrokerFunds
{
    private const string DefaultBaseUrl = "https://vortex-api.rupeezy.in/v2";
    private const int OrderPage = 500;
    private const int MaxOrderPages = 20;
    private const int LtpBatch = 1000;

    public string Name => "rupeezy";
    public string DisplayName => "Rupeezy";
    public BrokerCapability Capabilities => BrokerCapability.Orders | BrokerCapability.Portfolio | BrokerCapability.Funds;
    public IReadOnlyList<string> RequiredKeys { get; } = ["ApiKey", "ApiSecret"];

    // ---- vocabulary ------------------------------------------------------------------------------------------

    private static string Variety(string priceType) => priceType.ToUpperInvariant() switch { "MARKET" => "RL-MKT", "LIMIT" => "RL", "SL" => "SL", "SL-M" => "SL-MKT", _ => "RL-MKT" };

    private static string ReverseVariety(string? variety) => (variety ?? "").ToUpperInvariant() switch { "RL-MKT" => "MARKET", "RL" => "LIMIT", "SL" => "SL", "SL-MKT" => "SL-M", _ => variety ?? "" };

    private static string Product(string product) => product.ToUpperInvariant() switch { "CNC" or "NRML" => "DELIVERY", "MIS" => "INTRADAY", _ => "INTRADAY" };

    private static readonly HashSet<string> CashExchanges = ["NSE", "BSE", "NSE_EQ", "BSE_EQ"];

    private static string? ReverseProduct(string exchange, string? product)
    {
        var p = (product ?? "").ToUpperInvariant();
        if (p == "INTRADAY") return "MIS";
        if (p is "DELIVERY" or "MTF" or "BTST") return CashExchanges.Contains(exchange.ToUpperInvariant()) ? "CNC" : "NRML";
        return p.Length == 0 ? null : p;
    }

    private static string OpenAlgoExchange(string? vortex) => (vortex ?? "").ToUpperInvariant() switch
    {
        "NSE_EQ" => "NSE", "BSE_EQ" => "BSE", "NSE_FO" => "NFO", "BSE_FO" => "BFO", "MCX_FO" => "MCX", _ => vortex ?? "",
    };

    private static string Ticker(string brokerExchange, string brokerSymbol) => $"{(brokerExchange.Length > 3 ? brokerExchange[..3] : brokerExchange)}:{brokerSymbol}";

    private static string SplitTicker(string? ticker) => ticker is { } t && t.Contains(':', StringComparison.Ordinal) ? t[(t.IndexOf(':') + 1)..] : ticker ?? "";

    private static readonly HashSet<string> LotDenominated = ["MCX_FO"];

    private static long FromVortexQuantity(JsonNode? quantity, string? brokerExchange, int lotSize)
    {
        var units = Int(quantity);
        return LotDenominated.Contains(brokerExchange ?? "") ? units * Math.Max(lotSize, 1) : units;
    }

    private static readonly Dictionary<string, double> McxQuotationMultipliers = new()
    {
        ["GOLD"] = 100, ["GOLDM"] = 10, ["GOLDGUINEA"] = 1, ["GOLDTEN"] = 1, ["SILVER100"] = 10, ["ZINC"] = 5000, ["ZINCMINI"] = 1000, ["LEAD"] = 5000, ["LEADMINI"] = 1000,
        ["ALUMINIUM"] = 5000, ["ALUMINI"] = 1000, ["KAPAS"] = 200, ["COTTONOIL"] = 500,
    };

    /// <summary>The rupee value of a fill: units times price, except on MCX, where a contract quoted for a smaller unit is lots times price times its quotation multiplier.</summary>
    private static double TradeValue(double units, double price, string exchange, string? underlying, int lotSize)
    {
        if (exchange != "MCX" || lotSize == 0) return units * price;
        var lots = units / lotSize;
        return lots * price * (underlying is not null && McxQuotationMultipliers.TryGetValue(underlying, out var m) ? m : lotSize);
    }

    private static double F(JsonNode? node) => Truthy(node) ? Float(node) : 0.0;

    // ---- plumbing --------------------------------------------------------------------------------------------

    private HttpClient Client() => http.CreateClient(Name);

    private HttpRequestMessage Request(BrokerSession session, HttpMethod method, string pathAndQuery, JsonObject? body = null)
    {
        var request = new HttpRequestMessage(method, sessions.BaseUrl(Name, DefaultBaseUrl) + pathAndQuery);
        request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {session.AuthToken}");
        if (session.Key("ApiSecret") is { Length: > 0 } apiKey) request.Headers.TryAddWithoutValidation("x-api-key", apiKey);
        if (body is not null) request.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
        return request;
    }

    /// <summary>The JSON a call answered with. A reply that is not JSON, or no reply, is an error the trader can act on; an error status keeps its body, which says why.</summary>
    private async Task<(JsonObject Body, int Status)> SendAsync(BrokerSession session, HttpMethod method, string pathAndQuery, JsonObject? body, CancellationToken cancellationToken)
    {
        using var request = Request(session, method, pathAndQuery, body);
        try
        {
            using var response = await Client().SendAsync(request, cancellationToken);
            var node = response.IsSuccessStatusCode && response.Content.Headers.ContentLength == 0 ? new JsonObject { ["status"] = "success" } : await JsonRead.ReadAsync(response, cancellationToken);
            if (node is not JsonObject o) return (new JsonObject { ["status"] = "error", ["message"] = $"{DisplayName} returned an unexpected response. Please retry." }, (int)response.StatusCode);
            if (!response.IsSuccessStatusCode && !o.ContainsKey("status")) o["status"] = "error";
            return (o, (int)response.StatusCode);
        }
        catch (HttpRequestException ex)
        {
            throw new BrokerException($"{DisplayName} did not answer. Check your connection, then try again.", 502, ex);
        }
    }

    private async Task<JsonObject> GetAsync(BrokerSession session, string pathAndQuery, CancellationToken cancellationToken) =>
        (await SendAsync(session, HttpMethod.Get, pathAndQuery, null, cancellationToken)).Body;

    private static string OrderPath(string basePath, string id) => $"{basePath}/{Uri.EscapeDataString(id)}";

    // ---- orders ----------------------------------------------------------------------------------------------

    private static string? WholeLots(int quantity, string brokerExchange, int lotSize, out long vortex)
    {
        vortex = quantity;
        if (!LotDenominated.Contains(brokerExchange) || lotSize <= 1) return null;
        if (quantity % lotSize != 0) return $"Quantity {quantity} is not a multiple of the lot size {lotSize}.";
        vortex = quantity / lotSize;
        return null;
    }

    public async Task<BrokerCallResult> PlaceAsync(BrokerSession session, BrokerOrder order, CancellationToken cancellationToken)
    {
        var instrument = await symbols.FindAsync(Name, order.Symbol, order.Exchange, cancellationToken);
        if (instrument is null) return BrokerCallResult.Refused(400, $"Symbol {order.Symbol} not found on {order.Exchange}. Download the master contract.");
        var lot = instrument.LotSize > 0 ? instrument.LotSize : 1;
        if (WholeLots(order.Quantity, instrument.BrokerExchange, lot, out var quantity) is { } refusal) return BrokerCallResult.Refused(400, refusal);
        if (WholeLots(order.DisclosedQuantity, instrument.BrokerExchange, lot, out var disclosed) is { } disclosedRefusal) return BrokerCallResult.Refused(400, disclosedRefusal);

        var variety = Variety(order.PriceType);
        var market = variety is "RL-MKT" or "SL-MKT";
        var strategy = string.IsNullOrEmpty(order.Strategy) ? "openalgo" : order.Strategy;
        var payload = new JsonObject
        {
            ["ticker"] = Ticker(instrument.BrokerExchange, instrument.BrokerSymbol), ["transaction_type"] = order.Action.ToUpperInvariant(), ["product"] = Product(order.Product),
            ["variety"] = variety, ["quantity"] = quantity, ["price"] = market ? 0.0 : (double)order.Price, ["trigger_price"] = variety is "SL" or "SL-MKT" ? (double)order.TriggerPrice : 0.0,
            ["disclosed_quantity"] = disclosed, ["validity"] = "DAY", ["is_amo"] = false, ["order_identifier"] = strategy.Length > 50 ? strategy[..50] : strategy,
        };

        // An order is sent once: a retry after a lost answer could place it twice.
        (JsonObject Body, int Status) reply;
        try
        {
            reply = await SendAsync(session, HttpMethod.Post, "/trading/orders/regular", payload, cancellationToken);
        }
        catch (BrokerException ex) when (ex.HttpStatus == 502)
        {
            return new BrokerCallResult(false, 502, $"The order was sent to {DisplayName} but no reply came back, so it may have been placed. Check the order book on {DisplayName} before trying again.", MayHaveBeenSent: true);
        }

        if (Str(reply.Body["status"]) == "success" && Str((reply.Body["data"] as JsonObject)?["order_id"]) is { Length: > 0 } id) return BrokerCallResult.Success(id);
        return BrokerCallResult.Refused(reply.Status >= 500 ? 500 : reply.Status < 400 ? 400 : reply.Status, Str(reply.Body["message"]) ?? "Failed to place order");
    }

    public async Task<BrokerCallResult> ModifyAsync(BrokerSession session, BrokerOrderChange change, CancellationToken cancellationToken)
    {
        var instrument = await symbols.FindAsync(Name, change.Symbol, change.Exchange, cancellationToken);
        if (instrument is null) return BrokerCallResult.Refused(400, $"Symbol {change.Symbol} not found on {change.Exchange}. Download the master contract.");
        var lot = instrument.LotSize > 0 ? instrument.LotSize : 1;
        // Vortex grows the order by the quantity already traded unless it is told what that is, so the order's latest state is read first.
        var history = await GetAsync(session, OrderPath("/trading/orders", change.OrderId), cancellationToken);
        if (Str(history["status"]) != "success") return BrokerCallResult.Refused(502, Str(history["message"]) ?? $"Could not read the order from {DisplayName}.");
        var state = ((history["orders"] ?? history["data"]) as JsonArray)?.OfType<JsonObject>().FirstOrDefault();
        if (state is null) return BrokerCallResult.Refused(404, $"Order {change.OrderId} was not found.");
        if (WholeLots(change.Quantity, instrument.BrokerExchange, lot, out var quantity) is { } refusal) return BrokerCallResult.Refused(400, refusal);
        if (WholeLots(change.DisclosedQuantity, instrument.BrokerExchange, lot, out var disclosed) is { } disclosedRefusal) return BrokerCallResult.Refused(400, disclosedRefusal);

        var variety = Variety(change.PriceType);
        var payload = new JsonObject
        {
            ["variety"] = variety, ["quantity"] = quantity, ["traded_quantity"] = (long)Math.Truncate(F(state["traded_quantity"])),
            ["price"] = variety is "RL-MKT" or "SL-MKT" ? 0.0 : (double)change.Price, ["trigger_price"] = variety is "SL" or "SL-MKT" ? (double)change.TriggerPrice : 0.0,
            ["disclosed_quantity"] = disclosed, ["validity"] = "DAY",
        };
        var reply = (await SendAsync(session, HttpMethod.Put, OrderPath("/trading/orders/regular", change.OrderId), payload, cancellationToken)).Body;
        if (Str(reply["status"]) == "success") return BrokerCallResult.Success(Str((reply["data"] as JsonObject)?["order_id"]) ?? change.OrderId);
        return BrokerCallResult.Refused(400, Str(reply["message"]) ?? "Failed to modify order");
    }

    public async Task<BrokerCallResult> CancelAsync(BrokerSession session, string orderId, CancellationToken cancellationToken)
    {
        var reply = (await SendAsync(session, HttpMethod.Delete, OrderPath("/trading/orders/regular", orderId), null, cancellationToken)).Body;
        if (Str(reply["status"]) == "success") return BrokerCallResult.Success(orderId);
        return BrokerCallResult.Refused(400, Str(reply["message"]) ?? "Failed to cancel order");
    }

    // ---- books -----------------------------------------------------------------------------------------------

    private async Task<JsonArray> OrderRowsAsync(BrokerSession session, CancellationToken cancellationToken)
    {
        var orders = new JsonArray();
        for (var page = 0; page < MaxOrderPages; page++)
        {
            var reply = await GetAsync(session, $"/trading/orders?limit={OrderPage}&offset={page * OrderPage + 1}", cancellationToken);
            if (Str(reply["status"]) != "success") throw new BrokerException($"{DisplayName} could not send the order book: {Str(reply["message"]) ?? "no reason was given."}");
            var batch = (reply["orders"] as JsonArray) ?? [];
            foreach (var o in batch.ToList()) orders.Add(o?.DeepClone());
            if (batch.Count < OrderPage) break;
        }

        return orders;
    }

    private sealed record Row(JsonObject Raw, string Symbol, string Exchange, string Product, int LotSize, string VortexExchange);

    private async Task<Row> NormalizeAsync(JsonObject row, CancellationToken cancellationToken)
    {
        var vortex = Str(row["exchange"]) ?? "";
        var exchange = OpenAlgoExchange(vortex);
        var brokerSymbol = SplitTicker(Str(row["ticker"]));
        if (brokerSymbol.Length == 0) brokerSymbol = Str(row["symbol"]) ?? "";
        var known = await symbols.FindByBrokerSymbolAsync(Name, brokerSymbol, exchange, cancellationToken);
        var lot = Truthy(row["lot_size"]) ? (int)Int(row["lot_size"]) : known is { LotSize: > 0 } ? known.LotSize : 1;
        return new Row(row, known?.Symbol ?? brokerSymbol, exchange, ReverseProduct(exchange, Str(row["product"])) ?? Str(row["product"]) ?? "", lot, vortex);
    }

    private static string Status(string? raw) => (raw ?? "").Trim().ToUpperInvariant() switch
    {
        "EXECUTED" or "COMPLETE" => "complete", "PENDING" or "OPEN" or "OMSXMITTED" => "open", "TRIGGER PENDING" or "TRIGGER_PENDING" => "trigger pending",
        "CANCELLED" => "cancelled", "REJECTED" => "rejected", var s => s.ToLowerInvariant(),
    };

    public async Task<BrokerBook> GetOrderBookAsync(BrokerSession session, CancellationToken cancellationToken)
    {
        int buy = 0, sell = 0, completed = 0, open = 0, rejected = 0;
        var rows = new JsonArray();
        foreach (var o in (await OrderRowsAsync(session, cancellationToken)).OfType<JsonObject>())
        {
            var row = await NormalizeAsync(o, cancellationToken);
            var side = (Str(o["transaction_type"]) ?? "").ToUpperInvariant();
            if (side == "BUY") buy++;
            else if (side == "SELL") sell++;
            var status = Status(Str(o["status"]));
            if (status == "complete") completed++;
            else if (status is "open" or "trigger pending") open++;
            else if (status == "rejected") rejected++;
            rows.Add(new JsonObject
            {
                ["symbol"] = row.Symbol, ["exchange"] = row.Exchange, ["action"] = side, ["quantity"] = FromVortexQuantity(o["total_quantity"], row.VortexExchange, row.LotSize),
                ["price"] = F(o["order_price"]), ["trigger_price"] = F(o["trigger_price"]), ["pricetype"] = ReverseVariety(Str(o["variety"])), ["product"] = row.Product,
                ["orderid"] = Str(o["order_id"]) ?? "", ["order_status"] = status, ["timestamp"] = Str(o["order_created_at"]) ?? "",
            });
        }

        return new BrokerBook(rows, new JsonObject
        {
            ["total_buy_orders"] = buy, ["total_sell_orders"] = sell, ["total_completed_orders"] = completed, ["total_open_orders"] = open, ["total_rejected_orders"] = rejected,
        });
    }

    private static readonly string[] TradeFieldsFromOrder = ["ticker", "exchange", "transaction_type", "product", "variety", "lot_size"];

    public async Task<JsonArray> GetTradeBookAsync(BrokerSession session, CancellationToken cancellationToken)
    {
        var reply = await GetAsync(session, "/trading/trades", cancellationToken);
        var trades = ((reply["trades"] ?? reply["data"]) as JsonArray)?.OfType<JsonObject>().ToList() ?? [];
        // A trade can come with an order id and a price only; the rest is taken from its order, at the cost of one more call.
        if (trades.Count > 0 && trades.Any(t => !Truthy(t["ticker"])))
        {
            var orders = (await OrderRowsAsync(session, cancellationToken)).OfType<JsonObject>().Where(o => Truthy(o["order_id"])).GroupBy(o => Str(o["order_id"])!).ToDictionary(g => g.Key, g => g.First());
            foreach (var t in trades)
            {
                if (Str(t["order_id"]) is not { } id || !orders.TryGetValue(id, out var order)) continue;
                foreach (var field in TradeFieldsFromOrder)
                {
                    if (!Truthy(t[field]) && order[field] is { } value) t[field] = value.DeepClone();
                }
            }
        }

        var result = new JsonArray();
        foreach (var t in trades)
        {
            var row = await NormalizeAsync(t, cancellationToken);
            var units = FromVortexQuantity(t["trade_quantity"], row.VortexExchange, row.LotSize);
            var price = F(t["trade_price"]);
            var underlying = (await symbols.FindAsync(Name, row.Symbol, row.Exchange, cancellationToken))?.Name;
            result.Add(new JsonObject
            {
                ["symbol"] = row.Symbol, ["exchange"] = row.Exchange, ["product"] = row.Product, ["action"] = (Str(t["transaction_type"]) ?? "").ToUpperInvariant(), ["quantity"] = units,
                ["average_price"] = price, ["trade_value"] = BookFormatting.Round2(TradeValue(units, price, row.Exchange, underlying, row.LotSize)), ["orderid"] = Str(t["order_id"]) ?? "",
                ["timestamp"] = Str(t["traded_at"]) ?? "",
            });
        }

        return result;
    }

    /// <summary>The last price of each ticker, from batched quotes calls. A batch that fails leaves its tickers out, so a price is shown as unavailable rather than failing the book.</summary>
    private async Task<Dictionary<string, double>> LtpsAsync(BrokerSession session, IEnumerable<string?> tickers, CancellationToken cancellationToken)
    {
        var ltps = new Dictionary<string, double>();
        var all = tickers.Where(t => !string.IsNullOrEmpty(t)).Select(t => t!).Distinct().OrderBy(t => t, StringComparer.Ordinal).ToList();
        for (var i = 0; i < all.Count; i += LtpBatch)
        {
            var query = string.Join("&", all.Skip(i).Take(LtpBatch).Select(t => "q=" + Uri.EscapeDataString(t))) + "&mode=ltp";
            JsonObject reply;
            try
            {
                reply = await GetAsync(session, "/data/quotes?" + query, cancellationToken);
            }
            catch (BrokerException)
            {
                continue;
            }

            if (Str(reply["status"]) != "success") continue;
            foreach (var (ticker, quote) in (reply["data"] as JsonObject) ?? [])
            {
                var ltp = F((quote as JsonObject)?["last_trade_price"]);
                if (ltp != 0) ltps[ticker] = ltp;
            }
        }

        return ltps;
    }

    private static double OpenUnits(JsonObject p)
    {
        var quantity = F(p["quantity"]);
        if (quantity != 0 && Str(p["exchange"]) is "MCX_FO" or "NSE_CUR") quantity *= (Truthy(p["lot_size"]) ? F(p["lot_size"]) : 1) * (Truthy(p["multiplier"]) ? F(p["multiplier"]) : 1);
        return quantity;
    }

    /// <summary>The profit of a net position: what it has sold less what it has bought, plus the open quantity at the last price. Null for an open position with no price.</summary>
    private static double? PositionPnl(JsonObject p, double ltp)
    {
        var booked = F(p["sell_value"]) - F(p["buy_value"]);
        var open = OpenUnits(p);
        if (open == 0) return booked;
        return ltp == 0 ? null : booked + open * ltp;
    }

    private async Task<List<(JsonObject Raw, double Ltp)>> NetPositionsAsync(BrokerSession session, bool withLtp, CancellationToken cancellationToken)
    {
        var reply = await GetAsync(session, "/trading/portfolio/positions", cancellationToken);
        if (Str(reply["status"]) != "success") throw new BrokerException($"{DisplayName} could not send the positions: {Str(reply["message"]) ?? "no reason was given."}");
        var rows = ((reply["data"] as JsonObject)?["net"] as JsonArray)?.OfType<JsonObject>().ToList() ?? [];
        var ltps = withLtp ? await LtpsAsync(session, rows.Where(p => F(p["quantity"]) != 0).Select(p => Str(p["ticker"])), cancellationToken) : [];
        return rows.Select(p => (p, Str(p["ticker"]) is { } t && ltps.TryGetValue(t, out var ltp) ? ltp : F(p["ltp"]))).ToList();
    }

    public async Task<JsonArray> GetPositionsAsync(BrokerSession session, CancellationToken cancellationToken)
    {
        var result = new JsonArray();
        foreach (var (raw, ltp) in await NetPositionsAsync(session, true, cancellationToken))
        {
            var row = await NormalizeAsync(raw, cancellationToken);
            result.Add(new JsonObject
            {
                ["symbol"] = row.Symbol, ["exchange"] = row.Exchange, ["product"] = row.Product,
                ["quantity"] = FromVortexQuantity(raw["quantity"], row.VortexExchange, row.LotSize).ToString(CultureInfo.InvariantCulture),
                ["pnl"] = BookFormatting.Round2(PositionPnl(raw, ltp) ?? 0.0), ["average_price"] = F(raw["average_price"]).ToString("F2", CultureInfo.InvariantCulture), ["ltp"] = BookFormatting.Round2(ltp),
            });
        }

        return result;
    }

    private static JsonObject Listing(JsonObject holding)
    {
        foreach (var key in new[] { "nse", "bse" })
        {
            if (holding[key] is JsonObject listing && Truthy(listing["ticker"])) return listing;
        }

        return new JsonObject();
    }

    public async Task<BrokerBook> GetHoldingsAsync(BrokerSession session, CancellationToken cancellationToken)
    {
        var reply = await GetAsync(session, "/trading/portfolio/holdings", cancellationToken);
        if (Str(reply["status"]) != "success") throw new BrokerException($"{DisplayName} could not send the holdings: {Str(reply["message"]) ?? "no reason was given."}");
        var rows = (reply["data"] as JsonArray)?.OfType<JsonObject>().ToList() ?? [];
        var missing = rows.Where(h => F(h["last_price"]) == 0).ToList();
        if (missing.Count > 0)
        {
            var ltps = await LtpsAsync(session, missing.Select(h => Str(Listing(h)["ticker"])), cancellationToken);
            foreach (var h in missing)
            {
                if (Str(Listing(h)["ticker"]) is { } ticker && ltps.TryGetValue(ticker, out var ltp)) h["last_price"] = ltp;
            }
        }

        var holdings = new JsonArray();
        double value = 0, invested = 0, pnlTotal = 0;
        foreach (var h in rows)
        {
            var listing = Listing(h);
            var exchange = OpenAlgoExchange(Str(listing["exchange"]));
            var brokerSymbol = SplitTicker(Str(listing["ticker"]));
            var known = brokerSymbol.Length == 0 ? null : await symbols.FindByBrokerSymbolAsync(Name, brokerSymbol, exchange, cancellationToken);
            var symbol = known?.Symbol ?? (brokerSymbol.Length > 0 ? brokerSymbol : Str(h["isin"]) ?? "");
            var quantity = (long)Math.Truncate(F(h["total_free"]) + F(h["t1_quantity"]));
            var ltp = F(h["last_price"]);
            var average = F(h["average_price"]);
            var pnl = ltp != 0 ? (ltp - average) * quantity : 0.0;
            value += ltp * quantity;
            invested += average * quantity;
            pnlTotal += pnl;
            holdings.Add(new JsonObject
            {
                ["symbol"] = symbol, ["exchange"] = exchange, ["quantity"] = quantity, ["product"] = "CNC", ["average_price"] = average, ["ltp"] = ltp,
                ["pnl"] = BookFormatting.Round2(pnl), ["pnlpercent"] = average != 0 && ltp != 0 ? BookFormatting.Round2((ltp - average) / average * 100) : 0.0,
            });
        }

        return new BrokerBook(holdings, new JsonObject
        {
            ["totalholdingvalue"] = BookFormatting.Round2(value), ["totalinvvalue"] = BookFormatting.Round2(invested), ["totalprofitandloss"] = BookFormatting.Round2(pnlTotal),
            ["totalpnlpercentage"] = invested != 0 ? BookFormatting.Round2(pnlTotal / invested * 100) : 0.0,
        });
    }

    // ---- funds -----------------------------------------------------------------------------------------------

    public async Task<JsonObject> GetFundsAsync(BrokerSession session, CancellationToken cancellationToken)
    {
        var reply = await GetAsync(session, "/user/funds", cancellationToken);
        var body = reply["data"] as JsonObject ?? reply;
        if (Str(reply["status"]) == "error" || !new[] { "exchange_combined", "nse", "mcx" }.Any(body.ContainsKey))
            throw new BrokerException($"{DisplayName} could not report funds: {Str(reply["message"]) ?? "try again in a moment."}");
        var combined = body["exchange_combined"] as JsonObject;
        var buckets = combined is not null ? new List<JsonObject> { combined } : new List<JsonObject> { body["nse"] as JsonObject ?? [], body["mcx"] as JsonObject ?? [] };
        double Total(string key) => buckets.Sum(b => F(b[key]));

        // The day's profit is added up over the position book at live prices, so the two parts always add up to its net profit; a position whose split is unknown is left out.
        double realised = 0, unrealised = 0;
        try
        {
            foreach (var (raw, ltp) in await NetPositionsAsync(session, true, cancellationToken))
            {
                var total = PositionPnl(raw, ltp);
                if (total is null) continue;
                var open = OpenUnits(raw);
                if (open == 0)
                {
                    realised += total.Value;
                    continue;
                }

                var average = F(raw["average_price"]);
                if (average == 0) continue;
                var u = open * (ltp - average);
                realised += total.Value - u;
                unrealised += u;
            }
        }
        catch (BrokerException)
        {
            realised = Total("booked_profit") + Total("mtm_and_booked_loss");
            unrealised = 0;
        }

        return new JsonObject
        {
            ["availablecash"] = BookFormatting.Fixed2(Total("net_available")), ["collateral"] = BookFormatting.Fixed2(Total("collateral")), ["m2munrealized"] = BookFormatting.Fixed2(unrealised),
            ["m2mrealized"] = BookFormatting.Fixed2(realised), ["utiliseddebits"] = BookFormatting.Fixed2(Math.Abs(Total("total_utilization"))),
        };
    }
}
