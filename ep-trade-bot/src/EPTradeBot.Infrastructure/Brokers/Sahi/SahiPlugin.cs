using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using EPTradeBot.Core.Brokers;
using static EPTradeBot.Infrastructure.Brokers.JsonRead;

namespace EPTradeBot.Infrastructure.Brokers.Sahi;

/// <summary>
/// Sahi (direct.sahi.com, /v1). Port of broker/sahi: api/order_api.py, api/funds.py, mapping/transform_data.py and mapping/order_data.py. Prices travel as integer paise
/// and quantities as units. Sahi has no day-wide trade book, so it is gathered from the trades of every order that filled; positions and holdings carry no last price,
/// so it is read from the last-price call, up to 100 instruments at a time. A request Sahi answers with 429 is repeated (3 times) because a 429 is decided before
/// anything is placed. Not ported yet: margin, sign-in and the feed.
/// </summary>
[BrokerPlugin]
public sealed partial class SahiPlugin(IHttpClientFactory http, BrokerSessionFactory sessions, ISymbolMaster symbols, TimeProvider clock) : IBrokerOrders, IBrokerPortfolio, IBrokerFunds, IBrokerMultiQuotes, IBrokerHistory
{
    private const string DefaultBaseUrl = "https://direct.sahi.com";
    private const int LtpBatch = 100;
    private const int MaxRetries = 3;

    public string Name => "sahi";
    public string DisplayName => "Sahi";
    public BrokerCapability Capabilities => BrokerCapability.Orders | BrokerCapability.Portfolio | BrokerCapability.Funds | BrokerCapability.Quotes | BrokerCapability.Depth | BrokerCapability.History;
    public IReadOnlyList<string> RequiredKeys { get; } = ["ApiKey", "ApiSecret"];

    private static readonly TimeSpan Ist = TimeSpan.FromHours(5.5);

    // ---- vocabulary ------------------------------------------------------------------------------------------

    private static readonly HashSet<string> Segments = ["NSE", "BSE", "NFO", "BFO"];

    private static string? ProductCode(string? product) => (product ?? "").ToUpperInvariant() switch { "CNC" => "C", "MIS" => "I", "NRML" => "M", _ => null };

    private static string ReverseProduct(string? code) => code switch { "C" => "CNC", "I" => "MIS", "M" => "NRML", "F" => "MTF", _ => code ?? "" };

    private static string? OrderType(string? priceType) => (priceType ?? "").ToUpperInvariant() switch { "MARKET" => "MKT", "LIMIT" => "LMT", "SL" => "SL-LMT", "SL-M" => "SL-MKT", _ => null };

    private static string ReverseOrderType(string? type) => type switch { "MKT" => "MARKET", "LMT" => "LIMIT", "SL-LMT" => "SL", "SL-MKT" => "SL-M", _ => type ?? "" };

    private static string ReverseAction(string? type) => type switch { "B" => "BUY", "S" => "SELL", _ => type ?? "" };

    private static string Status(string? raw) => raw switch
    {
        "Open" or "OpenPartial" => "open", "PartialFilled" => "cancelled", "Completed" => "complete", "Cancelled" => "cancelled", "Rejected" => "rejected",
        null or "" => "open", var s => s.ToLowerInvariant(),
    };

    /// <summary>Rupees as integer paise, half up.</summary>
    private static long Paise(decimal value) => (long)Math.Round(value * 100m, 0, MidpointRounding.AwayFromZero);

    private static double FromPaise(JsonNode? paise) => paise is null || Str(paise) == "" ? 0.0 : BookFormatting.Round2(Float(paise) / 100.0);

    private static string Stamp(JsonNode? epochMs)
    {
        if (!Truthy(epochMs)) return "";
        try
        {
            return DateTimeOffset.FromUnixTimeMilliseconds(Int(epochMs)).ToOffset(Ist).ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
        }
        catch (ArgumentOutOfRangeException)
        {
            return "";
        }
    }

    // ---- calls -----------------------------------------------------------------------------------------------

    private HttpClient Client() => http.CreateClient(Name);

    /// <summary>One call. A 429 is repeated up to three times, after Retry-After seconds or 1, 2, 4. Returns the status and the JSON object ({} when there is none).</summary>
    private async Task<(int Status, JsonObject Body)> CallAsync(BrokerSession session, HttpMethod method, string path, JsonObject? body, CancellationToken cancellationToken)
    {
        for (var attempt = 0; ; attempt++)
        {
            using var request = new HttpRequestMessage(method, sessions.BaseUrl(Name, DefaultBaseUrl) + path);
            request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {session.AuthToken}");
            request.Headers.TryAddWithoutValidation("Accept", "application/json");
            request.Headers.TryAddWithoutValidation("User-Agent", "ep-trade-bot/1.0");
            if (body is not null) request.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
            using var response = await Client().SendAsync(request, cancellationToken);
            var status = (int)response.StatusCode;
            if (status == 429 && attempt < MaxRetries)
            {
                await Task.Delay(sessions.RetryBackoff(Name, RetryAfter(response, attempt)), cancellationToken);
                continue;
            }

            var text = await response.Content.ReadAsStringAsync(cancellationToken);
            if (text.Length == 0) return (status, []);
            try
            {
                return (status, JsonNode.Parse(text) as JsonObject ?? []);
            }
            catch (JsonException)
            {
                return (status, new JsonObject { ["message"] = text.Length > 200 ? text[..200] : text });
            }
        }
    }

    private static TimeSpan RetryAfter(HttpResponseMessage response, int attempt)
    {
        var delay = TimeSpan.FromSeconds(1 << attempt);
        if (response.Headers.RetryAfter is { } after)
        {
            if (after.Delta is { } d) delay = TimeSpan.FromSeconds(Math.Max(d.TotalSeconds, 0.05));
            else if (after.Date is { } date) delay = TimeSpan.FromSeconds(Math.Max((date - DateTimeOffset.UtcNow).TotalSeconds, 0.05));
        }

        return delay;
    }

    /// <summary>A Sahi refusal in words a trader can act on.</summary>
    private static string ErrorMessage(int status, JsonObject body)
    {
        var code = Str(body["code"]);
        var message = Str(body["message"]);
        if (code == "IP_NOT_ALLOWED")
            return "Sahi refused the order because it did not come from your registered static IP. Check the IP in Sahi web, Profile, API Dashboard, and that EP Trade bot runs on that machine.";
        if (status == 401 || code == "UNAUTHORIZED") return "Your Sahi session has expired. Log in to EP Trade bot again.";
        if (code == "FORBIDDEN") return "Your Sahi API key does not have permission for this. Create a key with Full access in the Sahi API Dashboard and use it in EP Trade bot.";
        if (code == "RATE_LIMITED" || status == 429) return "Sahi is limiting requests right now. Wait a few seconds and try again.";
        if (!string.IsNullOrEmpty(message)) return message;
        if (status >= 500) return "Sahi is not responding right now. Try again in a moment.";
        return "Sahi did not accept the request.";
    }

    private async Task<(int Status, JsonObject Body)> TryCallAsync(BrokerSession session, HttpMethod method, string path, JsonObject? body, CancellationToken cancellationToken)
    {
        try
        {
            return await CallAsync(session, method, path, body, cancellationToken);
        }
        catch (HttpRequestException ex)
        {
            throw new BrokerException("Could not reach Sahi. Check your connection.", 500, ex);
        }
    }

    /// <summary>A book: the object when Sahi answers 200 with the key, else the refusal as an exception.</summary>
    private async Task<JsonObject> ReadAsync(BrokerSession session, string path, string key, CancellationToken cancellationToken)
    {
        var (status, body) = await TryCallAsync(session, HttpMethod.Get, path, null, cancellationToken);
        if (status == 200 && body.ContainsKey(key)) return body;
        throw new BrokerException(ErrorMessage(status, body), 500);
    }

    /// <summary>The last price in rupees for each instrument id, read in batches of 100. A batch that fails is left out.</summary>
    private async Task<Dictionary<long, double>> LtpMapAsync(BrokerSession session, IEnumerable<long> ids, CancellationToken cancellationToken)
    {
        var ltp = new Dictionary<long, double>();
        var all = ids.Distinct().Order().ToList();
        for (var i = 0; i < all.Count; i += LtpBatch)
        {
            var query = string.Join("&", all.Skip(i).Take(LtpBatch).Select(id => "i=" + id.ToString(CultureInfo.InvariantCulture)));
            (int Status, JsonObject Body) reply;
            try
            {
                reply = await CallAsync(session, HttpMethod.Get, "/v1/marketdata/ltp?" + query, null, cancellationToken);
            }
            catch (HttpRequestException)
            {
                continue;
            }

            if (reply.Status != 200) continue;
            foreach (var (key, value) in (reply.Body["data"] as JsonObject) ?? [])
            {
                if (value is JsonObject v && v["last_trade_price"] is { } price && long.TryParse(key, out var id)) ltp[id] = FromPaise(price);
            }
        }

        return ltp;
    }

    private async Task<string> OaSymbolAsync(string? brokerSymbol, string segment, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(brokerSymbol)) return "";
        var known = await symbols.FindByBrokerSymbolAsync(Name, brokerSymbol, segment, cancellationToken);
        return known?.Symbol is { Length: > 0 } s ? s : brokerSymbol;
    }

    // ---- orders ----------------------------------------------------------------------------------------------

    private async Task<(JsonObject? Payload, string? Refusal)> OrderPayloadAsync(string exchange, string symbol, string action, int quantity, string priceType, string product, decimal price, decimal trigger, int disclosed, CancellationToken cancellationToken)
    {
        var segment = exchange.ToUpperInvariant();
        if (!Segments.Contains(segment)) return (null, $"Sahi does not take orders on {segment}.");
        var instrument = await symbols.FindAsync(Name, symbol, exchange, cancellationToken);
        if (instrument is null || string.IsNullOrEmpty(instrument.BrokerSymbol))
            return (null, $"{symbol} was not found on {exchange} in the Sahi master contract. Check the symbol, or download the master contract again.");
        if (ProductCode(product) is not { } code) return (null, $"Product {product} is not supported. Use CNC, MIS or NRML.");
        if (OrderType(priceType) is not { } type) return (null, $"Price type {priceType} is not supported. Use MARKET, LIMIT, SL or SL-M.");
        var side = action.ToUpperInvariant() switch { "BUY" => "B", "SELL" => "S", _ => null };
        if (side is null) return (null, $"Action {action} is not supported. Use BUY or SELL.");

        var payload = new JsonObject
        {
            ["exchange_segment"] = segment, ["symbol"] = instrument.BrokerSymbol, ["txn_type"] = side, ["qty"] = quantity, ["order_type"] = type, ["product"] = code, ["validity"] = "DAY",
            ["price"] = type is "MKT" or "SL-MKT" ? 0 : Paise(price),
        };
        if (type is "SL-LMT" or "SL-MKT") payload["trigger_price"] = Paise(trigger);
        if (disclosed != 0) payload["disclosed_qty"] = disclosed;
        return (payload, null);
    }

    public async Task<BrokerCallResult> PlaceAsync(BrokerSession session, BrokerOrder order, CancellationToken cancellationToken)
    {
        var (payload, refusal) = await OrderPayloadAsync(order.Exchange, order.Symbol, order.Action, order.Quantity, order.PriceType, order.Product, order.Price, order.TriggerPrice, order.DisclosedQuantity, cancellationToken);
        if (payload is null) return BrokerCallResult.Refused(400, refusal!);

        (int Status, JsonObject Body) reply;
        try
        {
            reply = await CallAsync(session, HttpMethod.Post, "/v1/orders", payload, cancellationToken);
        }
        catch (HttpRequestException)
        {
            return new BrokerCallResult(false, 502, $"The order was sent to {DisplayName} but no reply came back, so it may have been placed. Check the order book on {DisplayName} before trying again.", MayHaveBeenSent: true);
        }

        if (reply.Status is 200 or 201 && reply.Body["order_ids"] is JsonArray { Count: > 0 } ids && Str(ids[0]) is { } id) return BrokerCallResult.Success(id);
        return BrokerCallResult.Refused(reply.Status >= 400 ? reply.Status : 500, ErrorMessage(reply.Status, reply.Body));
    }

    public async Task<BrokerCallResult> ModifyAsync(BrokerSession session, BrokerOrderChange change, CancellationToken cancellationToken)
    {
        if (OrderType(change.PriceType) is not { } type) return BrokerCallResult.Refused(400, $"Price type {change.PriceType} is not supported. Use MARKET, LIMIT, SL or SL-M.");
        var payload = new JsonObject { ["qty"] = change.Quantity, ["order_type"] = type, ["validity"] = "DAY", ["price"] = type is "MKT" or "SL-MKT" ? 0 : Paise(change.Price) };
        if (type is "SL-LMT" or "SL-MKT") payload["trigger_price"] = Paise(change.TriggerPrice);
        payload["disclosed_qty"] = change.DisclosedQuantity;

        (int Status, JsonObject Body) reply;
        try
        {
            reply = await CallAsync(session, HttpMethod.Put, $"/v1/orders/{Uri.EscapeDataString(change.OrderId)}", payload, cancellationToken);
        }
        catch (HttpRequestException)
        {
            return BrokerCallResult.Refused(500, "Could not reach Sahi to modify the order.");
        }

        if (reply.Status == 200) return BrokerCallResult.Success(Truthy(reply.Body["order_id"]) ? Str(reply.Body["order_id"])! : change.OrderId);
        return BrokerCallResult.Refused(reply.Status >= 400 ? reply.Status : 500, ErrorMessage(reply.Status, reply.Body));
    }

    public async Task<BrokerCallResult> CancelAsync(BrokerSession session, string orderId, CancellationToken cancellationToken)
    {
        (int Status, JsonObject Body) reply;
        try
        {
            reply = await CallAsync(session, HttpMethod.Delete, $"/v1/orders/{Uri.EscapeDataString(orderId)}", null, cancellationToken);
        }
        catch (HttpRequestException)
        {
            return BrokerCallResult.Refused(500, "Could not reach Sahi to cancel the order.");
        }

        if (reply.Status is 200 or 204) return BrokerCallResult.Success(orderId);
        return BrokerCallResult.Refused(reply.Status >= 400 ? reply.Status : 500, ErrorMessage(reply.Status, reply.Body));
    }

    // ---- books -----------------------------------------------------------------------------------------------

    public async Task<BrokerBook> GetOrderBookAsync(BrokerSession session, CancellationToken cancellationToken)
    {
        var book = await ReadAsync(session, "/v1/orders", "orders", cancellationToken);
        int buy = 0, sell = 0, completed = 0, open = 0, rejected = 0;
        var rows = new JsonArray();
        foreach (var o in (book["orders"] as JsonArray ?? []).OfType<JsonObject>())
        {
            var segment = Str(o["exchange_segment"]) ?? "";
            var action = ReverseAction(Str(o["txn_type"]));
            var status = Status(Str(o["status"]));
            if (action == "BUY") buy++;
            else if (action == "SELL") sell++;
            if (status == "complete") completed++;
            else if (status == "open") open++;
            else if (status == "rejected") rejected++;
            rows.Add(new JsonObject
            {
                ["symbol"] = await OaSymbolAsync(Str(o["symbol"]), segment, cancellationToken), ["exchange"] = segment, ["action"] = action, ["quantity"] = Int(o["qty"]),
                ["price"] = FromPaise(o["price"]), ["trigger_price"] = FromPaise(o["trigger_price"]), ["pricetype"] = ReverseOrderType(Str(o["order_type"])),
                ["product"] = ReverseProduct(Str(o["product"])), ["orderid"] = Str(o["order_id"]) ?? "", ["order_status"] = status, ["timestamp"] = Stamp(o["created_at"]),
            });
        }

        return new BrokerBook(rows, new JsonObject
        {
            ["total_buy_orders"] = buy, ["total_sell_orders"] = sell, ["total_completed_orders"] = completed, ["total_open_orders"] = open, ["total_rejected_orders"] = rejected,
        });
    }

    public async Task<JsonArray> GetTradeBookAsync(BrokerSession session, CancellationToken cancellationToken)
    {
        var book = await ReadAsync(session, "/v1/orders", "orders", cancellationToken);
        var result = new JsonArray();
        foreach (var o in (book["orders"] as JsonArray ?? []).OfType<JsonObject>())
        {
            if (!Truthy(o["filled_qty"])) continue;
            var fills = await ReadAsync(session, $"/v1/orders/{Uri.EscapeDataString(Str(o["order_id"]) ?? "")}/trades", "trades", cancellationToken);
            foreach (var t in (fills["trades"] as JsonArray ?? []).OfType<JsonObject>())
            {
                var segment = Str(t["exchange_segment"]) ?? "";
                var quantity = Int(t["qty"]);
                var price = FromPaise(t["price"]);
                result.Add(new JsonObject
                {
                    ["symbol"] = await OaSymbolAsync(Str(t["symbol"]), segment, cancellationToken), ["exchange"] = segment, ["product"] = ReverseProduct(Str(t["product"])),
                    ["action"] = ReverseAction(Str(t["txn_type"])), ["quantity"] = quantity, ["average_price"] = price, ["trade_value"] = BookFormatting.Round2(quantity * price),
                    ["orderid"] = Str(t["order_id"]) ?? "", ["tradeid"] = Str(t["exchange_trade_id"]) ?? "", ["timestamp"] = Stamp(t["traded_at"]),
                });
            }
        }

        return result;
    }

    /// <summary>The raw positions, each with its last price in rupees as "ltp".</summary>
    private async Task<List<JsonObject>> RawPositionsAsync(BrokerSession session, CancellationToken cancellationToken)
    {
        var book = await ReadAsync(session, "/v1/positions", "positions", cancellationToken);
        var rows = (book["positions"] as JsonArray ?? []).OfType<JsonObject>().ToList();
        if (rows.Count > 0)
        {
            var ltp = await LtpMapAsync(session, rows.Where(p => Truthy(p["instrument_id"])).Select(p => Int(p["instrument_id"])), cancellationToken);
            foreach (var p in rows) p["ltp"] = ltp.GetValueOrDefault(Int(p["instrument_id"]), 0.0);
        }

        return rows;
    }

    /// <summary>The profit of a position: realised, plus the open part marked at the last price (left out when there is no price).</summary>
    private static double PositionPnl(JsonObject p, double average)
    {
        var netQty = Int(p["net_qty"]);
        var ltp = Float(p["ltp"]);
        var pnl = FromPaise(p["realised_pnl"]);
        if (netQty != 0 && ltp != 0) pnl += (ltp - average) * netQty;
        return pnl;
    }

    public async Task<JsonArray> GetPositionsAsync(BrokerSession session, CancellationToken cancellationToken)
    {
        var result = new JsonArray();
        foreach (var p in await RawPositionsAsync(session, cancellationToken))
        {
            var segment = Str(p["exchange_segment"]) ?? "";
            var netQty = Int(p["net_qty"]);
            var average = FromPaise(p["net_avg_price"]);
            result.Add(new JsonObject
            {
                ["symbol"] = await OaSymbolAsync(Str(p["symbol"]), segment, cancellationToken), ["exchange"] = segment, ["product"] = ReverseProduct(Str(p["product"])), ["quantity"] = netQty,
                ["pnl"] = BookFormatting.Round2(PositionPnl(p, average)), ["average_price"] = BookFormatting.Fixed2(netQty != 0 ? average : 0.0), ["ltp"] = BookFormatting.Round2(Float(p["ltp"])),
            });
        }

        return result;
    }

    public async Task<BrokerBook> GetHoldingsAsync(BrokerSession session, CancellationToken cancellationToken)
    {
        var book = await ReadAsync(session, "/v1/holdings", "holdings", cancellationToken);
        var rows = (book["holdings"] as JsonArray ?? []).OfType<JsonObject>().ToList();
        var ids = rows.SelectMany(h => (h["instrument_ids"] as JsonArray ?? []).Select(Int)).ToList();
        var ltp = ids.Count > 0 ? await LtpMapAsync(session, ids, cancellationToken) : [];

        var holdings = new JsonArray();
        double value = 0, invested = 0, pnlTotal = 0;
        foreach (var h in rows)
        {
            var quantity = Int(h["holding_qty"]);
            if (quantity <= 0) continue;
            // The listing: the NSE one first, else the BSE one.
            string? symbol = null, exchange = null;
            long listing = 0;
            var holdingIds = (h["instrument_ids"] as JsonArray ?? []).Select(Int).ToList();
            foreach (var venue in new[] { "NSE", "BSE" })
            {
                foreach (var id in holdingIds)
                {
                    var known = await symbols.FindByTokenAsync(Name, id.ToString(CultureInfo.InvariantCulture), venue, cancellationToken);
                    if (known is null || string.IsNullOrEmpty(known.Symbol)) continue;
                    (symbol, exchange, listing) = (known.Symbol, venue, id);
                    break;
                }

                if (symbol is not null) break;
            }

            if (symbol is null) continue;
            var average = FromPaise(h["holding_avg_price"]);
            var last = holdingIds.Contains(listing) ? ltp.GetValueOrDefault(listing, 0.0) : 0.0;
            var pnl = last != 0 ? BookFormatting.Round2((last - average) * quantity) : 0.0;
            var pnlPercent = last != 0 && average != 0 ? BookFormatting.Round2((last - average) / average * 100) : 0.0;
            value += last * quantity;
            invested += average * quantity;
            pnlTotal += pnl;
            holdings.Add(new JsonObject
            {
                ["symbol"] = symbol, ["exchange"] = exchange, ["quantity"] = quantity, ["product"] = "CNC", ["average_price"] = average, ["ltp"] = last, ["pnl"] = pnl, ["pnlpercent"] = pnlPercent,
            });
        }

        return new BrokerBook(holdings, new JsonObject
        {
            ["totalholdingvalue"] = BookFormatting.Round2(value), ["totalinvvalue"] = BookFormatting.Round2(invested), ["totalprofitandloss"] = BookFormatting.Round2(pnlTotal),
            ["totalpnlpercentage"] = invested != 0 ? BookFormatting.Round2(pnlTotal / invested * 100) : 0.0,
        });
    }

    // ---- funds -----------------------------------------------------------------------------------------------

    private static readonly string[] UtilisedFields = ["span", "exposure", "delivery_margin", "intraday_margin", "delivery_buy", "charges", "mtm_loss"];

    public async Task<JsonObject> GetFundsAsync(BrokerSession session, CancellationToken cancellationToken)
    {
        var (status, funds) = await TryCallAsync(session, HttpMethod.Get, "/v1/funds", null, cancellationToken);
        if (status != 200 || !funds.ContainsKey("available")) throw new BrokerException($"{DisplayName} could not send the funds: {ErrorMessage(status, funds)}", 500);

        var utilised = funds["utilised"] as JsonObject ?? [];
        var collateral = FromPaise((funds["balance"] as JsonObject)?["collateral"]);
        var availableCash = FromPaise((funds["available"] as JsonObject)?["intraday_and_fno_selling"]) - collateral;
        var used = UtilisedFields.Sum(f => FromPaise(utilised[f])) + FromPaise((funds["option_premium"] as JsonObject)?["paid"]);

        // The realised and unrealised parts come from the position book; unreadable, they are zero.
        double realised = 0, unrealised = 0;
        try
        {
            var rows = await RawPositionsAsync(session, cancellationToken);
            realised = rows.Sum(p => FromPaise(p["realised_pnl"]));
            var total = rows.Sum(p => BookFormatting.Round2(PositionPnl(p, FromPaise(p["net_avg_price"]))));
            unrealised = total - realised;
        }
        catch (BrokerException)
        {
        }

        return new JsonObject
        {
            ["availablecash"] = BookFormatting.Fixed2(availableCash), ["collateral"] = BookFormatting.Fixed2(collateral), ["m2munrealized"] = BookFormatting.Fixed2(unrealised),
            ["m2mrealized"] = BookFormatting.Fixed2(realised), ["utiliseddebits"] = BookFormatting.Fixed2(used),
        };
    }
}
