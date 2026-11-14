using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using EPTradeBot.Core.Brokers;
using EPTradeBot.Core.Brokers.Gtt;
using static EPTradeBot.Infrastructure.Brokers.JsonRead;

namespace EPTradeBot.Infrastructure.Brokers.FivePaisa;

/// <summary>
/// 5paisa's own REST API (Openapi.5paisa.com, not its XTS): port of broker/fivepaisa api/order_api.py, api/funds.py, mapping/transform_data.py and mapping/order_data.py. The broker key packs the app key, the user id and the
/// client code (key:::user:::client). Every call is a POST of {head: {key}, body} with the token as a bearer. 5paisa rejects an order that reads as at market, so a market order is sent as a limit order a slab beyond the last
/// price (from MarketSnapshot, with the best bid and ask from MarketDepth) and a stop-market as a stop-limit a slab beyond its trigger, snapped to the tick. A position, order or trade whose token is not in the master contract keeps
/// the broker's own letters for exchange, product and side, as OpenAlgo shows it.
/// </summary>
[BrokerPlugin]
public sealed partial class FivePaisaPlugin(IHttpClientFactory http, BrokerSessionFactory sessions, ISymbolMaster symbols) : IBrokerOrders, IBrokerPortfolio, IBrokerFunds, IBrokerMultiQuotes, IBrokerHistory
{
    private const string DefaultBaseUrl = "https://Openapi.5paisa.com";
    private const string Svc = "/VendorsAPI/Service1.svc";

    public string Name => "fivepaisa";
    public string DisplayName => "5paisa";
    public BrokerCapability Capabilities => BrokerCapability.Orders | BrokerCapability.Portfolio | BrokerCapability.Funds | BrokerCapability.Quotes | BrokerCapability.Depth | BrokerCapability.History;
    public IReadOnlyList<string> RequiredKeys { get; } = ["ApiKey", "ApiSecret"];

    // ---- keys and calls ---------------------------------------------------------------------------------------

    private sealed record Account(string AppKey, string UserId, string ClientCode);

    private Account Keys(BrokerSession session)
    {
        var parts = (session.Key("ApiKey") ?? "").Split(":::");
        if (parts.Length != 3 || parts.Any(string.IsNullOrWhiteSpace))
            throw new BrokerException($"The {DisplayName} key is not written as app key:::user id:::client code. Correct it in the server's settings, then sign in again.", 409);
        return new Account(parts[0].Trim(), parts[1].Trim(), parts[2].Trim());
    }

    private HttpClient Client() => http.CreateClient(Name);

    private async Task<(int Status, JsonObject Body)> PostAsync(BrokerSession session, string path, JsonObject body, CancellationToken cancellationToken, TimeSpan? timeout = null, bool accept = false)
    {
        var payload = new JsonObject { ["head"] = new JsonObject { ["key"] = Keys(session).AppKey }, ["body"] = body };
        using var request = new HttpRequestMessage(HttpMethod.Post, sessions.BaseUrl(Name, DefaultBaseUrl) + path) { Content = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json") };
        request.Headers.TryAddWithoutValidation("Authorization", $"bearer {session.AuthToken}");
        if (accept) request.Headers.TryAddWithoutValidation("Accept", "application/json");
        using var limit = timeout is { } t ? CancellationTokenSource.CreateLinkedTokenSource(cancellationToken) : null;
        limit?.CancelAfter(timeout!.Value);
        try
        {
            using var response = await Client().SendAsync(request, limit?.Token ?? cancellationToken);
            return ((int)response.StatusCode, await ReadAsync(response, cancellationToken) as JsonObject ?? []);
        }
        catch (HttpRequestException ex)
        {
            throw new BrokerException($"{DisplayName} did not answer. Check your connection, then try again.", 502, ex);
        }
    }

    private static string Description(JsonObject answer) => Str((answer["head"] as JsonObject)?["statusDescription"]) ?? "";

    private static bool Succeeded(JsonObject answer) => Description(answer) == "Success";

    private static JsonObject Body(JsonObject answer) => answer["body"] as JsonObject ?? [];

    private JsonObject ClientBody(BrokerSession session) => new() { ["ClientCode"] = Keys(session).ClientCode };

    // ---- vocabulary -------------------------------------------------------------------------------------------

    private static string? MapExchange(string exchange) => exchange switch
    {
        "NSE" or "NFO" or "CDS" or "NSE_INDEX" => "N", "BSE" or "BFO" or "BCD" or "BSE_INDEX" => "B", "MCX" => "M", _ => null,
    };

    private static string? MapExchangeType(string exchange) => exchange switch
    {
        "NSE" or "BSE" or "NSE_INDEX" or "BSE_INDEX" => "C", "NFO" or "BFO" or "MCX" => "D", "CDS" or "BCD" => "U", _ => null,
    };

    private static string? ReverseExchange(string? exch, string? type) => (exch, type) switch
    {
        ("N", "C") => "NSE", ("B", "C") => "BSE", ("N", "D") => "NFO", ("B", "D") => "BFO", ("N", "U") => "CDS", ("B", "U") => "BCD", ("M", "D") => "MCX", _ => null,
    };

    private static readonly string[] IndexSymbols = ["NIFTY", "BANKNIFTY", "FINNIFTY", "MIDCPNIFTY", "NIFTYNXT50", "SENSEX", "BANKEX", "SENSEX50", "INDIAVIX"];

    /// <summary>Index spot names live under NSE_INDEX / BSE_INDEX in the master contract; only an exact index name is remapped (NIFTYBEES is a share).</summary>
    private static string NormalizeExchange(string symbol, string exchange) => IndexSymbols.Contains(symbol.ToUpperInvariant()) ? exchange switch { "NSE" => "NSE_INDEX", "BSE" => "BSE_INDEX", _ => exchange } : exchange;

    private static string MapStatus(string? status)
    {
        var raw = (status ?? "").Trim();
        var lower = raw.ToLowerInvariant();
        var mapped = lower switch
        {
            "fully executed" => "complete",
            "pending" or "open" or "modified" or "placed" or "ah placed" or "ah modified" or "xmitted" => "open",
            "cancelled" or "canceled" or "ah cancelled" => "cancelled",
            "rejected by 5p" or "rejected by exch" => "rejected",
            _ => null,
        };
        if (mapped is not null) return mapped;
        if (lower.Contains("rejected", StringComparison.Ordinal)) return "rejected";
        return lower.Contains("cancel", StringComparison.Ordinal) ? "cancelled" : lower;
    }

    private static readonly Regex DatePattern = new(@"/Date\((\d+)([+-]\d{4})\)/", RegexOptions.Compiled);

    private static string ConvertDate(JsonNode? node)
    {
        var match = DatePattern.Match(Str(node) ?? "");
        if (!match.Success) return "Invalid date format";
        var moment = DateTimeOffset.FromUnixTimeMilliseconds(long.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture)).UtcDateTime;
        var offset = match.Groups[2].Value;
        var hours = int.Parse(offset[..3], CultureInfo.InvariantCulture);
        var minutes = int.Parse(offset[0] + offset[3..], CultureInfo.InvariantCulture);
        return moment.AddHours(hours).AddMinutes(minutes).ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
    }

    private static string PyFloat(double value)
    {
        var text = value.ToString("R", CultureInfo.InvariantCulture);
        return text.Contains('.', StringComparison.Ordinal) || text.Contains('E', StringComparison.Ordinal) ? text : text + ".0";
    }

    private static int TickDecimals(double tick)
    {
        var text = PyFloat(tick);
        var dot = text.IndexOf('.', StringComparison.Ordinal);
        return dot < 0 ? 0 : text.Length - dot - 1;
    }

    private static double SnapToTick(double value, double tick, bool floor)
    {
        var ratio = BookFormatting.RoundPy(value / tick, 6);
        var k = floor ? Math.Floor(ratio) : Math.Ceiling(ratio);
        return BookFormatting.RoundPy(k * tick, TickDecimals(tick));
    }

    /// <summary>The limit a stop-market is sent with: its trigger moved a slab beyond it in the fill direction, at least a tick away and snapped to the tick; refused when the tick is unknown.</summary>
    private double ProtectedStopPrice(SymbolRecord instrument, string action, double trigger)
    {
        var tick = instrument.TickSize is { } t ? t : 0.0;
        if (!double.IsFinite(tick) || tick <= 0) throw new BrokerException($"{DisplayName} needs the tick size of {instrument.Symbol} on {instrument.Exchange} to place a stop-market order, and the master contract has none. Download the master contract again.", 400);
        var pct = MarketPriceProtection.Percentage(trigger, MarketPriceProtection.InstrumentType(null, instrument.Symbol)) / 100.0;
        if (action.Equals("SELL", StringComparison.OrdinalIgnoreCase))
        {
            var limit = SnapToTick(Math.Min(trigger * (1 - pct), trigger - tick), tick, true);
            if (limit <= 0) throw new BrokerException($"The trigger {PyFloat(trigger)} for {instrument.Symbol} on {instrument.Exchange} is too low to work out a stop-market limit price. Use a stop-limit order instead.", 400);
            return limit;
        }

        return SnapToTick(Math.Max(trigger * (1 + pct), trigger + tick), tick, false);
    }

    // ---- orders -----------------------------------------------------------------------------------------------

    public async Task<BrokerCallResult> PlaceAsync(BrokerSession session, BrokerOrder order, CancellationToken cancellationToken)
    {
        var info = await symbols.FindAsync(Name, order.Symbol, order.Exchange, cancellationToken);
        if (info is null || string.IsNullOrEmpty(info.Token)) return BrokerCallResult.Refused(400, $"{order.Symbol} on {order.Exchange} is not in the {DisplayName} master contract. Check the symbol, or download the master contract again.");

        var action = order.Action.ToUpperInvariant();
        var price = (double)order.Price;
        if (order.PriceType == "MARKET")
        {
            Quote? quote = null;
            try
            {
                quote = await QuoteAsync(session, info, order.Exchange, cancellationToken);
            }
            catch (BrokerException)
            {
            }

            if (quote is { Ltp: > 0 } q) price = MarketPriceProtection.ProtectedPrice(q.Ltp, action, MarketPriceProtection.InstrumentType(null, order.Symbol), info.TickSize);
        }

        var trigger = (double)order.TriggerPrice;
        if (order.PriceType == "SL-M" && trigger > 0)
        {
            try
            {
                price = ProtectedStopPrice(info, action, trigger);
            }
            catch (BrokerException ex)
            {
                return BrokerCallResult.Refused(ex.HttpStatus, ex.Message);
            }
        }

        var body = new JsonObject
        {
            ["OrderType"] = action == "BUY" ? "B" : "S", ["Exchange"] = MapExchange(order.Exchange), ["ExchangeType"] = MapExchangeType(order.Exchange), ["ScripCode"] = info.Token, ["Price"] = price, ["Qty"] = order.Quantity,
            ["StopLossPrice"] = trigger, ["DisQty"] = order.DisclosedQuantity, ["IsIntraday"] = order.Product == "MIS", ["AHPlaced"] = "N", ["RemoteOrderID"] = "OpenAlgo",
        };

        (int Status, JsonObject Body) reply;
        try
        {
            reply = await PostAsync(session, $"{Svc}/V1/PlaceOrderRequest", body, cancellationToken);
        }
        catch (BrokerException ex) when (ex.HttpStatus == 502)
        {
            return new BrokerCallResult(false, 502, $"The order was sent to {DisplayName} but no reply came back, so it may have been placed. Check the order book on {DisplayName} before trying again.", MayHaveBeenSent: true);
        }

        if (reply.Status >= 500)
            return new BrokerCallResult(false, 502, $"{DisplayName} could not confirm the order, so it may have been placed. Check the order book on {DisplayName} before trying again.", MayHaveBeenSent: true);

        var answer = Body(reply.Body);
        var id = answer["BrokerOrderID"];
        if (reply.Status < 400 && Succeeded(reply.Body) && Float(answer["Status"], double.NaN) == 0 && Truthy(id)) return BrokerCallResult.Success(Str(id)!);

        var said = Str(answer["Message"]);
        var reason = !string.IsNullOrEmpty(said) && said != "Success" ? said : Description(reply.Body) is { Length: > 0 } d && d != "Success" ? d : $"{DisplayName} did not take the order and gave no reason. Check the order book on {DisplayName}.";
        return BrokerCallResult.Refused(400, reason);
    }

    private async Task<List<JsonObject>> OrderRowsAsync(BrokerSession session, CancellationToken cancellationToken)
    {
        var (status, answer) = await PostAsync(session, $"{Svc}/V3/OrderBook", ClientBody(session), cancellationToken);
        if (status >= 400 || !Succeeded(answer)) throw new BrokerException($"{DisplayName} could not send the order book: {(Description(answer) is { Length: > 0 } d ? d : "no reason was given.")}", 500);
        return (Body(answer)["OrderBookDetail"] as JsonArray)?.OfType<JsonObject>().ToList() ?? [];
    }

    public async Task<BrokerCallResult> ModifyAsync(BrokerSession session, BrokerOrderChange change, CancellationToken cancellationToken)
    {
        var rows = await OrderRowsAsync(session, cancellationToken);
        var match = rows.FirstOrDefault(r => (Str(r["BrokerOrderId"]) ?? "") == change.OrderId);
        if (match is null) return BrokerCallResult.Refused(400, $"Order {change.OrderId} not found in order book");
        var exchangeId = Str(match["ExchOrderID"]);
        if (string.IsNullOrEmpty(exchangeId)) return BrokerCallResult.Refused(400, "Exchange Order ID not found for this order");

        var body = new JsonObject { ["ExchOrderID"] = exchangeId, ["Price"] = (double)change.Price, ["Qty"] = change.Quantity, ["StopLossPrice"] = (double)change.TriggerPrice, ["DisQty"] = change.DisclosedQuantity };
        var (status, answer) = await PostAsync(session, $"{Svc}/V1/ModifyOrderRequest", body, cancellationToken);
        if (status < 400 && Str((answer["head"] as JsonObject)?["status"]) == "0") return BrokerCallResult.Success(change.OrderId);
        return BrokerCallResult.Refused(status >= 400 ? status : 400, Description(answer) is { Length: > 0 } d ? d : "Failed to modify order");
    }

    public async Task<BrokerCallResult> CancelAsync(BrokerSession session, string orderId, CancellationToken cancellationToken)
    {
        var rows = await OrderRowsAsync(session, cancellationToken);
        var match = rows.FirstOrDefault(r => (Str(r["ExchOrderID"]) ?? "") == orderId || (Str(r["BrokerOrderId"]) ?? "") == orderId);
        if (match is null) return BrokerCallResult.Refused(404, $"Order not found: {orderId}");
        var exchangeId = Str(match["ExchOrderID"]);
        if (Str(match["OrderStatus"]) == "Pending" && string.IsNullOrEmpty(exchangeId))
            return BrokerCallResult.Refused(400, "Order is still pending at broker level. Cannot cancel until it reaches exchange.");

        var (status, answer) = await PostAsync(session, $"{Svc}/V1/CancelOrderRequest", new JsonObject { ["ExchOrderID"] = exchangeId }, cancellationToken);
        if (status < 400 && Succeeded(answer)) return BrokerCallResult.Success(orderId);
        return BrokerCallResult.Refused(status >= 400 ? status : 400, Str(Body(answer)["Message"]) is { Length: > 0 } m ? m : "Failed to cancel order");
    }

    // ---- books ------------------------------------------------------------------------------------------------

    private async Task<string?> SymbolAsync(JsonObject row, string? exchange, CancellationToken cancellationToken) =>
        exchange is null ? null : (await symbols.FindByTokenAsync(Name, Str(row["ScripCode"]) ?? "", exchange, cancellationToken))?.Symbol;

    private static string Product(string exchange, string? delivery) => exchange is "NSE" or "BSE" && delivery == "D" ? "CNC" : delivery == "I" ? "MIS" : exchange is "NFO" or "MCX" or "BFO" or "CDS" && delivery == "D" ? "NRML" : delivery ?? "";

    public async Task<BrokerBook> GetOrderBookAsync(BrokerSession session, CancellationToken cancellationToken)
    {
        int buy = 0, sell = 0, completed = 0, open = 0, rejected = 0, cancelled = 0;
        var result = new JsonArray();
        foreach (var r in await OrderRowsAsync(session, cancellationToken))
        {
            var exchange = ReverseExchange(Str(r["Exch"]), Str(r["ExchType"]));
            var symbol = await SymbolAsync(r, exchange, cancellationToken);
            var shownExchange = symbol is null ? Str(r["Exch"]) ?? "" : exchange!;
            var shownProduct = symbol is null ? Str(r["DelvIntra"]) ?? "" : Product(exchange!, Str(r["DelvIntra"]));
            var side = Str(r["BuySell"]);
            if (side == "B") { buy++; side = "BUY"; }
            else if (side == "S") { sell++; side = "SELL"; }

            var normalized = MapStatus(Truthy(r["OrderStatus"]) ? Str(r["OrderStatus"]) : "");
            switch (normalized)
            {
                case "complete": completed++; break;
                case "open": open++; break;
                case "rejected": rejected++; break;
                case "cancelled": cancelled++; break;
            }

            var triggerNode = r["SLTriggerRate"];
            var stop = Truthy(triggerNode) ? Float(triggerNode) : 0.0;
            var atMarket = Str(r["AtMarket"]);
            var pricetype = (atMarket, stop > 0) switch { ("Y", false) => "MARKET", ("N", false) => "LIMIT", ("Y", true) => "SL-M", ("N", true) => "SL", _ => "LIMIT" };
            result.Add(new JsonObject
            {
                ["symbol"] = symbol ?? Str(r["ScripName"]) ?? "", ["exchange"] = shownExchange, ["action"] = side ?? "", ["quantity"] = Truthy(r["Qty"]) ? r["Qty"]!.DeepClone() : 0, ["price"] = r["Rate"]?.DeepClone() ?? 0.0,
                ["trigger_price"] = stop, ["pricetype"] = pricetype, ["product"] = shownProduct, ["orderid"] = Str(r["BrokerOrderId"]) ?? "", ["order_status"] = normalized, ["timestamp"] = ConvertDate(r["BrokerOrderTime"]),
                ["reason"] = r["Reason"]?.DeepClone() ?? "",
            });
        }

        return new BrokerBook(result, new JsonObject
        {
            ["total_buy_orders"] = buy, ["total_sell_orders"] = sell, ["total_completed_orders"] = completed, ["total_open_orders"] = open, ["total_rejected_orders"] = rejected, ["total_cancelled_orders"] = cancelled,
        });
    }

    private async Task<List<JsonObject>> ReadRowsAsync(BrokerSession session, string path, string what, string key, CancellationToken cancellationToken)
    {
        var (status, answer) = await PostAsync(session, path, ClientBody(session), cancellationToken);
        if (status >= 400 || !Succeeded(answer)) throw new BrokerException($"{DisplayName} could not send the {what}: {(Description(answer) is { Length: > 0 } d ? d : "no reason was given.")}", 500);
        return (Body(answer)[key] as JsonArray)?.OfType<JsonObject>().ToList() ?? [];
    }

    public async Task<JsonArray> GetTradeBookAsync(BrokerSession session, CancellationToken cancellationToken)
    {
        var result = new JsonArray();
        foreach (var r in await ReadRowsAsync(session, $"{Svc}/V1/TradeBook", "trade book", "TradeBookDetail", cancellationToken))
        {
            var exchange = ReverseExchange(Str(r["Exch"]), Str(r["ExchType"]));
            var symbol = await SymbolAsync(r, exchange, cancellationToken);
            var side = Str(r["BuySell"]);
            if (symbol is not null) side = side == "B" ? "BUY" : side == "S" ? "SELL" : side;
            var quantity = Float(r["Qty"]);
            var rate = Float(r["Rate"]);
            result.Add(new JsonObject
            {
                ["symbol"] = symbol ?? Str(r["ScripName"]) ?? "", ["exchange"] = symbol is null ? Str(r["Exch"]) ?? "" : exchange!, ["product"] = symbol is null ? Str(r["DelvIntra"]) ?? "" : Product(exchange!, Str(r["DelvIntra"])),
                ["action"] = side ?? "", ["quantity"] = r["Qty"]?.DeepClone() ?? 0, ["average_price"] = r["Rate"]?.DeepClone() ?? 0.0, ["trade_value"] = BookFormatting.Round2(quantity * rate),
                ["orderid"] = r["ExchOrderID"]?.DeepClone() ?? "", ["timestamp"] = ConvertDate(r["ExchangeTradeTime"]),
            });
        }

        return result;
    }

    private async Task<List<JsonObject>> PositionRowsAsync(BrokerSession session, CancellationToken cancellationToken)
    {
        // A slow position read is tried again: it is asked with a long wait, and an empty answer would read as flat.
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                var (status, answer) = await PostAsync(session, $"{Svc}/V2/NetPositionNetWise", ClientBody(session), cancellationToken, TimeSpan.FromSeconds(60));
                if (status >= 400 || !Succeeded(answer)) throw new BrokerException($"{DisplayName} could not send the positions: {(Description(answer) is { Length: > 0 } d ? d : "no reason was given.")}", 500);
                return (Body(answer)["NetPositionDetail"] as JsonArray)?.OfType<JsonObject>().ToList() ?? [];
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && attempt < 3)
            {
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new BrokerException($"{DisplayName} took too long to send the positions. Try again in a moment.", 504);
            }
        }
    }

    public async Task<JsonArray> GetPositionsAsync(BrokerSession session, CancellationToken cancellationToken)
    {
        var result = new JsonArray();
        foreach (var r in await PositionRowsAsync(session, cancellationToken))
        {
            var exchange = ReverseExchange(Str(r["Exch"]), Str(r["ExchType"]));
            var symbol = await SymbolAsync(r, exchange, cancellationToken);
            result.Add(new JsonObject
            {
                ["symbol"] = symbol ?? Str(r["ScripName"]) ?? "", ["exchange"] = symbol is null ? Str(r["Exch"]) ?? "" : exchange!, ["product"] = symbol is null ? Str(r["OrderFor"]) ?? "" : Product(exchange!, Str(r["OrderFor"])),
                ["quantity"] = r["NetQty"]?.DeepClone() ?? 0, ["average_price"] = (Float(r["NetQty"]) > 0 ? r["BuyAvgRate"] : r["SellAvgRate"])?.DeepClone() ?? 0,
            });
        }

        return result;
    }

    public async Task<BrokerBook> GetHoldingsAsync(BrokerSession session, CancellationToken cancellationToken)
    {
        double value = 0, invested = 0;
        var holdings = new JsonArray();
        foreach (var r in await ReadRowsAsync(session, $"{Svc}/V3/Holding", "holdings", "Data", cancellationToken))
        {
            var quantity = Float(r["Quantity"]);
            var buyValue = Float(r["AvgRate"]) * quantity;
            var ltpValue = Float(r["CurrentPrice"]) * quantity;
            invested += buyValue;
            value += ltpValue;
            var pnl = ltpValue - buyValue;
            holdings.Add(new JsonObject
            {
                ["symbol"] = Str(r["Symbol"]) ?? "", ["exchange"] = Str(r["Exch"]) switch { "N" => "NSE", "B" => "BSE", var other => other ?? "" }, ["quantity"] = r["Quantity"]?.DeepClone() ?? 0, ["product"] = "CNC",
                ["pnl"] = BookFormatting.Round2(pnl), ["pnlpercent"] = buyValue != 0 ? BookFormatting.Round2(pnl / buyValue * 100) : 0.0,
            });
        }

        var total = value - invested;
        return new BrokerBook(holdings, new JsonObject { ["totalholdingvalue"] = value, ["totalinvvalue"] = invested, ["totalprofitandloss"] = total, ["totalpnlpercentage"] = invested != 0 ? total / invested * 100 : 0 });
    }

    // ---- funds ------------------------------------------------------------------------------------------------

    public async Task<JsonObject> GetFundsAsync(BrokerSession session, CancellationToken cancellationToken)
    {
        var (status, answer) = await PostAsync(session, $"{Svc}/V4/Margin", ClientBody(session), cancellationToken, accept: true);
        if (status >= 400) throw new BrokerException($"{DisplayName} could not send the funds. Try again in a moment.", 500);
        var margin = (Body(answer)["EquityMargin"] as JsonArray)?.FirstOrDefault() as JsonObject;
        if (margin is null) throw new BrokerException($"{DisplayName} could not send the funds: {(Str(Body(answer)["Message"]) is { Length: > 0 } m ? m : "no margin record was returned.")}", 500);

        // The open profit and loss comes from the positions; a position read that fails leaves it at zero, as in OpenAlgo.
        double booked = 0, mark = 0;
        try
        {
            foreach (var p in await PositionRowsAsync(session, cancellationToken))
            {
                booked += Truthy(p["BookedPL"]) ? Float(p["BookedPL"]) : 0;
                mark += Truthy(p["MTOM"]) ? Float(p["MTOM"]) : 0;
            }
        }
        catch (BrokerException)
        {
        }

        return new JsonObject
        {
            ["availablecash"] = BookFormatting.Fixed2(Float(margin["NetAvailableMargin"])), ["collateral"] = BookFormatting.Fixed2(Float(margin["TotalCollateralValue"])), ["m2munrealized"] = BookFormatting.Round2(mark),
            ["m2mrealized"] = BookFormatting.Round2(booked), ["utiliseddebits"] = BookFormatting.Fixed2(Float(margin["MarginUtilized"])),
        };
    }
}
