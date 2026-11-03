using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using EPTradeBot.Core.Brokers;
using EPTradeBot.Core.Time;
using Microsoft.Extensions.Logging;
using static EPTradeBot.Infrastructure.Brokers.JsonRead;

namespace EPTradeBot.Infrastructure.Brokers.Delta;

/// <summary>
/// Delta Exchange India (crypto derivatives and spot, exchange CRYPTO). Port of
/// broker/deltaexchange (api/order_api.py, api/funds.py, api/data.py quotes and depth,
/// mapping/transform_data.py, mapping/order_data.py), held to
/// tools/contract-fixtures/deltaexchange.fixtures.json. Private requests are signed with
/// HMAC-SHA256 over method, timestamp, path, query and body. Order ids are
/// "product_id:order_id", as OpenAlgo forms them, so a cancel needs no lookup.
/// Not ported yet, and refused rather than guessed: fractional (spot) quantities,
/// leverage setting, bracket orders, history, margin, login and the Delta feed.
/// </summary>
[BrokerPlugin]
public sealed class DeltaExchangePlugin(
    IHttpClientFactory http,
    BrokerSessionFactory sessions,
    ISymbolMaster symbols,
    TimeProvider clock,
    ILogger<DeltaExchangePlugin> logger) : IBrokerOrders, IBrokerPortfolio, IBrokerFunds, IBrokerQuotes
{
    private const string BaseUrl = "https://api.india.delta.exchange";
    private const string Crypto = "CRYPTO";

    public string Name => "deltaexchange";
    public string DisplayName => "Delta Exchange";
    public BrokerCapability Capabilities => BrokerCapability.Orders | BrokerCapability.Portfolio | BrokerCapability.Funds | BrokerCapability.Quotes | BrokerCapability.Depth;

    /// <summary>The session token is the Delta API key (as OpenAlgo stores it); ApiSecret signs every request.</summary>
    public IReadOnlyList<string> RequiredKeys { get; } = ["ApiSecret"];

    /// <summary>The prehash Delta signs: METHOD + timestamp + path + query (with its "?") + body.</summary>
    public static string Sign(string secret, string method, string timestamp, string path, string query, string body) =>
        Convert.ToHexStringLower(HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes(method + timestamp + path + query + body)));

    private static string OrderType(string priceType) => priceType is "LIMIT" or "SL" ? "limit_order" : "market_order";

    private static string Text(decimal value) => value.ToString(CultureInfo.InvariantCulture);

    // ---- orders ----------------------------------------------------------------------------------------------

    public async Task<BrokerCallResult> PlaceAsync(BrokerSession session, BrokerOrder order, CancellationToken cancellationToken)
    {
        var instrument = await symbols.FindAsync(Name, order.Symbol, order.Exchange, cancellationToken);
        if (instrument is null) return BrokerCallResult.Refused(400, $"{order.Symbol} is not in the Delta Exchange master contract. Check the symbol, or download the master contract again.");
        var type = OrderType(order.PriceType);
        var body = new JsonObject
        {
            ["product_id"] = long.Parse(instrument.Token, CultureInfo.InvariantCulture), ["product_symbol"] = instrument.BrokerSymbol,
            ["size"] = order.Quantity, ["side"] = order.Action.ToLowerInvariant(), ["order_type"] = type, ["time_in_force"] = "gtc",
        };
        if (type == "limit_order") body["limit_price"] = order.Price != 0 ? Text(order.Price) : "0";
        if (order.PriceType is "SL" or "SL-M")
        {
            body["stop_order_type"] = "stop_loss_order";
            body["stop_price"] = order.TriggerPrice != 0 ? Text(order.TriggerPrice) : "0";
            body["stop_trigger_method"] = "last_traded_price";
        }

        HttpResponseMessage response;
        using var request = Signed(session, HttpMethod.Post, "/v2/orders", "", body);
        try
        {
            response = await Client().SendAsync(request, cancellationToken);
        }
        catch (HttpRequestException ex)
        {
            logger.LogWarning(ex, "Delta Exchange order POST got no answer.");
            return new BrokerCallResult(false, 502,
                "The order was sent to Delta Exchange but no reply came back, so it may have been placed. Check your orders on Delta Exchange before trying again.", MayHaveBeenSent: true);
        }

        using (response)
        {
            var reply = await ReadAsync(response, cancellationToken) as JsonObject;
            if (Truthy(reply?["success"]) && reply!["result"] is JsonObject result)
                return BrokerCallResult.Success($"{Str(result["product_id"]) ?? instrument.Token}:{Str(result["id"])}");
            return BrokerCallResult.Refused(400, ErrorText(reply));
        }
    }

    public async Task<BrokerCallResult> ModifyAsync(BrokerSession session, BrokerOrderChange change, CancellationToken cancellationToken)
    {
        var (productId, orderId) = await SplitAsync(change.OrderId, change.Symbol, change.Exchange, cancellationToken);
        var body = new JsonObject
        {
            ["id"] = orderId, ["product_id"] = productId, ["size"] = change.Quantity,
            ["limit_price"] = change.PriceType == "SL-M" ? "0" : Text(change.Price),
        };
        if (change.PriceType is "SL" or "SL-M") body["stop_price"] = change.TriggerPrice != 0 ? Text(change.TriggerPrice) : "0";
        using var request = Signed(session, HttpMethod.Put, "/v2/orders", "", body);
        using var response = await SendAsync(request, cancellationToken);
        var reply = await ReadAsync(response, cancellationToken) as JsonObject;
        return Truthy(reply?["success"]) ? BrokerCallResult.Success(change.OrderId) : BrokerCallResult.Refused(400, ErrorText(reply));
    }

    public async Task<BrokerCallResult> CancelAsync(BrokerSession session, string orderId, CancellationToken cancellationToken)
    {
        var parts = orderId.Split(':', 2);
        var body = parts.Length == 2 && long.TryParse(parts[0], out var pid) && long.TryParse(parts[1], out var oid)
            ? new JsonObject { ["id"] = oid, ["product_id"] = pid }
            : long.TryParse(orderId, out var bare) ? new JsonObject { ["id"] = bare } : null;
        if (body is null) return BrokerCallResult.Refused(400, $"{orderId} is not a Delta Exchange order id.");
        using var request = Signed(session, HttpMethod.Delete, "/v2/orders", "", body);
        using var response = await SendAsync(request, cancellationToken);
        var reply = await ReadAsync(response, cancellationToken) as JsonObject;
        return Truthy(reply?["success"]) ? BrokerCallResult.Success(orderId) : BrokerCallResult.Refused(400, ErrorText(reply));
    }

    private async Task<(long ProductId, long OrderId)> SplitAsync(string orderId, string symbol, string exchange, CancellationToken cancellationToken)
    {
        var parts = orderId.Split(':', 2);
        if (parts.Length == 2) return (long.Parse(parts[0], CultureInfo.InvariantCulture), long.Parse(parts[1], CultureInfo.InvariantCulture));
        var token = (await symbols.FindAsync(Name, symbol, exchange, cancellationToken))?.Token ?? "0";
        return (long.Parse(token, CultureInfo.InvariantCulture), long.Parse(orderId, CultureInfo.InvariantCulture));
    }

    /// <summary>Delta's error: its message, else its code.</summary>
    private static string ErrorText(JsonObject? reply) =>
        Str(reply?["error"]?["message"]) ?? Str(reply?["error"]?["code"]) ?? "Delta Exchange refused the request.";

    // ---- books -----------------------------------------------------------------------------------------------

    /// <summary>Delta keeps every order; the books show only today's, by the IST calendar day.</summary>
    private bool Today(JsonNode? createdAt)
    {
        if (Str(createdAt) is not { Length: >= 19 } text
            || !DateTime.TryParseExact(text[..19], "yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var utc))
            return false;
        return Ist.TradingDay(utc) == Ist.TradingDay(clock.GetUtcNow().UtcDateTime);
    }

    private async Task<JsonNode?> SymbolOfAsync(JsonObject row, CancellationToken cancellationToken)
    {
        var token = Str(row["product_id"]);
        var record = string.IsNullOrEmpty(token) ? null : await symbols.FindByTokenAsync(Name, token, Crypto, cancellationToken);
        return record?.Symbol ?? Get(row, "product_symbol", "");
    }

    private static string CompositeId(JsonObject row, string idKey)
    {
        var pid = Str(row["product_id"]);
        return string.IsNullOrEmpty(pid) ? Str(row[idKey]) ?? "" : $"{pid}:{Str(row[idKey])}";
    }

    public async Task<BrokerBook> GetOrderBookAsync(BrokerSession session, CancellationToken cancellationToken)
    {
        var rows = new List<JsonObject>();
        rows.AddRange(await ResultListAsync(session, "/v2/orders", "?state=open", cancellationToken));
        rows.AddRange(await ResultListAsync(session, "/v2/orders/history", "", cancellationToken));
        int buy = 0, sell = 0, completed = 0, open = 0, rejected = 0;
        var orders = new JsonArray();
        foreach (var o in rows.Where(r => Today(r["created_at"])))
        {
            var side = (Str(o["side"]) ?? "").ToUpperInvariant();
            if (side == "BUY") buy++;
            else if (side == "SELL") sell++;
            var state = (Str(o["state"]) ?? "").ToLowerInvariant();
            var status = state switch { "open" or "pending" => "open", "closed" or "filled" => "complete", "cancelled" => "cancelled", _ => state };
            if (status == "complete") completed++;
            else if (status == "open") open++;
            else if (status == "rejected") rejected++;
            var raw = Str(o["order_type"]) ?? "";
            var type = Str(o["stop_order_type"]) == "stop_loss_order"
                ? raw == "limit_order" ? "SL" : "SL-M"
                : raw switch { "limit_order" => "LIMIT", "market_order" => "MARKET", _ => raw.ToUpperInvariant() };
            orders.Add(new JsonObject
            {
                ["symbol"] = await SymbolOfAsync(o, cancellationToken), ["exchange"] = Crypto, ["action"] = side,
                ["quantity"] = Get(o, "size", 0), ["price"] = Float(o["limit_price"]), ["trigger_price"] = Float(o["stop_price"]),
                ["pricetype"] = type, ["product"] = "NRML", ["orderid"] = CompositeId(o, "id"), ["order_status"] = status,
                ["timestamp"] = Get(o, "created_at", ""),
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
        foreach (var t in (await ResultListAsync(session, "/v2/fills", "", cancellationToken)).Where(r => Today(r["created_at"])))
        {
            var quantity = Float(t["size"]);
            var price = Float(t["price"]);
            trades.Add(new JsonObject
            {
                ["symbol"] = await SymbolOfAsync(t, cancellationToken), ["exchange"] = Crypto, ["product"] = "NRML",
                ["action"] = (Str(t["side"]) ?? "").ToUpperInvariant(), ["quantity"] = quantity, ["average_price"] = price,
                ["trade_value"] = quantity * price, ["orderid"] = CompositeId(t, "order_id"), ["timestamp"] = Get(t, "created_at", ""),
            });
        }

        return trades;
    }

    public async Task<JsonArray> GetPositionsAsync(BrokerSession session, CancellationToken cancellationToken)
    {
        var positions = new JsonArray();
        foreach (var p in await ResultListAsync(session, "/v2/positions/margined", "", cancellationToken))
        {
            var token = Str(p["product_id"]);
            var record = string.IsNullOrEmpty(token) ? null : await symbols.FindByTokenAsync(Name, token, Crypto, cancellationToken);
            positions.Add(Position(record?.Symbol ?? Str(p["product_symbol"]) ?? "", "NRML", Float(p["size"]), Float(p["entry_price"]),
                Float(p["realized_pnl"]) + Float(p["unrealized_pnl"]), record is null ? 1.0 : record.ContractValue ?? 1.0));
        }

        // Spot balances other than INR are shown as positions, as OpenAlgo does.
        foreach (var asset in await ResultListAsync(session, "/v2/wallet/balances", "", cancellationToken))
        {
            var currency = Str(asset["asset_symbol"]) ?? Str(asset["symbol"]) ?? "";
            if (currency is "" or "INR" or "USD") continue;
            var size = Float(asset["balance"]) - Float(asset["blocked_margin"]);
            if (size <= 0) continue;
            var spot = $"{currency}_INR";
            var record = await symbols.FindByBrokerSymbolAsync(Name, spot, Crypto, cancellationToken);
            positions.Add(Position(record?.Symbol ?? spot, "CNC", size, 0, 0, record is null ? 1.0 : record.ContractValue ?? 1.0));
        }

        return positions;
    }

    private static JsonObject Position(string symbol, string product, double quantity, double average, double pnl, double lotSize) => new()
    {
        ["symbol"] = symbol, ["exchange"] = Crypto, ["product"] = product, ["quantity"] = quantity, ["average_price"] = average,
        // Delta's position book carries no last price; the risk check values an unpriced position at its average.
        ["ltp"] = 0.0, ["pnl"] = pnl, ["lot_size"] = lotSize,
    };

    /// <summary>Delta has no equity holdings; spot balances appear as positions.</summary>
    public Task<BrokerBook> GetHoldingsAsync(BrokerSession session, CancellationToken cancellationToken) =>
        Task.FromResult(new BrokerBook([], new JsonObject { ["totalholdingvalue"] = 0, ["totalinvvalue"] = 0, ["totalprofitandloss"] = 0, ["totalpnlpercentage"] = 0 }));

    // ---- funds -----------------------------------------------------------------------------------------------

    public async Task<JsonObject> GetFundsAsync(BrokerSession session, CancellationToken cancellationToken)
    {
        double balance = 0, blocked = 0, collateral = 0;
        foreach (var asset in await ResultListAsync(session, "/v2/wallet/balances", "", cancellationToken))
        {
            balance += Float(asset["balance_inr"]);
            blocked += Float(asset["blocked_margin"]);
            collateral += Float(asset["cross_locked_collateral"]);
        }

        double realised = 0, unrealised = 0;
        try
        {
            foreach (var p in await ResultListAsync(session, "/v2/positions/margined", "", cancellationToken))
            {
                realised += Float(p["realized_pnl"]);
                unrealised += Float(p["unrealized_pnl"]);
            }
        }
        catch (BrokerException ex)
        {
            logger.LogWarning(ex, "Could not read Delta Exchange positions for P&L.");
        }

        return new JsonObject
        {
            ["availablecash"] = BookFormatting.Fixed2(balance), ["collateral"] = BookFormatting.Fixed2(collateral),
            ["m2mrealized"] = BookFormatting.Fixed2(realised), ["m2munrealized"] = BookFormatting.Fixed2(unrealised),
            ["utiliseddebits"] = BookFormatting.Fixed2(blocked),
        };
    }

    // ---- market data (public, unsigned) ----------------------------------------------------------------------

    private async Task<JsonObject> PublicAsync(string path, string what, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, sessions.BaseUrl(Name, BaseUrl) + path);
        request.Headers.TryAddWithoutValidation("Accept", "application/json");
        using var response = await SendAsync(request, cancellationToken);
        var reply = await ReadAsync(response, cancellationToken) as JsonObject;
        if (!response.IsSuccessStatusCode || !Truthy(reply?["success"]) || reply!["result"] is not JsonObject result)
            throw new BrokerException(response.StatusCode == HttpStatusCode.NotFound
                ? $"Delta Exchange has no {what} for this contract."
                : $"Delta Exchange could not send the {what} right now. Try again in a moment.");
        return result;
    }

    private async Task<JsonObject> TickerAsync(string symbol, string exchange, CancellationToken cancellationToken)
    {
        var br = (await symbols.FindAsync(Name, symbol, exchange, cancellationToken))?.BrokerSymbol ?? symbol;
        return await PublicAsync($"/v2/tickers/{Uri.EscapeDataString(br)}", "ticker", cancellationToken);
    }

    public async Task<JsonObject> GetQuoteAsync(BrokerSession session, string symbol, string exchange, CancellationToken cancellationToken)
    {
        var t = await TickerAsync(symbol, exchange, cancellationToken);
        var quotes = t["quotes"];
        return new JsonObject
        {
            ["ltp"] = Float(t["mark_price"]), ["open"] = Float(t["open"]), ["high"] = Float(t["high"]), ["low"] = Float(t["low"]),
            ["volume"] = Int(t["volume"]), ["prev_close"] = Float(t["close"]), ["oi"] = Float(t["oi"]),
            ["bid"] = Float(quotes?["best_bid"]), ["ask"] = Float(quotes?["best_ask"]),
        };
    }

    public async Task<JsonObject> GetDepthAsync(BrokerSession session, string symbol, string exchange, CancellationToken cancellationToken)
    {
        var t = await TickerAsync(symbol, exchange, cancellationToken);
        var productId = Int(t["product_id"]);
        // OpenAlgo shows an empty book when the order book cannot be read; here the reader is told it is unavailable.
        if (productId == 0) throw new BrokerException("Delta Exchange did not name this contract's order book. Try again in a moment.");
        var book = await PublicAsync($"/v2/l2orderbook/{productId}", "order book", cancellationToken);
        static (JsonArray Levels, long Total) Side(JsonNode? side)
        {
            var levels = new JsonArray();
            long total = 0;
            foreach (var level in (side as JsonArray ?? []).OfType<JsonObject>().Take(5))
            {
                var qty = Int(level["size"]);
                total += qty;
                levels.Add(new JsonObject { ["price"] = Float(level["price"]), ["quantity"] = qty });
            }

            while (levels.Count < 5) levels.Add(new JsonObject { ["price"] = 0.0, ["quantity"] = 0 });
            return (levels, total);
        }

        var (bids, buyTotal) = Side(book["buy"]);
        var (asks, sellTotal) = Side(book["sell"]);
        return new JsonObject
        {
            ["bids"] = bids, ["asks"] = asks, ["ltp"] = Float(t["mark_price"]), ["ltq"] = 0, ["volume"] = Int(t["volume"]),
            ["open"] = Float(t["open"]), ["high"] = Float(t["high"]), ["low"] = Float(t["low"]), ["prev_close"] = Float(t["close"]),
            ["oi"] = Float(t["oi"]), ["totalbuyqty"] = buyTotal, ["totalsellqty"] = sellTotal,
        };
    }

    // ---- plumbing --------------------------------------------------------------------------------------------

    private HttpClient Client() => http.CreateClient(Name);

    private HttpRequestMessage Signed(BrokerSession session, HttpMethod method, string path, string query, JsonObject? body)
    {
        var text = body?.ToJsonString() ?? "";
        var timestamp = clock.GetUtcNow().ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);
        var request = new HttpRequestMessage(method, sessions.BaseUrl(Name, BaseUrl) + path + query);
        request.Headers.TryAddWithoutValidation("api-key", session.AuthToken);
        request.Headers.TryAddWithoutValidation("timestamp", timestamp);
        request.Headers.TryAddWithoutValidation("signature", Sign(session.Key("ApiSecret") ?? "", method.Method, timestamp, path, query, text));
        request.Headers.TryAddWithoutValidation("User-Agent", "ep-trade-bot");
        request.Headers.TryAddWithoutValidation("Accept", "application/json");
        if (body is not null)
        {
            request.Content = new StringContent(text, Encoding.UTF8);
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
            throw new BrokerException("Delta Exchange did not answer. Check your connection, then try again.", 502, ex);
        }
    }

    private async Task<List<JsonObject>> ResultListAsync(BrokerSession session, string path, string query, CancellationToken cancellationToken)
    {
        using var request = Signed(session, HttpMethod.Get, path, query, null);
        using var response = await SendAsync(request, cancellationToken);
        var reply = await ReadAsync(response, cancellationToken) as JsonObject;
        if (response.IsSuccessStatusCode && Truthy(reply?["success"])) return (reply!["result"] as JsonArray ?? []).OfType<JsonObject>().ToList();
        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            throw new BrokerException("Delta Exchange refused the API key. Check the key and secret, or create new ones on Delta Exchange.", 403);
        throw new BrokerException($"Delta Exchange could not answer: {ErrorText(reply)}");
    }
}
