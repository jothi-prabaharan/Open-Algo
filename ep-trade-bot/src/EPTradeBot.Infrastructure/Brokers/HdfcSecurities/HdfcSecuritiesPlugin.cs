using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using EPTradeBot.Core.Brokers;
using static EPTradeBot.Infrastructure.Brokers.JsonRead;

namespace EPTradeBot.Infrastructure.Brokers.HdfcSecurities;

/// <summary>
/// HDFC Securities (InvestRight, developer.hdfcsec.com). Port of broker/hdfcsecurities: api/order_api.py, api/funds.py, mapping/transform_data.py and mapping/order_data.py. The access token goes
/// in Authorization with no prefix, the api_key in the query, and a user agent is mandatory. An instrument is addressed by the parent exchange (NFO is NSE), an instrument segment and the
/// broker code; a derivative also spells out its expiry, strike, option type and the underlying's own broker code. Positions and holdings carry no price, so one batched /fetch-ltp call prices them.
/// Not ported yet: quotes, depth, several quotes, sign-in and the feed (history and margin are not offered).
/// </summary>
[BrokerPlugin]
public sealed partial class HdfcSecuritiesPlugin(IHttpClientFactory http, BrokerSessionFactory sessions, ISymbolMaster symbols) : IBrokerOrders, IBrokerPortfolio, IBrokerFunds
{
    private const string DefaultBaseUrl = "https://developer.hdfcsec.com";
    private const string UserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/123.0.0.0 Safari/537.36";
    private const int LtpBatch = 10;

    public string Name => "hdfcsecurities";
    public string DisplayName => "HDFC Securities";
    public BrokerCapability Capabilities => BrokerCapability.Orders | BrokerCapability.Portfolio | BrokerCapability.Funds;
    public IReadOnlyList<string> RequiredKeys { get; } = ["ApiKey", "ApiSecret"];

    // ---- vocabulary ------------------------------------------------------------------------------------------

    private static readonly HashSet<string> CashExchanges = ["NSE", "BSE", "NSE_INDEX", "BSE_INDEX"];

    private static string RestExchange(string exchange) => exchange switch { "NFO" => "NSE", "BFO" => "BSE", "CDS" => "NSE", "NSE_INDEX" => "NSE", "BSE_INDEX" => "BSE", _ => exchange };

    private static string OaExchange(string? restExchange, string? segment)
    {
        var exchange = (restExchange ?? "").ToUpperInvariant();
        var seg = (segment ?? "").ToUpperInvariant();
        if (seg.Length > 0)
        {
            var mapped = (exchange, seg) switch
            {
                ("NSE", "EQUITY") => "NSE", ("NSE", "FUTIDX" or "OPTIDX" or "FUTSTK" or "OPTSTK") => "NFO", ("NSE", "FUTCUR" or "OPTCUR" or "UNDCUR") => "CDS",
                ("BSE", "EQUITY") => "BSE", ("BSE", "FUTIDX" or "OPTIDX" or "FUTSTK" or "OPTSTK") => "BFO", ("MCX", "FUTCOM" or "OPTFUT" or "FUTIDX" or "OPTIDX" or "COM") => "MCX",
                _ => null,
            };
            if (mapped is not null) return mapped;
        }

        return exchange;
    }

    private static string OrderType(string? priceType) => (priceType ?? "").ToUpperInvariant() switch { "MARKET" => "MARKET", "LIMIT" => "LIMIT", "SL" => "SL", "SL-M" => "SL-M", _ => "MARKET" };

    private static string ReverseOrderType(string? type) => (type ?? "").ToUpperInvariant() switch { "MARKET" => "MARKET", "LIMIT" => "LIMIT", "SL" or "SL-L" => "SL", "SLM" or "SL-M" => "SL-M", _ => type ?? "" };

    private static string Product(string? product, string exchange)
    {
        var p = (product ?? "").ToUpperInvariant();
        return CashExchanges.Contains(exchange) ? p switch { "CNC" or "NRML" => "DELIVERY", "MIS" => "INTRADAY", _ => "INTRADAY" } : p switch { "CNC" or "NRML" => "OVERNIGHT", "MIS" => "INTRADAY", _ => "INTRADAY" };
    }

    private static string? ReverseProduct(string? product) => (product ?? "").ToUpperInvariant() switch
    {
        "DELIVERY" or "MTF" or "COLL-SELL" or "ENCASH" => "CNC", "OVERNIGHT" => "NRML", "INTRADAY" or "COVER" => "MIS", _ => null,
    };

    private static string ReverseSide(string? side) => (side ?? "").ToUpperInvariant();

    private static string NormaliseStatus(string? status) => string.Join(' ', (status ?? "").Replace('_', ' ').Replace('-', ' ').ToUpperInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries));

    private static readonly Dictionary<string, string> StatusMap = new()
    {
        ["TRADED"] = "complete", ["EXECUTED"] = "complete", ["COMPLETE"] = "complete", ["COMPLETED"] = "complete", ["FULLY EXECUTED"] = "complete",
        ["REJECTED"] = "rejected", ["CANCEL REJECTED"] = "rejected", ["MODIFY REJECTED"] = "rejected", ["CANCELLED"] = "cancelled", ["CANCELED"] = "cancelled", ["CANCEL CONFIRMED"] = "cancelled",
        ["TRIGGER PENDING"] = "trigger pending", ["SL TRIGGER PENDING"] = "trigger pending",
        ["PENDING"] = "open", ["OPEN"] = "open", ["PLACED"] = "open", ["ACCEPTED"] = "open", ["CONFIRMED"] = "open", ["RECEIVED"] = "open", ["MODIFIED"] = "open", ["MODIFY PENDING"] = "open",
        ["CANCEL PENDING"] = "open", ["PARTIALLY TRADED"] = "open", ["PARTIAL TRADE"] = "open", ["PUT ORDER REQ RECEIVED"] = "open", ["VALIDATION PENDING"] = "open",
        ["AFTER MARKET ORDER REQ RECEIVED"] = "open", ["OPEN PENDING"] = "open", ["TRANSIT"] = "open",
    };

    private static string MapStatus(string? status)
    {
        var n = NormaliseStatus(status);
        return StatusMap.TryGetValue(n, out var mapped) ? mapped : n.ToLowerInvariant();
    }

    private static double Flt(JsonNode? node, double fallback = 0.0)
    {
        if (BookFormatting.Number(node) is { } n) return n;
        return Str(node) is { } s && double.TryParse(s.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : fallback;
    }

    private static long Whole(JsonNode? node) => (long)Math.Truncate(Flt(node));

    // ---- calls -----------------------------------------------------------------------------------------------

    private HttpClient Client() => http.CreateClient(Name);

    private async Task<(int Status, JsonNode? Body)> CallAsync(BrokerSession session, HttpMethod method, string path, IReadOnlyDictionary<string, string>? query, JsonNode? body, bool authorised, CancellationToken cancellationToken)
    {
        var parts = new List<string> { "api_key=" + Uri.EscapeDataString(session.Key("ApiKey") ?? "") };
        foreach (var (k, v) in query ?? new Dictionary<string, string>()) parts.Add($"{Uri.EscapeDataString(k)}={Uri.EscapeDataString(v)}");
        using var request = new HttpRequestMessage(method, sessions.BaseUrl(Name, DefaultBaseUrl) + path + "?" + string.Join("&", parts));
        if (authorised) request.Headers.TryAddWithoutValidation("Authorization", session.AuthToken);
        request.Headers.TryAddWithoutValidation("User-Agent", UserAgent);
        request.Headers.TryAddWithoutValidation("Accept", "application/json");
        if (body is not null) request.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
        try
        {
            using var response = await Client().SendAsync(request, cancellationToken);
            return ((int)response.StatusCode, await JsonRead.ReadAsync(response, cancellationToken));
        }
        catch (HttpRequestException ex)
        {
            throw new BrokerException($"{DisplayName} did not answer. Check your connection, then try again.", 502, ex);
        }
    }

    /// <summary>A read: an error status, an error body or something that is not JSON is a refusal a trader can read, never an empty book.</summary>
    private async Task<JsonObject> ReadAsync(BrokerSession session, string path, string what, CancellationToken cancellationToken)
    {
        var (status, body) = await CallAsync(session, HttpMethod.Get, path, null, null, true, cancellationToken);
        var payload = body as JsonObject;
        if (status >= 400 || payload is null || Str(payload["status"]) == "error" || Truthy(payload["error"]))
        {
            var why = payload is null ? null : Str(payload["message"]) is { Length: > 0 } m ? m : Str(payload["error"]);
            throw new BrokerException($"{DisplayName} could not send the {what}: {why ?? "no reason was given."}", 500);
        }

        return payload;
    }

    private static string Message(JsonNode? body, string fallback) => Str((body as JsonObject)?["message"]) is { Length: > 0 } m ? m : fallback;

    // ---- orders ----------------------------------------------------------------------------------------------

    private static long lastReference;

    /// <summary>A caller-side tracking number: a millisecond counter that never repeats within the process.</summary>
    private static string ExternalReference()
    {
        long value, last;
        do
        {
            last = Interlocked.Read(ref lastReference);
            value = Math.Max(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), last + 1);
        }
        while (Interlocked.CompareExchange(ref lastReference, value, last) != last);
        return value.ToString(CultureInfo.InvariantCulture);
    }

    private static readonly HashSet<string> NfoIndexUnderlyings = ["NIFTY", "BANKNIFTY", "FINNIFTY", "MIDCPNIFTY", "NIFTYNXT50"];
    private static readonly HashSet<string> BfoIndexUnderlyings = ["SENSEX", "SENSEX50", "BANKEX", "FOCIT"];
    private static readonly HashSet<string> McxIndexUnderlyings = ["MCXBULLDEX", "MCXMETLDEX"];

    private async Task<SymbolRecord?> UnderlyingAsync(string name, string exchange, CancellationToken cancellationToken)
    {
        var candidates = exchange switch { "NFO" => new[] { "NSE_INDEX", "NSE" }, "BFO" => ["BSE_INDEX", "BSE"], "CDS" => ["CDS"], "MCX" => ["MCX"], _ => [] };
        foreach (var candidate in candidates)
        {
            if (candidate == exchange) continue;
            if (await symbols.FindAsync(Name, name, candidate, cancellationToken) is { } found) return found;
        }

        return null;
    }

    private static string Segment(string exchange, string instrumentType, string name, SymbolRecord? underlying)
    {
        var option = instrumentType is "CE" or "PE";
        if (exchange is "NSE" or "BSE" or "NSE_INDEX" or "BSE_INDEX") return "EQUITY";
        if (exchange == "CDS") return option ? "OPTCUR" : "FUTCUR";
        var index = underlying is not null ? underlying.Exchange is "NSE_INDEX" or "BSE_INDEX" : (exchange switch { "NFO" => NfoIndexUnderlyings, "BFO" => BfoIndexUnderlyings, "MCX" => McxIndexUnderlyings, _ => [] }).Contains(name);
        if (exchange == "MCX") return index ? (option ? "OPTIDX" : "FUTIDX") : (option ? "OPTFUT" : "FUTCOM");
        return index ? (option ? "OPTIDX" : "FUTIDX") : (option ? "OPTSTK" : "FUTSTK");
    }

    private static string OrderExpiry(string? expiry) =>
        DateTime.TryParseExact((expiry ?? "").ToUpperInvariant(), "dd-MMM-yy", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d.ToString("yyyyMMdd", CultureInfo.InvariantCulture) : "";

    public async Task<BrokerCallResult> PlaceAsync(BrokerSession session, BrokerOrder order, CancellationToken cancellationToken)
    {
        var info = await symbols.FindAsync(Name, order.Symbol, order.Exchange, cancellationToken);
        if (info is null) return BrokerCallResult.Refused(400, $"No {DisplayName} instrument found for {order.Exchange}:{order.Symbol}");
        var instrumentType = (info.InstrumentType ?? "").ToUpperInvariant();
        var name = info.Name ?? "";
        var underlying = instrumentType != "EQ" ? await UnderlyingAsync(name, order.Exchange, cancellationToken) : null;

        var payload = new JsonObject
        {
            ["exchange"] = RestExchange(order.Exchange), ["security_id"] = info.BrokerSymbol, ["instrument_segment"] = Segment(order.Exchange, instrumentType, name, underlying),
            ["transaction_type"] = order.Action.Equals("BUY", StringComparison.OrdinalIgnoreCase) ? "BUY" : "SELL", ["product"] = Product(order.Product, order.Exchange), ["order_type"] = OrderType(order.PriceType),
            ["quantity"] = order.Quantity, ["price"] = (double)order.Price, ["trigger_price"] = (double)order.TriggerPrice, ["disclosed_quantity"] = order.DisclosedQuantity, ["validity"] = "DAY", ["amo"] = false,
            ["external_reference_number"] = ExternalReference(),
        };
        if (instrumentType is "FUT" or "CE" or "PE")
        {
            var derivativeUnderlying = await UnderlyingAsync(name, order.Exchange, cancellationToken);
            payload["expiry_date"] = OrderExpiry(info.Expiry);
            payload["underlying_symbol"] = derivativeUnderlying is { } u ? (string.IsNullOrEmpty(u.BrokerSymbol) ? name : u.BrokerSymbol) : name;
            if (instrumentType is "CE" or "PE")
            {
                payload["option_type"] = instrumentType;
                payload["strike_price"] = info.Strike ?? 0.0;
            }
        }

        (int Status, JsonNode? Body) reply;
        try
        {
            reply = await CallAsync(session, HttpMethod.Post, "/oapi/v1/orders/regular", null, payload, true, cancellationToken);
        }
        catch (BrokerException ex) when (ex.HttpStatus == 502)
        {
            return new BrokerCallResult(false, 502, $"The order was sent to {DisplayName} but no reply came back, so it may have been placed. Check the order book on {DisplayName} before trying again.", MayHaveBeenSent: true);
        }

        var body = reply.Body as JsonObject;
        if (Str(body?["status"]) == "success" && Str((body?["data"] as JsonObject)?["order_id"]) is { Length: > 0 } id) return BrokerCallResult.Success(id);
        return BrokerCallResult.Refused(reply.Status >= 400 ? reply.Status : 400, Message(body, "Failed to place order"));
    }

    public async Task<BrokerCallResult> ModifyAsync(BrokerSession session, BrokerOrderChange change, CancellationToken cancellationToken)
    {
        var payload = new JsonObject
        {
            ["quantity"] = change.Quantity, ["order_type"] = OrderType(change.PriceType), ["validity"] = "DAY", ["disclosed_quantity"] = change.DisclosedQuantity,
            ["product"] = Product(change.Product, change.Exchange), ["price"] = (double)change.Price, ["trigger_price"] = (double)change.TriggerPrice, ["amo"] = false,
        };
        var (_, body) = await CallAsync(session, HttpMethod.Put, $"/oapi/v1/orders/regular/{Uri.EscapeDataString(change.OrderId)}", null, payload, true, cancellationToken);
        var reply = body as JsonObject;
        if (Str(reply?["status"]) == "success") return BrokerCallResult.Success(Str((reply?["data"] as JsonObject)?["order_id"]) is { Length: > 0 } id ? id : change.OrderId);
        return BrokerCallResult.Refused(400, Message(reply, "Failed to modify order"));
    }

    public async Task<BrokerCallResult> CancelAsync(BrokerSession session, string orderId, CancellationToken cancellationToken)
    {
        var (_, body) = await CallAsync(session, HttpMethod.Delete, $"/oapi/v1/orders/regular/{Uri.EscapeDataString(orderId)}", null, null, true, cancellationToken);
        var reply = body as JsonObject;
        if (Str(reply?["status"]) == "success") return BrokerCallResult.Success(Str((reply?["data"] as JsonObject)?["order_id"]) is { Length: > 0 } id ? id : orderId);
        return BrokerCallResult.Refused(400, Message(reply, "Failed to cancel order"));
    }

    // ---- symbols ---------------------------------------------------------------------------------------------

    private static string CompactExpiry(string? expiry)
    {
        var parts = (expiry ?? "").Replace('-', ' ').ToUpperInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 3 || !parts[0].All(char.IsDigit)) return "";
        if (!new[] { "JAN", "FEB", "MAR", "APR", "MAY", "JUN", "JUL", "AUG", "SEP", "OCT", "NOV", "DEC" }.Contains(parts[1])) return "";
        var year = parts[2];
        return $"{int.Parse(parts[0], CultureInfo.InvariantCulture):00}{parts[1]}{(year.Length >= 2 ? year[^2..] : year)}";
    }

    private static string FormatStrike(JsonNode? strike)
    {
        var value = Flt(strike);
        return value == Math.Truncate(value) ? ((long)value).ToString(CultureInfo.InvariantCulture) : PyFloat(value);
    }

    private static string PyFloat(double value)
    {
        var text = value.ToString("R", CultureInfo.InvariantCulture);
        return text.Contains('.', StringComparison.Ordinal) || text.Contains('E', StringComparison.Ordinal) ? text : text + ".0";
    }

    private static string ReverseOptionType(string? type) => (type ?? "").ToUpperInvariant() switch { "CALL" or "CE" => "CE", "PUT" or "PE" => "PE", _ => "" };

    private async Task<string> UnderlyingNameAsync(JsonObject row, string exchange, bool verify, CancellationToken cancellationToken)
    {
        var name = (Str(row["underlying_symbol"]) is { Length: > 0 } u ? u : Str(row["company_name"]) ?? "").Trim();
        if (name.Length > 0) return name;
        var candidate = (Str(row["security_id"]) ?? "").Trim();
        if (candidate.Length == 0 || candidate.All(char.IsDigit)) return "";
        if (verify && !await symbols.HasNameAsync(Name, candidate, exchange, cancellationToken)) return "";
        return candidate;
    }

    /// <summary>The OpenAlgo symbol of a broker row: its code in the master contract, else rebuilt from the parts it spells out, else the code, else the ISIN.</summary>
    private async Task<string> OaSymbolAsync(JsonObject row, string exchange, CancellationToken cancellationToken)
    {
        var securityId = (Str(row["security_id"]) ?? "").Trim();
        if (securityId.Length > 0 && (await symbols.FindByBrokerSymbolAsync(Name, securityId, exchange, cancellationToken))?.Symbol is { Length: > 0 } resolved) return resolved;

        var expiry = CompactExpiry(Str(row["expiry_date"]));
        var underlying = await UnderlyingNameAsync(row, exchange, expiry.Length > 0, cancellationToken);
        if (underlying.Length > 0)
        {
            if (expiry.Length == 0) return underlying;
            return ReverseOptionType(Str(row["option_type"])) is { Length: > 0 } option ? $"{underlying}{expiry}{FormatStrike(row["strike_price"])}{option}" : $"{underlying}{expiry}FUT";
        }

        if (securityId.Length > 0) return securityId;
        return (Str(row["isin"]) ?? "").Trim();
    }

    private async Task<(string Exchange, string Symbol, JsonNode? Product)> NormaliseAsync(JsonObject row, CancellationToken cancellationToken)
    {
        var exchange = OaExchange(Str(row["exchange"]), Str(row["instrument_segment"]));
        var symbol = await OaSymbolAsync(row, exchange, cancellationToken);
        JsonNode? product = ReverseProduct(Str(row["product"])) is { } p ? JsonValue.Create(p) : Same(row["product"]);
        return (exchange, symbol, product);
    }

    private static JsonNode? Same(JsonNode? node) => node?.DeepClone();

    // ---- books -----------------------------------------------------------------------------------------------

    private static List<JsonObject> Rows(JsonObject body) => (body["data"] as JsonArray ?? []).OfType<JsonObject>().ToList();

    public async Task<BrokerBook> GetOrderBookAsync(BrokerSession session, CancellationToken cancellationToken)
    {
        int buy = 0, sell = 0, completed = 0, open = 0, rejected = 0;
        var rows = new JsonArray();
        foreach (var o in Rows(await ReadAsync(session, "/oapi/v1/orders", "order book", cancellationToken)))
        {
            var (exchange, symbol, product) = await NormaliseAsync(o, cancellationToken);
            var side = ReverseSide(Str(o["transaction_type"]));
            var status = MapStatus(Str(o["status"]));
            if (side == "BUY") buy++;
            else if (side == "SELL") sell++;
            if (status == "complete") completed++;
            else if (status is "open" or "trigger pending") open++;
            else if (status == "rejected") rejected++;
            rows.Add(new JsonObject
            {
                ["symbol"] = symbol, ["exchange"] = exchange, ["action"] = side, ["quantity"] = Whole(o["quantity"]), ["price"] = Flt(o["price"]), ["trigger_price"] = Flt(o["trigger_price"]),
                ["pricetype"] = ReverseOrderType(Str(o["order_type"])), ["product"] = product ?? "", ["orderid"] = Str(o["order_id"]) ?? "", ["order_status"] = status, ["timestamp"] = Same(o["order_timestamp"]) ?? "",
            });
        }

        return new BrokerBook(rows, new JsonObject
        {
            ["total_buy_orders"] = buy, ["total_sell_orders"] = sell, ["total_completed_orders"] = completed, ["total_open_orders"] = open, ["total_rejected_orders"] = rejected,
        });
    }

    public async Task<JsonArray> GetTradeBookAsync(BrokerSession session, CancellationToken cancellationToken)
    {
        var result = new JsonArray();
        foreach (var t in Rows(await ReadAsync(session, "/oapi/v1/trades", "trade book", cancellationToken)))
        {
            var (exchange, symbol, product) = await NormaliseAsync(t, cancellationToken);
            var quantity = Whole(t["filled_quantity"]);
            var average = Flt(t["average_price"]);
            var value = Flt(t["total_traded_value"], quantity * average);
            result.Add(new JsonObject
            {
                ["symbol"] = symbol, ["exchange"] = exchange, ["product"] = product ?? "", ["action"] = ReverseSide(Str(t["transaction_type"])), ["quantity"] = quantity, ["average_price"] = average,
                ["trade_value"] = BookFormatting.Round2(value), ["orderid"] = Str(t["order_id"]) ?? "", ["timestamp"] = Same(t["fill_timestamp"]) ?? "",
            });
        }

        return result;
    }

    private static List<JsonObject> PositionRows(JsonObject payload)
    {
        if (payload["data"] is JsonArray list) return list.OfType<JsonObject>().ToList();
        if (payload["data"] is JsonObject data)
        {
            if (data["net"] is JsonArray net) return net.OfType<JsonObject>().ToList();
            var first = data.Select(kv => kv.Value).OfType<JsonArray>().FirstOrDefault();
            return first?.OfType<JsonObject>().ToList() ?? [];
        }

        return [];
    }

    /// <summary>Gives each row the last price from one batched /fetch-ltp call (ten instruments at a time). A row that cannot be resolved, or a batch that fails, is left without one.</summary>
    private async Task EnrichWithLtpAsync(BrokerSession session, List<JsonObject> rows, CancellationToken cancellationToken)
    {
        var wanted = new Dictionary<int, (string Exchange, string Token)>();
        for (var i = 0; i < rows.Count; i++)
        {
            var securityId = (Str(rows[i]["security_id"]) ?? "").Trim();
            if (securityId.Length == 0) continue;
            var exchange = OaExchange(Str(rows[i]["exchange"]), Str(rows[i]["instrument_segment"]));
            if (await symbols.FindByBrokerSymbolAsync(Name, securityId, exchange, cancellationToken) is { } match) wanted[i] = (match.Exchange, match.Token);
        }

        if (wanted.Count == 0) return;
        var instruments = wanted.Values.Distinct().ToList();
        var quotes = new Dictionary<(string, string), double>();
        for (var i = 0; i < instruments.Count; i += LtpBatch)
        {
            foreach (var (key, ltp) in await FetchLtpAsync(session, instruments.Skip(i).Take(LtpBatch).ToList(), cancellationToken)) quotes[key] = ltp.Ltp;
        }

        foreach (var (index, key) in wanted)
        {
            if (quotes.TryGetValue(key, out var ltp)) rows[index]["ltp"] = ltp;
        }
    }

    private async Task<Dictionary<(string Exchange, string Token), (double Ltp, double PrevClose)>> FetchLtpAsync(BrokerSession session, List<(string Exchange, string Token)> instruments, CancellationToken cancellationToken)
    {
        var result = new Dictionary<(string, string), (double, double)>();
        var body = new JsonObject { ["data"] = new JsonArray(instruments.Select(i => (JsonNode?)new JsonObject { ["exchange"] = i.Exchange, ["token"] = i.Token }).ToArray()) };
        (int Status, JsonNode? Body) reply = (0, null);
        try
        {
            for (var attempt = 0; attempt < 4; attempt++)
            {
                reply = await CallAsync(session, HttpMethod.Put, "/oapi/v1/fetch-ltp", null, body, true, cancellationToken);
                // A rate-limited batch is tried again rather than dropped: no price would read as a price of zero.
                if (reply.Status == 429 && attempt < 3)
                {
                    await Task.Delay(sessions.RetryBackoff(Name, TimeSpan.FromSeconds(0.5 * (1 << attempt))), cancellationToken);
                    continue;
                }

                break;
            }
        }
        catch (BrokerException)
        {
            return result;
        }

        if (reply.Status != 200 || reply.Body is not JsonObject payload) return result;
        var requested = new Dictionary<string, HashSet<string>>();
        foreach (var (exchange, token) in instruments)
        {
            if (!requested.TryGetValue(token, out var set)) requested[token] = set = [];
            set.Add(exchange.ToUpperInvariant());
        }

        foreach (var row in (payload["data"] as JsonArray ?? []).OfType<JsonObject>())
        {
            var token = Str(row["token"]) ?? "";
            var exchange = (Str(row["exchange"]) ?? "").ToUpperInvariant();
            if (exchange.Length == 0)
            {
                if (!requested.TryGetValue(token, out var codes) || codes.Count != 1) continue;
                exchange = codes.First();
            }

            result[(exchange, token)] = (Flt(row["ltp"]), Flt(row["prev_close"]));
        }

        return result;
    }

    private async Task<List<JsonObject>> PricedPositionsAsync(BrokerSession session, CancellationToken cancellationToken)
    {
        var rows = PositionRows(await ReadAsync(session, "/oapi/v1/portfolio/cumulative-positions", "positions", cancellationToken));
        await EnrichWithLtpAsync(session, rows, cancellationToken);
        return rows;
    }

    public async Task<JsonArray> GetPositionsAsync(BrokerSession session, CancellationToken cancellationToken)
    {
        var result = new JsonArray();
        foreach (var p in await PricedPositionsAsync(session, cancellationToken))
        {
            var (exchange, symbol, product) = await NormaliseAsync(p, cancellationToken);
            var net = Whole(p["net_qty"]);
            var ltp = Flt(p["ltp"]);
            var pnl = (Flt(p["total_sell_value"]) - Flt(p["total_buy_value"])) + (net * ltp);
            var average = net > 0 ? Flt(p["average_buy_price"]) : net < 0 ? Flt(p["average_sell_price"]) : 0.0;
            result.Add(new JsonObject
            {
                ["symbol"] = symbol, ["exchange"] = exchange, ["product"] = product ?? "", ["quantity"] = net, ["pnl"] = BookFormatting.Round2(pnl),
                ["average_price"] = BookFormatting.Fixed2(average), ["ltp"] = BookFormatting.Round2(ltp),
            });
        }

        return result;
    }

    public async Task<BrokerBook> GetHoldingsAsync(BrokerSession session, CancellationToken cancellationToken)
    {
        var payload = await ReadAsync(session, "/oapi/v1/portfolio/holdings", "holdings", cancellationToken);
        var rows = Rows(payload);
        await EnrichWithLtpAsync(session, rows, cancellationToken);

        var holdings = new JsonArray();
        double value = 0, invested = 0;
        foreach (var h in rows)
        {
            var exchange = OaExchange(Str(h["exchange"]), "EQUITY");
            var symbol = await OaSymbolAsync(h, exchange, cancellationToken);
            var quantity = Whole(h["quantity"]);
            var average = Flt(h["average_price"]);
            var ltp = Flt(h["ltp"], Flt(h["close_price"]));
            value += ltp * quantity;
            invested += average * quantity;
            holdings.Add(new JsonObject
            {
                ["symbol"] = symbol, ["exchange"] = exchange, ["quantity"] = quantity, ["product"] = "CNC", ["average_price"] = BookFormatting.Round2(average),
                ["pnl"] = BookFormatting.Round2((ltp - average) * quantity), ["pnlpercent"] = average != 0 ? BookFormatting.Round2((ltp - average) / average * 100) : 0.0,
            });
        }

        var pnlTotal = value - invested;
        return new BrokerBook(holdings, new JsonObject
        {
            ["totalholdingvalue"] = value, ["totalinvvalue"] = invested, ["totalprofitandloss"] = pnlTotal, ["totalpnlpercentage"] = invested != 0 ? pnlTotal / invested * 100 : 0.0,
        });
    }

    // ---- funds -----------------------------------------------------------------------------------------------

    public async Task<JsonObject> GetFundsAsync(BrokerSession session, CancellationToken cancellationToken)
    {
        var payload = await ReadAsync(session, "/oapi/v1/user/margins", "funds", cancellationToken);
        if ((payload["data"] as JsonObject)?["equity"] is not JsonObject equity) throw new BrokerException($"{DisplayName} sent funds EP Trade bot could not read. Try again in a moment.", 502);
        var limits = equity["totalLimitDetails"] as JsonObject ?? [];

        double realised = 0, unrealised = 0;
        try
        {
            foreach (var p in await PricedPositionsAsync(session, cancellationToken))
            {
                realised += Flt(p["realised_pl_overall_position"]);
                var net = Flt(p["net_qty"]);
                if (net == 0) continue;
                var ltp = Flt(p["ltp"]);
                if (ltp == 0) continue;
                var cost = net > 0 ? Flt(p["average_buy_price"]) : Flt(p["average_sell_price"]);
                unrealised += (ltp - cost) * net;
            }
        }
        catch (BrokerException)
        {
            (realised, unrealised) = (0, 0);
        }

        return new JsonObject
        {
            ["availablecash"] = BookFormatting.Fixed2(Money(equity["total_available_limit"])), ["collateral"] = BookFormatting.Fixed2(Money(limits["pledge_limit"])),
            ["m2munrealized"] = BookFormatting.Fixed2(unrealised), ["m2mrealized"] = BookFormatting.Fixed2(realised), ["utiliseddebits"] = BookFormatting.Fixed2(Money(equity["total_utilised_limit"])),
        };
    }

    /// <summary>A figure that may arrive as text with thousands separators.</summary>
    private static double Money(JsonNode? node) => double.TryParse((Str(node) ?? "").Replace(",", "").Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : 0.0;
}
