using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using EPTradeBot.Core.Brokers;
using EPTradeBot.Core.Brokers.Gtt;
using static EPTradeBot.Infrastructure.Brokers.JsonRead;

namespace EPTradeBot.Infrastructure.Brokers.Kotak;

/// <summary>
/// Kotak Neo: port of broker/kotak api/order_api.py, api/funds.py, mapping/transform_data.py and mapping/order_data.py. The session token holds the trading token, the session id, the account's own base address and the
/// access token (joined by three colons); every call carries them as Auth, Sid and neo-fin-key headers, and a write is a jData form. Kotak refuses a stop-market from an API session, so one is sent as a stop-limit whose limit
/// is a protection band beyond the trigger (Kotak's own bands, snapped to the tick away from the trigger). Kotak's positions carry no price, so a positions read also asks for the price of each instrument held.
/// </summary>
[BrokerPlugin]
public sealed partial class KotakPlugin(IHttpClientFactory http, BrokerSessionFactory sessions, ISymbolMaster symbols, TimeProvider clock) : IBrokerOrders, IBrokerPortfolio, IBrokerFunds, IBrokerMultiQuotes, IBrokerMargin, IBrokerHistory
{
    public string Name => "kotak";
    public string DisplayName => "Kotak Neo";
    public BrokerCapability Capabilities => BrokerCapability.Orders | BrokerCapability.Portfolio | BrokerCapability.Funds | BrokerCapability.Quotes | BrokerCapability.Depth | BrokerCapability.Margin | BrokerCapability.History;
    public IReadOnlyList<string> RequiredKeys { get; } = ["ApiKey", "ApiSecret"];

    private const string Display = "Kotak Neo";

    private sealed record Neo(string Token, string Sid, string Base, string Access);

    private HttpClient Client() => http.CreateClient(Name);

    private Neo Parse(BrokerSession session)
    {
        var parts = session.AuthToken.Split(":::");
        if (parts.Length < 4 || !parts[2].StartsWith("http", StringComparison.OrdinalIgnoreCase))
            throw new BrokerException($"Your {Display} sign-in does not hold the address of your account. Sign in to {Display} again.", 401);
        return new Neo(parts[0], parts[1], sessions.BaseUrl(Name, parts[2].TrimEnd('/')), parts[3]);
    }

    private static string Exchange(string? segment) => segment switch
    {
        "nse_cm" => "NSE", "bse_cm" => "BSE", "cde_fo" => "CDS", "nse_fo" => "NFO", "bse_fo" => "BFO", "bcs_fo" => "BCD", "mcx_fo" => "MCX", _ => "",
    };

    private static JsonNode? Segment(string exchange) => exchange switch
    {
        "NSE" => "nse_cm", "BSE" => "bse_cm", "CDS" => "cde_fo", "NFO" => "nse_fo", "BFO" => "bse_fo", "BCD" => "bcs_fo", "MCX" => "mcx_fo", _ => null,
    };

    private static void Headers(HttpRequestMessage request, Neo neo)
    {
        request.Headers.TryAddWithoutValidation("accept", "application/json");
        request.Headers.TryAddWithoutValidation("Sid", neo.Sid);
        request.Headers.TryAddWithoutValidation("Auth", neo.Token);
        request.Headers.TryAddWithoutValidation("neo-fin-key", "neotradeapi");
    }

    /// <summary>One call: the status, and the body as it was read (null when it was not JSON).</summary>
    private async Task<(int Status, JsonNode? Body)> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        try
        {
            using var response = await Client().SendAsync(request, cancellationToken);
            return ((int)response.StatusCode, await ReadAsync(response, cancellationToken));
        }
        catch (HttpRequestException ex)
        {
            throw new BrokerException($"{Display} did not answer. Check your connection, then try again.", 502, ex);
        }
    }

    private Task<(int Status, JsonNode? Body)> GetAsync(Neo neo, string path, CancellationToken cancellationToken)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, neo.Base + path);
        Headers(request, neo);
        return SendAsync(request, cancellationToken);
    }

    private Task<(int Status, JsonNode? Body)> FormAsync(Neo neo, string path, JsonObject data, CancellationToken cancellationToken)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, neo.Base + path) { Content = new StringContent("jData=" + Uri.EscapeDataString(data.ToJsonString()), Encoding.UTF8, "application/x-www-form-urlencoded") };
        Headers(request, neo);
        return SendAsync(request, cancellationToken);
    }

    private static bool Ok(JsonNode? body) => body is JsonObject o && Str(o["stat"]) == "Ok";

    private static string Reason(JsonNode? body, string fallback) => body is JsonObject o && Str(o["emsg"]) is { Length: > 0 } m ? m : fallback;

    // ---- prices ----------------------------------------------------------------------------------------------

    private static string Whole(decimal value) => value.ToString(CultureInfo.InvariantCulture);

    /// <summary>Kotak rejects 0.0 on a numeric field: zero is "0", anything else is the value as it came.</summary>
    private static string Price(decimal value) => value == 0 ? "0" : Whole(value);

    private static string PyFloat(double value)
    {
        var text = value.ToString("R", CultureInfo.InvariantCulture);
        return text.Contains('.', StringComparison.Ordinal) || text.Contains('E', StringComparison.Ordinal) ? text : text + ".0";
    }

    private static string OrderType(string priceType) => priceType switch { "MARKET" => "MKT", "LIMIT" => "L", "SL" => "SL", "SL-M" => "SL-M", _ => "MARKET" };

    private static string Side(string action) => action.ToUpperInvariant() == "BUY" ? "B" : action.ToUpperInvariant() == "SELL" ? "S" : "None";

    /// <summary>The decimal places a tick size implies (0.05 is 2, 0.0025 is 4, 1.0 is 1), read from its Python text.</summary>
    private static int TickDecimals(double tick)
    {
        var text = PyFloat(tick);
        var exponent = 0;
        var e = text.IndexOf('E', StringComparison.Ordinal);
        if (e >= 0)
        {
            exponent = int.Parse(text[(e + 1)..], CultureInfo.InvariantCulture);
            text = text[..e];
        }

        var dot = text.IndexOf('.', StringComparison.Ordinal);
        var fraction = dot < 0 ? 0 : text.Length - dot - 1;
        return Math.Max(0, fraction - exponent);
    }

    private static double Offset(double price, string type)
    {
        if (type is "CE" or "PE")
        {
            if (price < 1.0) return 0.10;
            foreach (var (max, percent) in new[] { (5.0, 10.0), (10.0, 5.0), (100.0, 3.0), (500.0, 2.0) })
            {
                if (price < max) return price * percent / 100.0;
            }

            return price * 1.0 / 100.0;
        }

        foreach (var (max, percent) in new[] { (100.0, 2.0), (500.0, 1.0) })
        {
            if (price < max) return price * percent / 100.0;
        }

        return price * 0.5 / 100.0;
    }

    private static double Snap(double value, double tick, bool floor)
    {
        var ratio = BookFormatting.RoundPy(value / tick, 6);
        var k = floor ? Math.Floor(ratio) : Math.Ceiling(ratio);
        return BookFormatting.RoundPy(k * tick, TickDecimals(tick));
    }

    private sealed class Unsendable(string message) : Exception(message);

    /// <summary>The limit that turns a stop-market into a stop-limit Kotak takes: one band beyond the trigger in the fill direction, at least a tick away, on the tick grid.</summary>
    private static double ProtectedLimit(string symbol, string exchange, string action, double trigger, double? tickSize)
    {
        if (trigger <= 0) throw new Unsendable("A stop-market order needs a trigger price above zero. Enter one, then try again.");
        if (tickSize is not { } tick || !double.IsFinite(tick) || tick <= 0)
            throw new Unsendable($"The tick size of {symbol} on {exchange} is not in the {Display} master contract, so the limit price of a stop-market order cannot be worked out. Download the master contract again, then try again.");
        var offset = Offset(trigger, MarketPriceProtection.InstrumentType(null, symbol));
        if (action.Equals("SELL", StringComparison.OrdinalIgnoreCase))
        {
            var limit = Snap(Math.Min(trigger - offset, trigger - tick), tick, floor: true);
            if (limit <= 0) throw new Unsendable($"The trigger price {PyFloat(trigger)} is too low to work out a limit price for a stop-market sell of {symbol}. Raise the trigger price, then try again.");
            return limit;
        }

        return Snap(Math.Max(trigger + offset, trigger + tick), tick, floor: false);
    }

    // ---- orders ----------------------------------------------------------------------------------------------

    public async Task<BrokerCallResult> PlaceAsync(BrokerSession session, BrokerOrder order, CancellationToken cancellationToken)
    {
        var info = await symbols.FindAsync(Name, order.Symbol, order.Exchange, cancellationToken);
        if (info is null) return BrokerCallResult.Refused(400, $"{order.Symbol} on {order.Exchange} is not in the {DisplayName} master contract. Check the symbol, or download the master contract again.");
        var neo = Parse(session);
        var body = new JsonObject
        {
            ["am"] = "NO", ["dq"] = order.DisclosedQuantity.ToString(CultureInfo.InvariantCulture), ["es"] = Segment(order.Exchange), ["mp"] = "0", ["pc"] = order.Product, ["pf"] = "N", ["pr"] = Price(order.Price),
            ["pt"] = OrderType(order.PriceType), ["qt"] = order.Quantity.ToString(CultureInfo.InvariantCulture), ["rt"] = "DAY", ["tp"] = Price(order.TriggerPrice), ["ts"] = info.BrokerSymbol, ["tt"] = Side(order.Action),
            ["ig"] = $"openalgo-{Guid.NewGuid()}",
        };
        try
        {
            if (order.PriceType == "SL-M")
            {
                body["pt"] = "SL";
                body["pr"] = PyFloat(ProtectedLimit(order.Symbol, order.Exchange, order.Action, (double)order.TriggerPrice, info.TickSize));
            }
        }
        catch (Unsendable ex)
        {
            return BrokerCallResult.Refused(400, ex.Message);
        }

        (int Status, JsonNode? Body) reply;
        try
        {
            reply = await FormAsync(neo, "/quick/order/rule/ms/place", body, cancellationToken);
        }
        catch (BrokerException ex) when (ex.HttpStatus == 502)
        {
            return new BrokerCallResult(false, 502, $"The order was sent to {Display} but no reply came back, so it may have been placed. Check the order book on {Display} before trying again.", MayHaveBeenSent: true);
        }

        if (reply.Body is not JsonObject answer)
            return new BrokerCallResult(false, 502, $"{Display} could not confirm the order, so it may have been placed. Check the order book on {Display} before trying again.", MayHaveBeenSent: true);
        if (Ok(answer))
        {
            if (Str(answer["nOrdNo"]) is { Length: > 0 } id) return BrokerCallResult.Success(id);
            return new BrokerCallResult(false, 502, $"{Display} accepted the order but sent no order number, so it cannot be followed. Check the order book on {Display} before trying again.", MayHaveBeenSent: true);
        }

        return BrokerCallResult.Refused(reply.Status is >= 400 ? reply.Status : 400, Reason(answer, "Failed to place order"));
    }

    public async Task<BrokerCallResult> ModifyAsync(BrokerSession session, BrokerOrderChange change, CancellationToken cancellationToken)
    {
        var info = await symbols.FindAsync(Name, change.Symbol, change.Exchange, cancellationToken);
        if (info is null) return BrokerCallResult.Refused(400, $"{change.Symbol} on {change.Exchange} is not in the {DisplayName} master contract. Check the symbol, or download the master contract again.");
        var neo = Parse(session);
        var body = new JsonObject
        {
            ["tk"] = info.Token, ["dq"] = change.DisclosedQuantity.ToString(CultureInfo.InvariantCulture), ["es"] = Segment(change.Exchange), ["mp"] = "0", ["dd"] = "NA", ["vd"] = "DAY", ["pc"] = change.Product,
            ["pr"] = Price(change.Price), ["pt"] = OrderType(change.PriceType), ["qt"] = change.Quantity.ToString(CultureInfo.InvariantCulture), ["tp"] = Price(change.TriggerPrice), ["ts"] = info.BrokerSymbol,
            ["no"] = change.OrderId, ["tt"] = change.Action == "BUY" ? "B" : change.Action == "SELL" ? "S" : "None",
        };
        try
        {
            if (change.PriceType == "SL-M")
            {
                body["pt"] = "SL";
                body["pr"] = PyFloat(ProtectedLimit(change.Symbol, change.Exchange, change.Action, (double)change.TriggerPrice, info.TickSize));
            }
        }
        catch (Unsendable ex)
        {
            return BrokerCallResult.Refused(400, ex.Message);
        }

        var reply = await FormAsync(neo, "/quick/order/vr/modify", body, cancellationToken);
        if (Ok(reply.Body)) return BrokerCallResult.Success(Str(((JsonObject)reply.Body!)["nOrdNo"]) is { Length: > 0 } id ? id : change.OrderId);
        return BrokerCallResult.Refused(reply.Status is >= 400 ? reply.Status : 400, Reason(reply.Body, "Failed to modify order"));
    }

    public async Task<BrokerCallResult> CancelAsync(BrokerSession session, string orderId, CancellationToken cancellationToken)
    {
        var reply = await FormAsync(Parse(session), "/quick/order/cancel", new JsonObject { ["on"] = orderId, ["am"] = "NO" }, cancellationToken);
        if (Ok(reply.Body)) return BrokerCallResult.Success(Str(((JsonObject)reply.Body!)["nOrdNo"]) is { Length: > 0 } id ? id : orderId);
        return BrokerCallResult.Refused(reply.Status is >= 400 ? reply.Status : 400, Reason(reply.Body, "Failed to cancel order"));
    }

    // ---- books -----------------------------------------------------------------------------------------------

    private async Task<List<JsonObject>> ReadBookAsync(BrokerSession session, string path, string what, CancellationToken cancellationToken)
    {
        var (_, body) = await GetAsync(Parse(session), path, cancellationToken);
        if (!Ok(body)) throw new BrokerException($"{Display} could not send the {what}: {Reason(body, "no reason was given.")}", 500);
        return ((JsonObject)body!)["data"] switch { JsonArray list => list.OfType<JsonObject>().ToList(), JsonObject one => [one], _ => [] };
    }

    /// <summary>The OpenAlgo symbol of a row: by its token when it has one, else by the broker's trading symbol, else the trading symbol itself.</summary>
    private async Task<string> SymbolAsync(JsonObject row, string exchange, CancellationToken cancellationToken) =>
        await SymbolOrNullAsync(row, exchange, cancellationToken) ?? Str(row["trdSym"]) ?? "";

    private static JsonNode Raw(JsonObject row, string key, JsonNode fallback) => row[key]?.DeepClone() ?? fallback;

    private static string Action(string? raw) => raw switch { "B" => "BUY", "S" => "SELL", _ => raw ?? "" };

    public async Task<BrokerBook> GetOrderBookAsync(BrokerSession session, CancellationToken cancellationToken)
    {
        int buy = 0, sell = 0, completed = 0, open = 0, rejected = 0;
        var result = new JsonArray();
        foreach (var r in await ReadBookAsync(session, "/quick/user/orders", "order book", cancellationToken))
        {
            var exchange = Exchange(Str(r["exSeg"]));
            var side = Str(r["trnsTp"]) ?? "";
            if (side == "B") buy++;
            else if (side == "S") sell++;
            var status = Str(r["ordSt"]) ?? "";
            if (status == "trigger pending") status = "open";
            if (status == "complete") completed++;
            else if (status == "open") open++;
            else if (status == "rejected") rejected++;

            var priceType = Str(r["prcTp"]) ?? "";
            priceType = priceType switch { "MKT" => "MARKET", "L" => "LIMIT", _ => priceType };
            var price = Raw(r, "avgPrc", 0.0);
            if (priceType is "LIMIT" or "SL" && Str(r["ordSt"]) != "complete") price = Raw(r, "prc", 0.0);
            result.Add(new JsonObject
            {
                ["symbol"] = await SymbolAsync(r, exchange, cancellationToken), ["exchange"] = exchange, ["action"] = Action(side), ["quantity"] = Raw(r, "qty", 0), ["price"] = price, ["trigger_price"] = Raw(r, "trgPrc", 0.0),
                ["pricetype"] = priceType, ["product"] = Raw(r, "prod", ""), ["orderid"] = Raw(r, "nOrdNo", ""), ["order_status"] = status, ["timestamp"] = Raw(r, "ordEntTm", ""), ["order_tag"] = Raw(r, "GuiOrdId", ""),
            });
        }

        return new BrokerBook(result, new JsonObject { ["total_buy_orders"] = buy, ["total_sell_orders"] = sell, ["total_completed_orders"] = completed, ["total_open_orders"] = open, ["total_rejected_orders"] = rejected });
    }

    public async Task<JsonArray> GetTradeBookAsync(BrokerSession session, CancellationToken cancellationToken)
    {
        var result = new JsonArray();
        foreach (var r in await ReadBookAsync(session, "/quick/user/trades", "trade book", cancellationToken))
        {
            var exchange = Exchange(Str(r["exSeg"]));
            result.Add(new JsonObject
            {
                ["symbol"] = await SymbolAsync(r, exchange, cancellationToken), ["exchange"] = exchange, ["product"] = Raw(r, "prod", ""), ["action"] = Action(Str(r["trnsTp"])), ["quantity"] = Raw(r, "fldQty", 0),
                ["average_price"] = Raw(r, "avgPrc", 0.0), ["trade_value"] = Float(r["fldQty"]) * Float(r["avgPrc"]), ["orderid"] = Raw(r, "nOrdNo", ""), ["tradeid"] = Raw(r, "flId", ""), ["timestamp"] = Raw(r, "exTm", ""),
                ["order_tag"] = Raw(r, "GuiOrdId", ""),
            });
        }

        return result;
    }

    // ---- positions -------------------------------------------------------------------------------------------

    private static double Number(JsonObject row, string field) => Truthy(row[field]) ? Float(row[field]) : 0.0;

    private static double Term(JsonObject row, string field)
    {
        var value = Number(row, field);
        return value == 0 ? 1.0 : value;
    }

    public async Task<JsonArray> GetPositionsAsync(BrokerSession session, CancellationToken cancellationToken)
    {
        var rows = await ReadBookAsync(session, "/quick/user/positions", "positions", cancellationToken);
        var prices = await LtpsAsync(Parse(session), rows, cancellationToken);
        var result = new JsonArray();
        foreach (var r in rows)
        {
            var exchange = Exchange(Str(r["exSeg"]));
            var symbol = await SymbolAsync(r, exchange, cancellationToken);
            var ltp = prices.GetValueOrDefault(((await SymbolOrNullAsync(r, exchange, cancellationToken)) ?? Str(r["trdSym"]) ?? "", exchange));
            var quantity = (long)Math.Truncate((Number(r, "flBuyQty") - Number(r, "flSellQty")) + (Number(r, "cfBuyQty") - Number(r, "cfSellQty")));
            var position = new JsonObject { ["symbol"] = symbol, ["exchange"] = exchange, ["product"] = Raw(r, "prod", ""), ["quantity"] = quantity, ["average_price"] = Raw(r, "avgnetprice", 0.0), ["ltp"] = BookFormatting.Round2(ltp) };

            var factor = Term(r, "multiplier") * (Term(r, "genNum") / Term(r, "genDen")) * (Term(r, "prcNum") / Term(r, "prcDen"));
            double cfBuyAmt = 0, cfSellAmt = 0;
            var carried = false;
            var cfBuyQty = Number(r, "cfBuyQty");
            var cfSellQty = Number(r, "cfSellQty");
            if (cfBuyQty != 0 || cfSellQty != 0)
            {
                var carriedPrice = Number(r, "upldPrc");
                if (carriedPrice > 0)
                {
                    cfBuyAmt = cfBuyQty * carriedPrice * factor;
                    cfSellAmt = cfSellQty * carriedPrice * factor;
                }
                else
                {
                    cfBuyAmt = Number(r, "cfBuyAmt");
                    cfSellAmt = Number(r, "cfSellAmt");
                    carried = true;
                }
            }

            var totalBuy = cfBuyAmt + Number(r, "buyAmt");
            var totalSell = cfSellAmt + Number(r, "sellAmt");
            var buyQty = Number(r, "flBuyQty") + cfBuyQty;
            var sellQty = Number(r, "flSellQty") + cfSellQty;
            if (quantity > 0 && buyQty > 0) position["average_price"] = BookFormatting.Round2(totalBuy / (buyQty * factor));
            else if (quantity < 0 && sellQty > 0) position["average_price"] = BookFormatting.Round2(totalSell / (sellQty * factor));
            else if (quantity != 0) position["average_price"] = 0.0;

            var realized = totalSell - totalBuy;
            double pnl;
            if (quantity == 0) pnl = BookFormatting.Round2(realized);
            else if (ltp != 0) pnl = BookFormatting.Round2(realized + quantity * ltp * factor);
            else pnl = 0.0;
            position["pnl"] = pnl == 0 ? 0.0 : pnl;
            if (carried) position["average_price_basis"] = "carry_forward_valuation";
            result.Add(position);
        }

        return result;
    }

    private async Task<string?> SymbolOrNullAsync(JsonObject row, string exchange, CancellationToken cancellationToken)
    {
        var token = (Str(row["tok"]) ?? "").Trim();
        if (token.Length > 0 && (await symbols.FindByTokenAsync(Name, token, exchange, cancellationToken))?.Symbol is { Length: > 0 } byToken) return byToken;
        var brokerSymbol = Truthy(row["trdSym"]) ? Str(row["trdSym"]) ?? "" : Str(row["sym"]) ?? "";
        return brokerSymbol.Length > 0 ? (await symbols.FindByBrokerSymbolAsync(Name, brokerSymbol, exchange, cancellationToken))?.Symbol : null;
    }

    // ---- holdings and funds ----------------------------------------------------------------------------------

    public async Task<BrokerBook> GetHoldingsAsync(BrokerSession session, CancellationToken cancellationToken)
    {
        var holdings = new JsonArray();
        double value = 0, cost = 0;
        foreach (var h in await ReadBookAsync(session, "/portfolio/v1/holdings", "holdings", cancellationToken))
        {
            var market = Float(h["mktValue"]);
            var held = Float(h["holdingCost"]);
            value += market;
            cost += held;
            var product = Str(h["instrumentType"]) ?? "";
            holdings.Add(new JsonObject
            {
                ["symbol"] = Raw(h, "displaySymbol", ""), ["exchange"] = Exchange(Str(h["exchangeSegment"])), ["quantity"] = Raw(h, "quantity", 0), ["product"] = product == "Equity" ? "CNC" : product,
                ["average_price"] = BookFormatting.Round2(Float(h["averagePrice"])), ["pnl"] = BookFormatting.Round2(market - held), ["pnlpercent"] = BookFormatting.Round2(held != 0 ? (market - held) / held * 100 : 0),
            });
        }

        var profit = value - cost;
        return new BrokerBook(holdings, new JsonObject { ["totalholdingvalue"] = value, ["totalinvvalue"] = cost, ["totalprofitandloss"] = profit, ["totalpnlpercentage"] = BookFormatting.Round2(cost != 0 ? profit / cost * 100 : 0) });
    }

    public async Task<JsonObject> GetFundsAsync(BrokerSession session, CancellationToken cancellationToken)
    {
        var (_, body) = await FormAsync(Parse(session), "/quick/user/limits", new JsonObject { ["seg"] = "ALL", ["exch"] = "ALL", ["prod"] = "ALL" }, cancellationToken);
        if (!Ok(body)) throw new BrokerException($"{Display} could not send the funds: {Reason(body, "no reason was given.")}", 500);
        var d = (JsonObject)body!;
        return new JsonObject
        {
            ["availablecash"] = BookFormatting.Fixed2(Float(d["CollateralValue"])), ["collateral"] = BookFormatting.Fixed2(Float(d["Collateral"])), ["m2munrealized"] = BookFormatting.Fixed2(Float(d["UnrealizedMtomPrsnt"])),
            ["m2mrealized"] = BookFormatting.Fixed2(Float(d["RealizedMtomPrsnt"])), ["utiliseddebits"] = BookFormatting.Fixed2(Float(d["MarginUsed"])),
        };
    }
}
