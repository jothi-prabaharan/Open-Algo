using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using EPTradeBot.Core.Brokers;
using EPTradeBot.Core.Brokers.Gtt;
using static EPTradeBot.Infrastructure.Brokers.JsonRead;

namespace EPTradeBot.Infrastructure.Brokers.Motilal;

/// <summary>
/// Motilal Oswal (openapi.motilaloswal.com): port of broker/motilal api/baseurl.py, api/order_api.py, api/funds.py, mapping/transform_data.py and mapping/order_data.py. Every call carries the documented common header set; the stored session is
/// "AuthToken:::accesstoken" (the access token only when the server has an API secret). Motilal answers with status SUCCESS or FAILURE, and a quantity goes out in lots.
/// </summary>
[BrokerPlugin]
public sealed partial class MotilalPlugin(IHttpClientFactory http, BrokerSessionFactory sessions, ISymbolMaster symbols, TimeProvider clock, IMotilalSockets? sockets = null) : IBrokerOrders, IBrokerPortfolio, IBrokerFunds, IBrokerLogin, IBrokerMultiQuotes, IBrokerHistory
{
    private const string DefaultBaseUrl = "https://openapi.motilaloswal.com";
    private const string Display = "Motilal Oswal";
    private const string Separator = ":::";

    public string Name => "motilal";
    public string DisplayName => "Motilal Oswal";
    public BrokerCapability Capabilities => BrokerCapability.Orders | BrokerCapability.Portfolio | BrokerCapability.Funds | BrokerCapability.Quotes | BrokerCapability.Depth | BrokerCapability.History | BrokerCapability.Streaming;
    public IReadOnlyList<string> RequiredKeys { get; } = ["ApiKey"];

    private static IMotilalSockets? defaultSockets;

    private IMotilalSockets Sockets => sockets ?? (defaultSockets ??= new MotilalSnapshotSocket());

    private HttpClient Client() => http.CreateClient(Name);

    private static readonly Dictionary<string, string> Endpoints = new(StringComparer.Ordinal)
    {
        ["authdirectapi"] = "/rest/login/v7/authdirectapi",
        ["getaccesstoken"] = "/rest/login/v1/getaccesstoken",
        ["placeorder"] = "/rest/trans/v2/placeorder",
        ["modifyorder"] = "/rest/trans/v5/modifyorder",
        ["cancelorder"] = "/rest/trans/v2/cancelorder",
        ["getorderbook"] = "/rest/book/v5/getorderbook",
        ["gettradebook"] = "/rest/book/v4/gettradebook",
        ["getorderdetail"] = "/rest/book/v5/getorderdetailbyuniqueorderid",
        ["getposition"] = "/rest/book/v4/getposition",
        ["getdpholding"] = "/rest/report/v3/getdpholding",
        ["getreportmargindetail"] = "/rest/report/v3/getreportmargindetail",
        ["getltpdata"] = "/rest/report/v3/getltpdata",
        ["getindexltpdata"] = "/rest/report/v3/getindexltpdata",
    };

    /// <summary>The stored session split into the AuthToken and the optional access token.</summary>
    private static (string? AuthToken, string? AccessToken) SplitToken(string? stored)
    {
        if (string.IsNullOrEmpty(stored)) return (null, null);
        var at = stored.IndexOf(Separator, StringComparison.Ordinal);
        if (at < 0) return (stored, null);
        var auth = stored[..at];
        var access = stored[(at + Separator.Length)..];
        return (auth.Length > 0 ? auth : null, access.Length > 0 ? access : null);
    }

    /// <summary>The documented common headers (doc 05). <paramref name="session"/> is null for the sign-in call, which must not send Authorization.</summary>
    private void AddHeaders(HttpRequestMessage request, IReadOnlyDictionary<string, string> keys, string? vendor, string? stored)
    {
        void Add(string name, string value) => request.Headers.TryAddWithoutValidation(name, value);
        Add("Accept", "application/json");
        Add("User-Agent", "MOSL/V.1.1.0");
        Add("ApiKey", keys.TryGetValue("ApiKey", out var key) ? key : "");
        Add("ClientLocalIp", "127.0.0.1");
        Add("ClientPublicIp", "127.0.0.1");
        Add("MacAddress", "00:00:00:00:00:00");
        Add("SourceId", "WEB");
        Add("vendorinfo", vendor ?? "");
        Add("osname", "Windows 10");
        Add("osversion", "10.0.19041");
        Add("devicemodel", "AHV");
        Add("manufacturer", "DELL");
        Add("productname", "OpenAlgo");
        Add("productversion", "1.0.0");
        Add("browsername", "Chrome");
        Add("browserversion", "120.0");
        if (keys.TryGetValue("ApiSecret", out var secret) && !string.IsNullOrEmpty(secret)) Add("apisecretkey", secret);
        var (auth, access) = SplitToken(stored);
        if (!string.IsNullOrEmpty(auth)) Add("Authorization", auth);
        if (!string.IsNullOrEmpty(access)) Add("accesstoken", access);
    }

    /// <summary>What a call came back with: the HTTP status, the body when it was a JSON object, and the reason when the call did not succeed.</summary>
    private sealed record Reply(int Status, JsonObject? Body, string? Failure)
    {
        public bool Ok => Failure is null;
    }

    private async Task<Reply> CallAsync(BrokerSession session, string endpoint, JsonNode? payload, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, sessions.BaseUrl(Name, DefaultBaseUrl) + Endpoints[endpoint]);
        AddHeaders(request, session.Keys, session.ClientId, session.AuthToken);
        request.Content = new StringContent(payload?.ToJsonString() ?? "", Encoding.UTF8, "application/json");
        int status;
        JsonNode? body;
        try
        {
            using var response = await Client().SendAsync(request, cancellationToken);
            status = (int)response.StatusCode;
            body = await ReadAsync(response, cancellationToken);
        }
        catch (HttpRequestException ex)
        {
            throw new BrokerException($"{Display} did not answer. Check your connection, then try again.", 502, ex);
        }

        if (body is not JsonObject o) return new Reply(status, null, body is null ? $"{Display} sent an answer that could not be read (HTTP {status})." : $"{Display} sent an answer that could not be read (HTTP {status}).");
        var raw = o.TryGetPropertyValue("status", out var node) ? (node is null ? "NONE" : Str(node) ?? "") : "";
        if (raw.Length == 0 || string.Equals(raw, "SUCCESS", StringComparison.OrdinalIgnoreCase)) return new Reply(status, o, null);
        var message = Truthy(o["message"]) ? Str(o["message"])! : "Motilal API returned FAILURE";
        var code = Truthy(o["errorcode"]) ? Str(o["errorcode"]) : null;
        return new Reply(status, o, code is { Length: > 0 } ? $"{message} (errorcode: {code})" : message);
    }

    private static bool Succeeded(Reply reply) => reply.Body is not null && string.Equals(Str(reply.Body["status"]), "SUCCESS", StringComparison.Ordinal);

    // ---- vocabulary ------------------------------------------------------------------------------------------

    private static string MotilalExchange(string exchange) => exchange switch { "NFO" => "NSEFO", "CDS" => "NSECD", "BFO" => "BSEFO", _ => exchange };

    private static string OpenAlgoExchange(string? exchange) => exchange switch { "NSEFO" => "NFO", "NSECD" => "CDS", "BSEFO" => "BFO", _ => exchange ?? "" };

    private static readonly HashSet<string> Derivatives = ["NFO", "CDS", "MCX", "BFO", "NSEFO", "NSECD", "BSEFO"];

    private static string OrderTypeOf(string? priceType) => priceType switch { "MARKET" => "MARKET", "LIMIT" => "LIMIT", "SL" => "STOPLOSS", "SL-M" => "STOPLOSS", _ => "MARKET" };

    /// <summary>map_product_type: the cash exchanges keep three products, a derivative has only NORMAL.</summary>
    private static string ProductOf(string? product, string exchange)
    {
        var p = (product ?? "").ToUpperInvariant();
        if (exchange is "NSE" or "BSE") return p switch { "CNC" => "DELIVERY", "MIS" => "VALUEPLUS", "NRML" => "NORMAL", _ => "VALUEPLUS" };
        if (exchange is "NFO" or "MCX" or "CDS" or "BFO" or "NCDEX" or "NSEFO" or "NSECD" or "BSEFO") return "NORMAL";
        return p switch { "CNC" => "DELIVERY", "MIS" => "VALUEPLUS", "NRML" => "NORMAL", _ => "VALUEPLUS" };
    }

    private static string ProductFrom(string? product) => (product ?? "").ToUpperInvariant() switch
    {
        "DELIVERY" => "CNC", "VALUEPLUS" => "MIS", "NORMAL" => "NRML", "SELLFROMDP" => "CNC", "BTST" => "CNC", "MTF" => "NRML", _ => "MIS",
    };

    private static string Money(double value) => BookFormatting.Fixed2(value);

    // ---- orders ----------------------------------------------------------------------------------------------

    /// <summary>The lot size to divide a quantity by: a cash share has none to find, a derivative with none is refused, since a lot of 1 would multiply the order by the real lot.</summary>
    private static (int Lot, string? Error) LotOf(SymbolRecord info, string symbol, string exchange)
    {
        if (info.LotSize > 0) return (info.LotSize, null);
        if (Derivatives.Contains(exchange.ToUpperInvariant()))
            return (0, $"The lot size of {symbol} on {exchange} is not in the master contract, so the order was not sent: a lot of 1 would multiply the order by the real lot size. Download the master contract again, then retry.");
        return (1, null);
    }

    private sealed record Priced(string OrderType, double Price);

    /// <summary>MARKET and SL-M are refused on a vendor channel (M01108), so a market order goes as a limit a slab beyond the last price and a stop-market as a stop-limit a slab beyond its trigger. When no price can be worked out the order goes as it was asked.</summary>
    private async Task<Priced> ProtectAsync(BrokerSession session, SymbolRecord info, BrokerOrder order, CancellationToken cancellationToken)
    {
        var action = order.Action.ToUpperInvariant();
        var type = OrderTypeOf(order.PriceType);
        var price = (double)order.Price;
        double? tick = info.TickSize is { } t && t > 0 ? t : null;
        if (order.PriceType == "MARKET")
        {
            double ltp;
            try
            {
                ltp = await LastPriceAsync(session, info, cancellationToken);
            }
            catch (BrokerException)
            {
                return new Priced(type, price);
            }

            if (ltp > 0) return new Priced("LIMIT", MarketPriceProtection.ProtectedPrice(ltp, action, MarketPriceProtection.InstrumentType(null, order.Symbol), tick));
        }
        else if (order.PriceType == "SL-M" && (double)order.TriggerPrice > 0)
        {
            return new Priced("STOPLOSS", MarketPriceProtection.ProtectedPrice((double)order.TriggerPrice, action, MarketPriceProtection.InstrumentType(null, order.Symbol), tick));
        }

        return new Priced(type, price);
    }

    public async Task<BrokerCallResult> PlaceAsync(BrokerSession session, BrokerOrder order, CancellationToken cancellationToken)
    {
        var info = await symbols.FindAsync(Name, order.Symbol, order.Exchange, cancellationToken);
        if (info is null || string.IsNullOrEmpty(info.Token)) return BrokerCallResult.Refused(400, $"{order.Symbol} on {order.Exchange} is not in the {Display} master contract. Check the symbol, or download the master contract again.");
        var (lot, lotError) = LotOf(info, order.Symbol, order.Exchange);
        if (lotError is not null) return BrokerCallResult.Refused(400, lotError);
        if (order.Quantity % lot != 0)
            return BrokerCallResult.Refused(400, $"Invalid quantity: {order.Quantity} shares is not a multiple of lot size {lot}. Valid quantities: {lot}, {lot * 2}, {lot * 3}, etc.");

        var priced = await ProtectAsync(session, info, order, cancellationToken);
        var body = new JsonObject
        {
            ["exchange"] = MotilalExchange(order.Exchange), ["symboltoken"] = long.TryParse(info.Token, NumberStyles.Integer, CultureInfo.InvariantCulture, out var token) ? token : 0, ["buyorsell"] = order.Action.ToUpperInvariant(),
            ["ordertype"] = priced.OrderType, ["producttype"] = ProductOf(order.Product, order.Exchange), ["orderduration"] = "DAY", ["price"] = priced.Price, ["triggerprice"] = (double)order.TriggerPrice,
            ["quantityinlot"] = order.Quantity / lot, ["disclosedquantity"] = order.DisclosedQuantity, ["amoorder"] = "N",
        };

        Reply reply;
        try
        {
            reply = await CallAsync(session, "placeorder", body, cancellationToken);
        }
        catch (BrokerException ex) when (ex.HttpStatus == 502)
        {
            return new BrokerCallResult(false, 502, $"The order was sent to {Display} but no reply came back, so it may have been placed. Check the order book on {Display} before trying again.", MayHaveBeenSent: true);
        }

        if (reply.Body is null) return new BrokerCallResult(false, 502, $"{Display} could not confirm the order, so it may have been placed. Check the order book on {Display} before trying again.", MayHaveBeenSent: true);
        if (Succeeded(reply))
        {
            var id = Truthy(reply.Body["uniqueorderid"]) ? Str(reply.Body["uniqueorderid"]) : null;
            if (id is { Length: > 0 }) return BrokerCallResult.Success(id);
            return new BrokerCallResult(false, 502, $"{Display} accepted the order but sent no order number, so it cannot be followed. Check the order book on {Display} before trying again.", MayHaveBeenSent: true);
        }

        return BrokerCallResult.Refused(reply.Status >= 400 ? reply.Status : 400, Truthy(reply.Body["message"]) ? Str(reply.Body["message"])! : "Failed to place order");
    }

    /// <summary>The fields a modify needs from the order: its last-modified time (an order never modified has "0", so its insert time is used) and what it has already traded.</summary>
    private async Task<(JsonObject? Row, string? Error)> OrderDetailAsync(BrokerSession session, string orderId, CancellationToken cancellationToken)
    {
        try
        {
            var detail = await CallAsync(session, "getorderdetail", new JsonObject { ["uniqueorderid"] = orderId }, cancellationToken);
            if (Succeeded(detail) && detail.Body!["data"] is JsonArray rows)
            {
                foreach (var row in rows.OfType<JsonObject>())
                    if (!Truthy(row["uniqueorderid"]) || Str(row["uniqueorderid"]) == orderId) return (row, null);
            }
        }
        catch (BrokerException)
        {
        }

        var book = await CallAsync(session, "getorderbook", null, cancellationToken);
        if (!Succeeded(book) || book.Body!["data"] is not JsonArray { Count: > 0 } list) return (null, book.Failure ?? "Failed to fetch order book");
        foreach (var row in list.OfType<JsonObject>())
            if (Str(row["uniqueorderid"]) == orderId) return (row, null);
        return (null, $"Order {orderId} not found in order book");
    }

    private static string LastModified(JsonObject row)
    {
        string Clean(string name) => Truthy(row[name]) ? (Str(row[name]) ?? "").Trim() : "";
        var last = Clean("lastmodifiedtime");
        if (last.Length > 0 && last != "0") return last;
        foreach (var field in new[] { "recordinserttime", "entrydatetime" })
        {
            var fallback = Clean(field);
            if (fallback.Length > 0 && fallback != "0") return fallback;
        }

        return last;
    }

    public async Task<BrokerCallResult> ModifyAsync(BrokerSession session, BrokerOrderChange change, CancellationToken cancellationToken)
    {
        (JsonObject? Row, string? Error) found;
        try
        {
            found = await OrderDetailAsync(session, change.OrderId, cancellationToken);
        }
        catch (BrokerException ex)
        {
            return BrokerCallResult.Refused(ex.HttpStatus, ex.Message);
        }

        if (found.Row is null) return BrokerCallResult.Refused(found.Error!.Contains("not found", StringComparison.OrdinalIgnoreCase) ? 404 : 500, found.Error!);
        var info = await symbols.FindAsync(Name, change.Symbol, change.Exchange, cancellationToken);
        if (info is null || string.IsNullOrEmpty(info.Token)) return BrokerCallResult.Refused(400, $"{change.Symbol} on {change.Exchange} is not in the {Display} master contract. Check the symbol, or download the master contract again.");
        var (lot, lotError) = LotOf(info, change.Symbol, change.Exchange);
        if (lotError is not null) return BrokerCallResult.Refused(400, lotError);
        if (change.Quantity % lot != 0)
            return BrokerCallResult.Refused(400, $"Invalid quantity for modify order: {change.Quantity} shares is not a multiple of lot size {lot}. Valid quantities: {lot}, {lot * 2}, {lot * 3}, etc.");

        var body = new JsonObject
        {
            ["uniqueorderid"] = change.OrderId, ["newordertype"] = OrderTypeOf(change.PriceType), ["neworderduration"] = "DAY", ["newprice"] = (double)change.Price, ["newtriggerprice"] = (double)change.TriggerPrice,
            ["newquantityinlot"] = change.Quantity / lot, ["newdisclosedquantity"] = change.DisclosedQuantity, ["newgoodtilldate"] = "", ["lastmodifiedtime"] = LastModified(found.Row),
            ["qtytradedtoday"] = Truthy(found.Row["qtytradedtoday"]) ? (int)Int(found.Row["qtytradedtoday"]) : 0,
        };

        Reply reply;
        try
        {
            reply = await CallAsync(session, "modifyorder", body, cancellationToken);
        }
        catch (BrokerException ex) when (ex.HttpStatus == 502)
        {
            return new BrokerCallResult(false, 502, $"The change was sent to {Display} but no reply came back, so it may have been made. Check the order book on {Display} before trying again.", MayHaveBeenSent: true);
        }

        if (reply.Body is null) return new BrokerCallResult(false, 502, $"{Display} sent an answer that could not be read, so the change may have been made. Check the order book on {Display} before trying again.", MayHaveBeenSent: true);
        if (Succeeded(reply)) return BrokerCallResult.Success(change.OrderId);
        return BrokerCallResult.Refused(reply.Status >= 400 ? reply.Status : 400, reply.Failure ?? (Truthy(reply.Body["message"]) ? Str(reply.Body["message"])! : "Failed to modify order"));
    }

    public async Task<BrokerCallResult> CancelAsync(BrokerSession session, string orderId, CancellationToken cancellationToken)
    {
        Reply reply;
        try
        {
            reply = await CallAsync(session, "cancelorder", new JsonObject { ["uniqueorderid"] = orderId }, cancellationToken);
        }
        catch (BrokerException ex) when (ex.HttpStatus == 502)
        {
            return new BrokerCallResult(false, 502, $"The cancel was sent to {Display} but no reply came back, so the order may have been cancelled. Check the order book on {Display} before trying again.", MayHaveBeenSent: true);
        }

        if (reply.Body is null) return new BrokerCallResult(false, 502, $"{Display} sent an answer that could not be read, so the order may have been cancelled. Check the order book on {Display} before trying again.", MayHaveBeenSent: true);
        if (Succeeded(reply)) return BrokerCallResult.Success(orderId);
        return BrokerCallResult.Refused(reply.Status >= 400 ? reply.Status : 400, reply.Failure ?? "Failed to cancel order");
    }
}
