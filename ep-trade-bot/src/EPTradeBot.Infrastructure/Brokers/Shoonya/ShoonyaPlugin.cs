using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using EPTradeBot.Core.Brokers;
using EPTradeBot.Core.Brokers.Gtt;
using Microsoft.Extensions.Logging;
using static EPTradeBot.Infrastructure.Brokers.JsonRead;

namespace EPTradeBot.Infrastructure.Brokers.Shoonya;

/// <summary>
/// Shoonya (broker/shoonya), a Noren API broker: every call is a POST of <c>jData=&lt;json&gt;</c> in a text/plain body with a bearer token.
/// Port of api/order_api.py, api/funds.py, api/data.py (quotes and depth) and mapping/. Held to tools/contract-fixtures/shoonya.fixtures.json.
/// Market and stop-market orders are priced like OpenAlgo's market price protection (Shoonya refuses both types over the API).
/// Not ported yet, and refused rather than guessed: history, margin and the feed.
/// </summary>
public abstract partial class NorenPluginBase(IHttpClientFactory http, BrokerSessionFactory sessions, ISymbolMaster symbols, ILogger logger)
    : IBrokerOrders, IBrokerPortfolio, IBrokerFunds, IBrokerMultiQuotes, IBrokerLogin
{
    /// <summary>The path every call lives under (Shoonya and Zebu: /NorenWClientAPI; Flattrade: /PiConnectAPI).</summary>
    protected virtual string Api => "/NorenWClientAPI";
    private const int QuoteAttempts = 3;

    public abstract string Name { get; }
    public abstract string DisplayName { get; }

    /// <summary>The broker's API address.</summary>
    protected abstract string Host { get; }

    /// <summary>Whether a stop-market order is priced from a quote and sent as a stop-limit (Shoonya refuses the stop-market type over the API; Zebu takes it).</summary>
    protected virtual bool ProtectsStopMarket => true;

    /// <summary>Whether a quote is checked to be for the instrument asked about and asked for again when it is not (a fault seen on Shoonya's Noren backend; Zebu runs the same one).</summary>
    protected virtual bool GuardsQuotes => true;
    public virtual BrokerCapability Capabilities => BrokerCapability.Orders | BrokerCapability.Portfolio | BrokerCapability.Funds | BrokerCapability.Quotes | BrokerCapability.Depth;
    public IReadOnlyList<string> RequiredKeys { get; } = ["ApiKey", "ApiSecret"];

    // ---- plumbing --------------------------------------------------------------------------------------------

    /// <summary>The trading user id: the part of the saved key before ":::".</summary>
    private string UserId(BrokerSession session) =>
        session.Key("ApiKey") is { Length: > 0 } key ? key.Split(":::")[0] : throw new BrokerException($"The {DisplayName} key is not set on this server. Add it to the server's settings.", 409);

    /// <summary>Python's str(float): "800.5", "24010.0", "0.0".</summary>
    private static string PyFloat(double value)
    {
        var text = value.ToString("R", CultureInfo.InvariantCulture);
        return text.Contains('E') || text.Contains('.') ? text : text + ".0";
    }

    /// <summary>The jData value. Shoonya splits the body on "&amp;" before it reads the JSON, so a literal ampersand is sent as the JSON escape.</summary>
    private static string JData(JsonObject payload) => payload.ToJsonString(new System.Text.Json.JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }).Replace("&", "\\u0026", StringComparison.Ordinal);

    protected virtual async Task<JsonNode?> PostAsync(BrokerSession session, string endpoint, JsonObject payload, CancellationToken cancellationToken, bool book = false)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, sessions.BaseUrl(Name, Host) + Api + endpoint)
        {
            Content = new StringContent("jData=" + JData(payload), Encoding.UTF8, "text/plain"),
        };
        request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {session.AuthToken}");
        try
        {
            using var response = await http.CreateClient(Name).SendAsync(request, cancellationToken);
            return await ReadAsync(response, cancellationToken);
        }
        catch (HttpRequestException ex)
        {
            throw new BrokerException($"{DisplayName} did not answer. Check your connection, then try again.", 502, ex);
        }
    }

    private static bool SessionExpired(string? message) =>
        message is not null && new[] { "session expired", "invalid session", "session key", "not logged in", "invalid input : uid" }.Any(m => message.Contains(m, StringComparison.OrdinalIgnoreCase));

    protected JsonObject Account(BrokerSession session, bool holdings = false)
    {
        var id = UserId(session);
        var body = new JsonObject { ["uid"] = id, ["actid"] = id };
        if (holdings) body["prd"] = "C";
        return body;
    }

    // ---- orders ----------------------------------------------------------------------------------------------

    protected static string DataExchangeOf(string exchange) => DataExchange(exchange);

    protected static string EncodeJData(JsonObject payload) => JData(payload);


    protected IHttpClientFactory Http => http;

    protected ISymbolMaster Symbols => symbols;

    protected BrokerSessionFactory Sessions => sessions;

    protected static string MapProduct(string product) => Product(product);

    protected static string MapType(string priceType) => OrderType(priceType);

    protected static bool SessionLapsed(string? message) => SessionExpired(message);

    protected Task<JsonNode?> PostJsonAsync(BrokerSession session, string endpoint, JsonObject payload, CancellationToken cancellationToken) => PostAsync(session, endpoint, payload, cancellationToken);

    protected Task<JsonObject> QuoteForMarginAsync(BrokerSession session, SymbolRecord instrument, CancellationToken cancellationToken) => QuoteAsync(session, instrument.Exchange, instrument.Token, cancellationToken);

    protected string UserIdOf(BrokerSession session) => UserId(session);

    private static string Product(string product) => product switch { "CNC" => "C", "NRML" => "M", "MIS" => "I", _ => "I" };

    private static string OrderType(string priceType) => priceType switch { "MARKET" => "MKT", "LIMIT" => "LMT", "SL" => "SL-LMT", "SL-M" => "SL-MKT", _ => "MARKET" };

    private static string EscapeSymbol(string symbol) => symbol.Replace("&", "%26", StringComparison.Ordinal);

    public async Task<BrokerCallResult> PlaceAsync(BrokerSession session, BrokerOrder order, CancellationToken cancellationToken)
    {
        var instrument = await symbols.FindAsync(Name, order.Symbol, order.Exchange, cancellationToken);
        if (instrument is null) return BrokerCallResult.Refused(400, $"{order.Symbol} on {order.Exchange} is not in the {DisplayName} master contract. Check the symbol, or download the master contract again.");

        var price = (double)order.Price;
        var type = OrderType(order.PriceType);
        var action = order.Action.ToUpperInvariant();
        var instrumentType = MarketPriceProtection.InstrumentType(null, order.Symbol);
        if (order.PriceType == "MARKET" || (ProtectsStopMarket && order.PriceType == "SL-M"))
        {
            var converted = false;
            try
            {
                var quote = await QuoteAsync(session, instrument.Exchange, instrument.Token, cancellationToken);
                var ltp = Float(quote["lp"]);
                var tick = Truthy(quote["ti"]) ? Float(quote["ti"]) : (double?)null;
                if (ltp > 0)
                {
                    price = MarketPriceProtection.ProtectedPrice(ltp, action, instrumentType, tick);
                    type = order.PriceType == "MARKET" ? "LMT" : "SL-LMT";
                    converted = true;
                }
            }
            catch (BrokerException ex)
            {
                logger.LogWarning(ex, "{Broker} market price protection could not read a quote; the order goes without it.", DisplayName);
            }

            // A stop-market that was not converted would go out as the order type Shoonya refuses, so it becomes a stop-limit priced off its trigger.
            if (ProtectsStopMarket && order.PriceType == "SL-M" && !converted)
            {
                type = "SL-LMT";
                var trigger = (double)order.TriggerPrice;
                if (trigger > 0)
                {
                    price = instrument.TickSize is { } tick and > 0 ? MarketPriceProtection.ProtectedPrice(trigger, action, instrumentType, tick) : trigger;
                }
            }
        }

        var id = UserId(session);
        var body = new JsonObject
        {
            ["uid"] = id, ["actid"] = id, ["exch"] = order.Exchange, ["tsym"] = EscapeSymbol(instrument.BrokerSymbol),
            ["qty"] = order.Quantity.ToString(CultureInfo.InvariantCulture), ["prc"] = PyFloat(price), ["trgprc"] = PyFloat((double)order.TriggerPrice),
            ["dscqty"] = order.DisclosedQuantity.ToString(CultureInfo.InvariantCulture), ["prd"] = Product(order.Product),
            ["trantype"] = action == "BUY" ? "B" : "S", ["prctyp"] = type, ["mkt_protection"] = "0", ["ret"] = "DAY", ["ordersource"] = "API",
        };

        JsonNode? reply;
        try
        {
            reply = await PostAsync(session, "/PlaceOrder", body, cancellationToken);
        }
        catch (BrokerException ex) when (ex.HttpStatus == 502)
        {
            return new BrokerCallResult(false, 502, $"The order was sent to {DisplayName} but no reply came back, so it may have been placed. Check the order book on {DisplayName} before trying again.", MayHaveBeenSent: true);
        }

        var answer = reply as JsonObject;
        if (Str(answer?["stat"]) == "Ok" && Str(answer?["norenordno"]) is { Length: > 0 } orderId) return BrokerCallResult.Success(orderId);
        return BrokerCallResult.Refused(500, Reason(answer, "Failed to place order"));
    }

    public async Task<BrokerCallResult> ModifyAsync(BrokerSession session, BrokerOrderChange change, CancellationToken cancellationToken)
    {
        var instrument = await symbols.FindAsync(Name, change.Symbol, change.Exchange, cancellationToken);
        var symbol = instrument?.BrokerSymbol ?? change.Symbol;
        var body = new JsonObject
        {
            ["uid"] = UserId(session), ["exch"] = change.Exchange, ["norenordno"] = change.OrderId, ["prctyp"] = OrderType(change.PriceType),
            ["prc"] = PyFloat((double)change.Price), ["qty"] = change.Quantity.ToString(CultureInfo.InvariantCulture), ["tsym"] = EscapeSymbol(symbol), ["ret"] = "DAY",
            ["dscqty"] = change.DisclosedQuantity.ToString(CultureInfo.InvariantCulture),
        };
        // A trigger price on a limit order makes Shoonya answer "Trigger price invalid".
        if (change.PriceType is "SL" or "SL-M") body["trgprc"] = PyFloat((double)change.TriggerPrice);
        var answer = await PostAsync(session, "/ModifyOrder", body, cancellationToken) as JsonObject;
        if (Str(answer?["stat"]) == "Ok") return BrokerCallResult.Success(change.OrderId);
        return BrokerCallResult.Refused(500, Reason(answer, "Failed to modify order"));
    }

    public async Task<BrokerCallResult> CancelAsync(BrokerSession session, string orderId, CancellationToken cancellationToken)
    {
        var answer = await PostAsync(session, "/CancelOrder", new JsonObject { ["uid"] = UserId(session), ["norenordno"] = orderId }, cancellationToken) as JsonObject;
        if (Str(answer?["stat"]) == "Ok") return BrokerCallResult.Success(orderId);
        return BrokerCallResult.Refused(500, Reason(answer, "Failed to cancel order"));
    }

    private static string Reason(JsonObject? reply, string fallback) => Str(reply?["emsg"]) is { Length: > 0 } m ? m : fallback;

    // ---- books -----------------------------------------------------------------------------------------------

    /// <summary>A list answer is the book; the "no data" refusal Shoonya gives an empty book is an empty list; a lapsed session is an error.</summary>
    protected async Task<JsonArray> BookAsync(BrokerSession session, string endpoint, JsonObject payload, CancellationToken cancellationToken)
    {
        var reply = await PostAsync(session, endpoint, payload, cancellationToken, book: true);
        if (reply is JsonArray rows) return rows;
        var message = Str((reply as JsonObject)?["emsg"]);
        if (SessionExpired(message)) throw new BrokerException($"{DisplayName} says the session has expired. Log in to {DisplayName} again.", 403);
        return [];
    }

    protected static string NormalizeStatus(string? raw)
    {
        var status = (raw ?? "").Trim().ToUpperInvariant().Replace('_', ' ');
        return status switch
        {
            "COMPLETE" => "complete",
            "OPEN" or "PENDING" or "TRIGGER PENDING" or "NEW" or "REPLACED" or "OPEN PENDING" or "MODIFY PENDING" or "CANCEL PENDING" or "AFTER MARKET ORDER REQ RECEIVED" => "open",
            "REJECTED" or "REJECT" => "rejected",
            "CANCELED" or "CANCELLED" => "cancelled",
            _ => status.ToLowerInvariant(),
        };
    }

    protected static readonly HashSet<string> Derivatives = ["NFO", "MCX", "BFO", "CDS"];

    protected static string BookProduct(string exchange, string product) =>
        (exchange is "NSE" or "BSE") && product == "C" ? "CNC" : product == "I" ? "MIS" : Derivatives.Contains(exchange) && product == "M" ? "NRML" : product;

    protected async Task<string?> SymbolByTokenAsync(string token, string exchange, CancellationToken cancellationToken) =>
        token.Length == 0 ? null : (await symbols.FindByTokenAsync(Name, token, exchange, cancellationToken))?.Symbol;

    protected async Task<string?> SymbolByBrokerSymbolAsync(string brokerSymbol, string exchange, CancellationToken cancellationToken) =>
        (await symbols.FindByBrokerSymbolAsync(Name, brokerSymbol, exchange, cancellationToken))?.Symbol;

    public virtual async Task<BrokerBook> GetOrderBookAsync(BrokerSession session, CancellationToken cancellationToken)
    {
        int buy = 0, sell = 0, completed = 0, open = 0, rejected = 0;
        var orders = new JsonArray();
        foreach (var o in (await BookAsync(session, "/OrderBook", Account(session), cancellationToken)).OfType<JsonObject>())
        {
            var exchange = Str(o["exch"]) ?? "";
            var symbol = await SymbolByTokenAsync(Str(o["token"]) ?? "", exchange, cancellationToken) ?? Str(o["tsym"]) ?? "";
            var side = Str(o["trantype"]);
            if (side == "B") buy++;
            else if (side == "S") sell++;
            var status = NormalizeStatus(Str(o["status"]));
            if (status == "complete") completed++;
            else if (status == "open") open++;
            else if (status == "rejected") rejected++;
            var priceType = (Str(o["prctyp"]) ?? "").ToUpperInvariant() switch
            {
                "MKT" => "MARKET", "LMT" => "LIMIT", "SL-MKT" or "SLMKT" => "SL-M", "SL-LMT" or "SLLMT" => "SL", _ => Str(o["prctyp"]) ?? "",
            };
            orders.Add(new JsonObject
            {
                ["symbol"] = symbol, ["exchange"] = exchange, ["action"] = side == "B" ? "BUY" : side == "S" ? "SELL" : Str(o["trantype"]) ?? "",
                ["quantity"] = Get(o, "qty", 0), ["price"] = Get(o, "prc", 0.0), ["trigger_price"] = Get(o, "trgprc", 0.0), ["pricetype"] = priceType,
                ["product"] = BookProduct(exchange, Str(o["prd"]) ?? ""), ["orderid"] = Get(o, "norenordno", ""), ["order_status"] = status, ["timestamp"] = Get(o, "norentm", ""),
            });
        }

        return new BrokerBook(orders, new JsonObject
        {
            ["total_buy_orders"] = buy, ["total_sell_orders"] = sell, ["total_completed_orders"] = completed, ["total_open_orders"] = open, ["total_rejected_orders"] = rejected,
        });
    }

    public virtual async Task<JsonArray> GetTradeBookAsync(BrokerSession session, CancellationToken cancellationToken)
    {
        var trades = new JsonArray();
        foreach (var t in (await BookAsync(session, "/TradeBook", Account(session), cancellationToken)).OfType<JsonObject>())
        {
            var exchange = Str(t["exch"]) ?? "";
            var known = await SymbolByBrokerSymbolAsync(Str(t["tsym"]) ?? "", exchange, cancellationToken);
            var side = Str(t["trantype"]) ?? "";
            var time = Str(t["norentm"]) ?? "";
            trades.Add(new JsonObject
            {
                ["symbol"] = known ?? Str(t["tsym"]) ?? "", ["exchange"] = exchange, ["product"] = known is null ? Get(t, "prd", "") : BookProduct(exchange, Str(t["prd"]) ?? ""),
                ["action"] = known is not null ? (side == "B" ? "BUY" : side == "S" ? "SELL" : side) : side, ["quantity"] = Get(t, "qty", 0), ["average_price"] = Get(t, "avgprc", 0.0),
                ["trade_value"] = Float(t["avgprc"]) * Int(t["qty"]), ["orderid"] = Get(t, "norenordno", ""), ["timestamp"] = time.Contains(' ') ? time.Split(' ')[0] : time,
            });
        }

        return trades;
    }

    public virtual async Task<JsonArray> GetPositionsAsync(BrokerSession session, CancellationToken cancellationToken)
    {
        var positions = new JsonArray();
        foreach (var p in (await BookAsync(session, "/PositionBook", Account(session), cancellationToken)).OfType<JsonObject>())
        {
            var exchange = Str(p["exch"]) ?? "";
            var known = await SymbolByBrokerSymbolAsync(Str(p["tsym"]) ?? "", exchange, cancellationToken);
            var product = Str(p["prd"]) ?? "";
            if (known is not null) product = BookProduct(exchange, product);
            var netQty = Float(p["netqty"]);
            var avg = Float(p["netavgprc"]);
            var lp = Float(p["lp"]);
            var rpnl = Float(p["rpnl"]);
            var urmtom = Float(p["urmtom"]);
            if (avg == 0 && netQty == 0)
            {
                avg = Float(p["daybuyavgprc"]);
                if (avg == 0) avg = Float(p["totbuyavgprc"]);
            }

            double pnl;
            if (netQty != 0 && lp > 0) pnl = urmtom != 0 ? urmtom + rpnl : netQty > 0 ? (lp - avg) * netQty + rpnl : (avg - lp) * Math.Abs(netQty) + rpnl;
            else pnl = rpnl;
            positions.Add(new JsonObject
            {
                ["symbol"] = known ?? Str(p["tsym"]) ?? "", ["exchange"] = exchange, ["product"] = product, ["quantity"] = netQty, ["average_price"] = avg, ["ltp"] = lp, ["pnl"] = pnl,
            });
        }

        return positions;
    }

    public virtual async Task<BrokerBook> GetHoldingsAsync(BrokerSession session, CancellationToken cancellationToken)
    {
        var holdings = new JsonArray();
        double value = 0;
        foreach (var h in (await BookAsync(session, "/Holdings", Account(session, holdings: true), cancellationToken)).OfType<JsonObject>())
        {
            if (Str(h["stat"]) != "Ok") continue;
            var entries = (h["exch_tsym"] as JsonArray ?? []).OfType<JsonObject>().Where(e => Str(e["exch"]) == "NSE").ToList();
            if (entries.Count == 0) continue;
            var total = Float(h["btstqty"]) + Float(h["holdqty"]) + Float(h["brkcolqty"]) + Float(h["unplgdqty"]) + Float(h["benqty"]) + Math.Max(Float(h["npoadqty"]), Float(h["dpqty"])) - Float(h["usedqty"]);
            value += total * Float(h["upldprc"]);
            foreach (var entry in entries)
            {
                var brokerSymbol = Str(entry["tsym"]) ?? "";
                holdings.Add(new JsonObject
                {
                    ["symbol"] = await SymbolByBrokerSymbolAsync(brokerSymbol, "NSE", cancellationToken) ?? brokerSymbol, ["exchange"] = Str(entry["exch"]),
                    ["quantity"] = (long)Math.Truncate(total), ["product"] = Get(h, "s_prdt_ali", "CNC"), ["pnl"] = 0.0, ["pnlpercent"] = 0.0,
                });
            }
        }

        value = BookFormatting.Round2(value);
        return new BrokerBook(holdings, new JsonObject { ["totalholdingvalue"] = value, ["totalinvvalue"] = value, ["totalprofitandloss"] = 0, ["totalpnlpercentage"] = 0 });
    }

    // ---- funds -----------------------------------------------------------------------------------------------

    public virtual async Task<JsonObject> GetFundsAsync(BrokerSession session, CancellationToken cancellationToken)
    {
        var limits = await PostAsync(session, "/Limits", Account(session), cancellationToken) as JsonObject;
        if (Str(limits?["stat"]) != "Ok")
        {
            // OpenAlgo shows an empty balance when Shoonya refuses; a lapsed session is reported so nothing is mistaken for a balance.
            if (SessionExpired(Str(limits?["emsg"]))) throw new BrokerException($"{DisplayName} says the session has expired. Log in to {DisplayName} again.", 403);
            throw new BrokerException($"{DisplayName} could not report funds: {Reason(limits, "try again in a moment.")}");
        }

        var used = Float(limits!["marginused"]);
        return new JsonObject
        {
            ["availablecash"] = BookFormatting.Fixed2(Float(limits["cash"]) + Float(limits["payin"]) - used), ["collateral"] = BookFormatting.Fixed2(Float(limits["brkcollamt"])),
            ["m2munrealized"] = BookFormatting.Fixed2(Float(limits["unmtom"])), ["m2mrealized"] = BookFormatting.Fixed2(-Float(limits["rpnl"])),
            ["utiliseddebits"] = BookFormatting.Fixed2(used),
        };
    }

    // ---- market data -----------------------------------------------------------------------------------------

    private static string DataExchange(string exchange) => exchange switch { "NSE_INDEX" => "NSE", "BSE_INDEX" => "BSE", _ => exchange };

    /// <summary>GetQuotes, checking the reply is for the instrument asked about (Shoonya sometimes answers with another instrument's quote) and asking again when it is not.</summary>
    private async Task<JsonObject> QuoteAsync(BrokerSession session, string exchange, string token, CancellationToken cancellationToken)
    {
        JsonObject? reply = null;
        for (var attempt = 1; attempt <= QuoteAttempts; attempt++)
        {
            reply = await PostAsync(session, "/GetQuotes", QuoteBody(session, DataExchange(exchange), token), cancellationToken) as JsonObject
                ?? throw new BrokerException($"{DisplayName} sent a reply EP Trade bot could not read. Try again in a moment.", 502);
            if (Str(reply["stat"]) != "Ok")
            {
                if (SessionExpired(Str(reply["emsg"]))) throw new BrokerException($"{DisplayName} says the session has expired. Log in to {DisplayName} again.", 403);
                throw new BrokerException($"{DisplayName} could not send the quote: {Str(reply["emsg"]) ?? "no reason was given."}");
            }

            if (!GuardsQuotes) return reply;
            var gotToken = Str(reply["token"]) ?? "";
            var gotExchange = Str(reply["exch"]) ?? "";
            if (gotToken.Length == 0 || (gotToken == token && (gotExchange.Length == 0 || gotExchange == DataExchange(exchange)))) return reply;
        }

        throw new BrokerException($"{DisplayName} sent the quote of another instrument ({Str(reply?["exch"])}|{Str(reply?["token"])}) on all {QuoteAttempts} tries. Try again in a moment.");
    }

    private async Task<(SymbolRecord Record, JsonObject Quote)> FindAndQuoteAsync(BrokerSession session, string symbol, string exchange, CancellationToken cancellationToken)
    {
        var record = await symbols.FindAsync(Name, symbol, exchange, cancellationToken)
            ?? throw new BrokerException($"{symbol} on {exchange} is not in the {DisplayName} master contract. Check the symbol, or download the master contract again.", 400);
        return (record, await QuoteAsync(session, record.Exchange, record.Token, cancellationToken));
    }

    /// <summary>The body of a quote call: the exchange, the token and the user (Flattrade adds the account too).</summary>
    protected virtual JsonObject QuoteBody(BrokerSession session, string exchange, string token) => new() { ["exch"] = exchange, ["token"] = token, ["uid"] = UserId(session) };

    protected virtual JsonObject QuoteData(JsonObject q) => new()
    {
        ["bid"] = Float(q["bp1"]), ["ask"] = Float(q["sp1"]), ["open"] = Float(q["o"]), ["high"] = Float(q["h"]), ["low"] = Float(q["l"]), ["ltp"] = Float(q["lp"]),
        ["prev_close"] = Float(q["c"]), ["volume"] = Int(q["v"]), ["oi"] = Int(q["oi"]),
    };

    public async Task<JsonObject> GetQuoteAsync(BrokerSession session, string symbol, string exchange, CancellationToken cancellationToken)
    {
        var q = (await FindAndQuoteAsync(session, symbol, exchange, cancellationToken)).Quote;
        var data = QuoteData(q);
        data["tick_size"] = Truthy(q["ti"]) ? Float(q["ti"]) : null;
        return data;
    }

    public virtual async Task<JsonObject> GetDepthAsync(BrokerSession session, string symbol, string exchange, CancellationToken cancellationToken)
    {
        var q = (await FindAndQuoteAsync(session, symbol, exchange, cancellationToken)).Quote;
        var bids = new JsonArray();
        var asks = new JsonArray();
        long totalBuy = 0, totalSell = 0;
        for (var i = 1; i <= 5; i++)
        {
            bids.Add(new JsonObject { ["price"] = Float(q[$"bp{i}"]), ["quantity"] = Int(q[$"bq{i}"]) });
            asks.Add(new JsonObject { ["price"] = Float(q[$"sp{i}"]), ["quantity"] = Int(q[$"sq{i}"]) });
            totalBuy += Int(q[$"bq{i}"]);
            totalSell += Int(q[$"sq{i}"]);
        }

        return new JsonObject
        {
            ["bids"] = bids, ["asks"] = asks, ["totalbuyqty"] = totalBuy, ["totalsellqty"] = totalSell, ["high"] = Float(q["h"]), ["low"] = Float(q["l"]), ["ltp"] = Float(q["lp"]),
            ["ltq"] = Int(q["ltq"]), ["open"] = Float(q["o"]), ["prev_close"] = Float(q["c"]), ["volume"] = Int(q["v"]), ["oi"] = 0,
        };
    }

    public async Task<JsonArray> GetMultiQuotesAsync(BrokerSession session, IReadOnlyList<SymbolRecord> instruments, CancellationToken cancellationToken)
    {
        var results = new JsonArray();
        foreach (var r in instruments)
        {
            try
            {
                var q = await QuoteAsync(session, r.Exchange, r.Token, cancellationToken);
                results.Add(new JsonObject { ["symbol"] = r.Symbol, ["exchange"] = r.Exchange, ["data"] = QuoteData(q) });
            }
            catch (BrokerException ex)
            {
                results.Add(new JsonObject { ["symbol"] = r.Symbol, ["exchange"] = r.Exchange, ["error"] = ex.Message });
            }
        }

        return results;
    }
}

/// <summary>Shoonya (broker/shoonya), api.shoonya.com.</summary>
[BrokerPlugin]
public sealed class ShoonyaPlugin(IHttpClientFactory http, BrokerSessionFactory sessions, ISymbolMaster symbols, TimeProvider clock, ILogger<ShoonyaPlugin> logger)
    : NorenPluginBase(http, sessions, symbols, logger), IBrokerMargin, IBrokerHistory
{
    // ---- history ---------------------------------------------------------------------------------------------

    private static readonly Dictionary<string, string> Resolutions = new()
    {
        ["1m"] = "1", ["3m"] = "3", ["5m"] = "5", ["10m"] = "10", ["15m"] = "15", ["30m"] = "30", ["1h"] = "60", ["2h"] = "120", ["4h"] = "240", ["D"] = "D",
    };

    private static readonly Dictionary<string, int> WindowDays = new()
    {
        ["1m"] = 5, ["3m"] = 10, ["5m"] = 20, ["10m"] = 40, ["15m"] = 60, ["30m"] = 90, ["1h"] = 180, ["2h"] = 180, ["4h"] = 365, ["D"] = 730,
    };

    /// <summary>The name Shoonya's daily chart knows an index by.</summary>
    private static readonly Dictionary<(string, string), string> IndexNames = new()
    {
        [("NSE_INDEX", "NIFTY")] = "Nifty 50", [("NSE_INDEX", "BANKNIFTY")] = "Nifty Bank", [("NSE_INDEX", "FINNIFTY")] = "Nifty Financial Services",
        [("NSE_INDEX", "MIDCPNIFTY")] = "Nifty Midcap Select", [("NSE_INDEX", "NIFTYNXT50")] = "Nifty Next 50", [("NSE_INDEX", "INDIAVIX")] = "India VIX",
    };

    private static readonly TimeSpan IstOffset = TimeSpan.FromHours(5.5);

    public IReadOnlyList<string> HistoryTimeframes { get; } = [.. Resolutions.Keys];

    private static long IstEpoch(DateOnly day, TimeOnly time) => new DateTimeOffset(day.ToDateTime(time), IstOffset).ToUnixTimeSeconds();

    /// <summary>A chart call: the JSON and the token ride in a form body, as Shoonya's chart endpoints take them.</summary>
    private async Task<JsonNode?> ChartAsync(BrokerSession session, string endpoint, JsonObject payload, CancellationToken cancellationToken)
    {
        payload["uid"] = UserIdOf(session);
        using var request = new HttpRequestMessage(HttpMethod.Post, Sessions.BaseUrl(Name, Host) + "/NorenWClientAPI" + endpoint)
        {
            Content = new StringContent("jData=" + EncodeJData(payload) + "&jKey=" + session.AuthToken, System.Text.Encoding.UTF8, "application/x-www-form-urlencoded"),
        };
        using var response = await Http.CreateClient(Name).SendAsync(request, cancellationToken);
        return await JsonRead.ReadAsync(response, cancellationToken);
    }

    public async Task<JsonArray> GetHistoryAsync(BrokerSession session, SymbolRecord instrument, string interval, DateOnly start, DateOnly end, CancellationToken cancellationToken)
    {
        if (!Resolutions.TryGetValue(interval, out var resolution))
            throw new BrokerException($"{DisplayName} does not offer {interval} candles. Choose one of: {string.Join(", ", Resolutions.Keys)}.", 400);

        var exchange = DataExchangeOf(instrument.Exchange);
        var startTs = IstEpoch(start, new TimeOnly(0, 0, 0));
        var endTs = IstEpoch(end, new TimeOnly(23, 59, 59));
        var daily = interval == "D";
        var endpoint = daily ? "/EODChartData" : "/TPSeries";
        var chartName = IndexNames.TryGetValue((instrument.Exchange, instrument.Symbol), out var index) ? index : instrument.BrokerSymbol;
        long window = WindowDays[interval] * 86400L;

        var raw = new List<JsonNode?>();
        int attempted = 0, failed = 0;
        for (var chunkStart = startTs; chunkStart <= endTs;)
        {
            var chunkEnd = Math.Min(chunkStart + window, endTs);
            attempted++;
            var payload = daily
                ? new JsonObject { ["sym"] = $"{exchange}:{chartName}", ["from"] = chunkStart.ToString(CultureInfo.InvariantCulture), ["to"] = chunkEnd.ToString(CultureInfo.InvariantCulture) }
                : new JsonObject
                {
                    ["exch"] = exchange, ["token"] = instrument.Token, ["st"] = chunkStart.ToString(CultureInfo.InvariantCulture),
                    ["et"] = chunkEnd.ToString(CultureInfo.InvariantCulture), ["intrv"] = resolution,
                };
            JsonNode? answer = null;
            var sent = true;
            try
            {
                answer = await ChartAsync(session, endpoint, payload, cancellationToken);
            }
            catch (HttpRequestException)
            {
                sent = false;
            }

            chunkStart = chunkEnd + 1;
            if (!sent)
            {
                failed++;
                continue;
            }

            if (answer is JsonObject error)
            {
                var message = Str(error["emsg"]) ?? Str(error["message"]) ?? "unknown";
                if (SessionLapsed(message)) throw new BrokerException($"{DisplayName} says the session has expired. Log in to {DisplayName} again.", 403);
                if (message.Contains("no data", StringComparison.OrdinalIgnoreCase)) continue;
                failed++;
                continue;
            }

            if (answer is not JsonArray list)
            {
                failed++;
                continue;
            }

            raw.AddRange(list);
        }

        if (attempted > 0 && failed == attempted)
            throw new BrokerException($"{DisplayName} did not send the candles for {instrument.Symbol} between {start:yyyy-MM-dd} and {end:yyyy-MM-dd}. Try again in a moment.");

        var candles = new SortedDictionary<long, double[]>();
        foreach (var node in raw)
        {
            var candle = node is JsonValue v && v.TryGetValue<string>(out var text) ? TryParseObject(text) : node as JsonObject;
            if (candle is null) continue;
            if (!TryFloat(candle["into"], out var open) || !TryFloat(candle["inth"], out var high) || !TryFloat(candle["intl"], out var low) || !TryFloat(candle["intc"], out var close)) continue;
            if (open == 0 && high == 0 && low == 0 && close == 0) continue;
            long? stamp = null;
            if (candle["ssboe"] is not null && Str(candle["ssboe"]) is { } ssboe && long.TryParse(ssboe, NumberStyles.Integer, CultureInfo.InvariantCulture, out var seconds)) stamp = seconds;
            else if (candle["ssboe"] is null && Str(candle["time"]) is { } time) stamp = ParseCandleTime(time);
            if (stamp is null) continue;
            if (!TryFloat(candle.ContainsKey("intv") ? candle["intv"] : 0, out var volume) || !TryFloat(candle.ContainsKey("oi") ? candle["oi"] : 0, out var oi)) continue;
            candles[stamp.Value] = [open, high, low, close, volume, oi];
        }

        if (daily)
        {
            var today = DateOnly.FromDateTime(clock.GetUtcNow().ToOffset(IstOffset).DateTime);
            var todayTs = IstEpoch(today, new TimeOnly(0, 0, 0)) + 19800;
            if (todayTs >= startTs && todayTs <= endTs && (candles.Count == 0 || candles.Keys.Max() < todayTs))
            {
                try
                {
                    var quote = await QuoteForMarginAsync(session, instrument, cancellationToken);
                    candles[todayTs] = [Float(quote["o"]), Float(quote["h"]), Float(quote["l"]), Float(quote["lp"]), Float(quote["v"]), Float(quote["oi"])];
                }
                catch (BrokerException)
                {
                }
            }
        }

        var result = new JsonArray();
        foreach (var (stamp, c) in candles)
        {
            // A bar whose range leaves out its own open or close is widened to cover them; a volume below zero cannot be true and is zeroed.
            result.Add(new JsonObject
            {
                ["timestamp"] = stamp, ["open"] = c[0], ["high"] = Math.Max(Math.Max(c[0], c[1]), Math.Max(c[2], c[3])), ["low"] = Math.Min(Math.Min(c[0], c[1]), Math.Min(c[2], c[3])),
                ["close"] = c[3], ["volume"] = c[4] < 0 ? 0 : c[4], ["oi"] = c[5],
            });
        }

        return result;
    }

    private static JsonObject? TryParseObject(string text)
    {
        try
        {
            return JsonNode.Parse(text) as JsonObject;
        }
        catch (System.Text.Json.JsonException)
        {
            return null;
        }
    }

    private static bool TryFloat(JsonNode? node, out double value)
    {
        value = 0;
        if (node is null) return false;
        return BookFormatting.Number(node) is { } n ? (value = n) == n : double.TryParse(Str(node), NumberStyles.Float, CultureInfo.InvariantCulture, out value);
    }

    /// <summary>A bar with no epoch second carries its time as text: "02-11-2026 09:15:00" (IST) or "02-NOV-2026" (a day, read as midnight UTC).</summary>
    private static long? ParseCandleTime(string text)
    {
        if (DateTime.TryParseExact(text, "dd-MM-yyyy HH:mm:ss", CultureInfo.InvariantCulture, DateTimeStyles.None, out var local))
            return new DateTimeOffset(local, IstOffset).ToUnixTimeSeconds();
        if (DateTime.TryParseExact(text, "dd-MMM-yyyy", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var day))
            return new DateTimeOffset(day, TimeSpan.Zero).ToUnixTimeSeconds();
        return null;
    }

    public override BrokerCapability Capabilities => base.Capabilities | BrokerCapability.Margin | BrokerCapability.History;

    /// <summary>
    /// The margin of a basket (GetBasketMargin). Shoonya refuses market and stop-market types here as it does on orders, so they are converted
    /// the same way; with no usable quote the converted order carries the price given (a market order) or the trigger (a stop-market).
    /// </summary>
    public async Task<JsonObject> CalculateMarginAsync(BrokerSession session, IReadOnlyList<MarginLeg> legs, CancellationToken cancellationToken)
    {
        var orders = new List<JsonObject>();
        foreach (var l in legs)
        {
            var (type, price) = await MarginPriceAsync(session, l, cancellationToken);
            orders.Add(new JsonObject
            {
                ["exch"] = l.Instrument.Exchange, ["tsym"] = l.Instrument.BrokerSymbol.Replace("&", "%26", StringComparison.Ordinal),
                ["qty"] = l.Quantity.ToString(System.Globalization.CultureInfo.InvariantCulture), ["prc"] = price,
                ["trgprc"] = l.TriggerPrice == 0 ? "0" : Plain(l.TriggerPrice), ["prd"] = MapProduct(l.Product), ["trantype"] = l.Action.Equals("BUY", StringComparison.OrdinalIgnoreCase) ? "B" : "S", ["prctyp"] = type,
            });
        }

        var id = UserIdOf(session);
        var body = new JsonObject { ["uid"] = id, ["actid"] = id };
        foreach (var (key, value) in orders[0]) body[key] = value?.DeepClone();
        body["basketlists"] = new JsonArray(orders.Skip(1).Select(o => (JsonNode?)o).ToArray());

        var reply = await PostJsonAsync(session, "/GetBasketMargin", body, cancellationToken) as JsonObject
            ?? throw new BrokerException($"{DisplayName} sent a reply EP Trade bot could not read. Try again in a moment.", 502);
        if (JsonRead.Str(reply["stat"]) != "Ok")
        {
            if (SessionLapsed(JsonRead.Str(reply["emsg"]))) throw new BrokerException($"{DisplayName} says the session has expired. Log in to {DisplayName} again.", 403);
            var reason = JsonRead.Str(reply["emsg"]) is { Length: > 0 } e ? e : JsonRead.Str(reply["remarks"]) is { Length: > 0 } r ? r : "no reason was given.";
            throw new BrokerException($"{DisplayName} could not work out the margin: {reason}", 400);
        }

        return new JsonObject { ["total_margin_required"] = JsonRead.Float(reply["marginused"]), ["span_margin"] = 0, ["exposure_margin"] = 0 };
    }

    private async Task<(string Type, string Price)> MarginPriceAsync(BrokerSession session, MarginLeg l, CancellationToken cancellationToken)
    {
        var type = MapType(l.PriceType);
        if (l.PriceType is not ("MARKET" or "SL-M")) return (type, l.Price == 0 ? "0" : Plain(l.Price));
        var converted = l.PriceType == "MARKET" ? "LMT" : "SL-LMT";
        var fallback = l.PriceType == "MARKET" ? (l.Price == 0 ? "0" : Plain(l.Price)) : (l.TriggerPrice == 0 ? "0" : Plain(l.TriggerPrice));
        try
        {
            var quote = await QuoteForMarginAsync(session, l.Instrument, cancellationToken);
            var ltp = JsonRead.Float(quote["lp"]);
            if (ltp > 0)
            {
                var tick = JsonRead.Truthy(quote["ti"]) ? JsonRead.Float(quote["ti"]) : (double?)null;
                return (converted, Plain((decimal)MarketPriceProtection.ProtectedPrice(ltp, l.Action, MarketPriceProtection.InstrumentType(null, l.Instrument.Symbol), tick)));
            }
        }
        catch (BrokerException)
        {
        }

        return (converted, fallback);
    }

    /// <summary>Python's str() of a price: the figure with no trailing zeros ("805.5", "24010.0" for a whole number sent as a float).</summary>
    private static string Plain(decimal value) => value.ToString("0.0###############", System.Globalization.CultureInfo.InvariantCulture);

    public override string Name => "shoonya";
    public override string DisplayName => "Shoonya";
    protected override string Host => "https://api.shoonya.com";
}

/// <summary>
/// Zebu (broker/zebu), go.mynt.in: the same Noren API as Shoonya. A stop-market order is sent as it is, a quote is checked as Shoonya's is (OpenAlgo's Zebu plugin takes whatever answer comes, even another instrument's), and its holdings
/// carry the product, profit and loss on each row, with the held quantity counted as held shares plus the larger of the T1 and demat quantities.
/// </summary>
[BrokerPlugin]
public sealed class ZebuPlugin(IHttpClientFactory http, BrokerSessionFactory sessions, ISymbolMaster symbols, TimeProvider clock, ILogger<ZebuPlugin> logger)
    : NorenPluginBase(http, sessions, symbols, logger), IBrokerHistory
{
    public override BrokerCapability Capabilities => base.Capabilities | BrokerCapability.History;

    private static readonly Dictionary<string, string> Resolutions = new()
    {
        ["1m"] = "1", ["3m"] = "3", ["5m"] = "5", ["10m"] = "10", ["15m"] = "15", ["30m"] = "30", ["1h"] = "60", ["2h"] = "120", ["4h"] = "240", ["D"] = "D",
    };

    private static readonly TimeSpan IstOffset = TimeSpan.FromHours(5.5);

    public IReadOnlyList<string> HistoryTimeframes { get; } = [.. Resolutions.Keys];

    private static long IstEpoch(DateOnly day, TimeOnly time) => new DateTimeOffset(day.ToDateTime(time), IstOffset).ToUnixTimeSeconds();

    /// <summary>
    /// One call for the whole range (Zebu is asked once, not in windows). Intraday bars carry their time as text (IST); daily bars carry an epoch second. Unlike
    /// OpenAlgo's plugin, bars are sorted with one per timestamp and a bar whose range leaves out its open or close is widened and a volume below zero zeroed,
    /// because a chart client rejects the whole series over one such bar.
    /// </summary>
    public async Task<JsonArray> GetHistoryAsync(BrokerSession session, SymbolRecord instrument, string interval, DateOnly start, DateOnly end, CancellationToken cancellationToken)
    {
        if (!Resolutions.TryGetValue(interval, out var resolution))
            throw new BrokerException($"{DisplayName} does not offer {interval} candles. Choose one of: {string.Join(", ", Resolutions.Keys)}.", 400);

        var exchange = DataExchangeOf(instrument.Exchange);
        var startTs = IstEpoch(start, new TimeOnly(0, 0, 0));
        var endTs = IstEpoch(end, new TimeOnly(23, 59, 59));
        var daily = interval == "D";
        JsonNode? answer = null;
        string? failure = null;
        try
        {
            answer = await PostJsonAsync(session, daily ? "/EODChartData" : "/TPSeries", daily
                ? new JsonObject { ["sym"] = $"{exchange}:{instrument.BrokerSymbol}", ["from"] = startTs.ToString(CultureInfo.InvariantCulture), ["to"] = endTs.ToString(CultureInfo.InvariantCulture), ["uid"] = UserIdOf(session) }
                : new JsonObject { ["exch"] = exchange, ["token"] = instrument.Token, ["st"] = startTs.ToString(CultureInfo.InvariantCulture), ["et"] = endTs.ToString(CultureInfo.InvariantCulture), ["intrv"] = resolution, ["uid"] = UserIdOf(session) },
                cancellationToken);
        }
        catch (BrokerException ex) when (ex.HttpStatus == 502)
        {
            failure = ex.Message;
        }

        if (answer is JsonObject error) failure = $"{DisplayName} did not send the candles: {JsonRead.Str(error["emsg"]) ?? "no reason was given."}";
        else if (answer is not JsonArray && failure is null) failure = $"{DisplayName} did not send the candles. Try again in a moment.";
        // A daily chart that fails still lets today's candle be built from a quote; an intraday one has nothing to fall back on.
        if (failure is not null && !daily) throw new BrokerException(failure);

        var candles = new SortedDictionary<long, double[]>();
        foreach (var node in answer as JsonArray ?? [])
        {
            var candle = node is JsonValue v && v.TryGetValue<string>(out var text) ? TryObject(text) : node as JsonObject;
            if (candle is null) continue;
            if (!Num(candle["into"], out var open) || !Num(candle["inth"], out var high) || !Num(candle["intl"], out var low) || !Num(candle["intc"], out var close)) continue;
            if (!Num(candle.ContainsKey("intv") ? candle["intv"] : 0, out var volume) || !Num(candle.ContainsKey("oi") ? candle["oi"] : 0, out var oi)) continue;
            long stamp;
            if (daily)
            {
                stamp = long.TryParse(JsonRead.Str(candle["ssboe"]), NumberStyles.Integer, CultureInfo.InvariantCulture, out var s) ? s : 0;
            }
            else
            {
                if (open == 0 && high == 0 && low == 0 && close == 0) continue;
                if (!DateTime.TryParseExact(JsonRead.Str(candle["time"]), "dd-MM-yyyy HH:mm:ss", CultureInfo.InvariantCulture, DateTimeStyles.None, out var local)) continue;
                stamp = new DateTimeOffset(local, IstOffset).ToUnixTimeSeconds();
            }

            candles[stamp] = [open, high, low, close, volume, oi];
        }

        if (daily)
        {
            var today = DateOnly.FromDateTime(clock.GetUtcNow().ToOffset(IstOffset).DateTime);
            var todayTs = IstEpoch(today, new TimeOnly(0, 0, 0));
            if (todayTs >= startTs && todayTs <= endTs && (candles.Count == 0 || candles.Keys.Max() < todayTs))
            {
                try
                {
                    if (await PostJsonAsync(session, "/GetQuotes", new JsonObject { ["exch"] = exchange, ["token"] = instrument.Token, ["uid"] = UserIdOf(session) }, cancellationToken) is JsonObject quote && JsonRead.Str(quote["stat"]) == "Ok")
                        candles[todayTs] = [JsonRead.Float(quote["o"]), JsonRead.Float(quote["h"]), JsonRead.Float(quote["l"]), JsonRead.Float(quote["lp"]), JsonRead.Float(quote["v"]), JsonRead.Float(quote["oi"])];
                }
                catch (BrokerException)
                {
                }
            }

            if (candles.Count == 0 && failure is not null) throw new BrokerException(failure);
        }

        var result = new JsonArray();
        foreach (var (stamp, c) in candles)
        {
            result.Add(new JsonObject
            {
                ["timestamp"] = stamp, ["open"] = c[0], ["high"] = Math.Max(Math.Max(c[0], c[1]), Math.Max(c[2], c[3])), ["low"] = Math.Min(Math.Min(c[0], c[1]), Math.Min(c[2], c[3])),
                ["close"] = c[3], ["volume"] = c[4] < 0 ? 0 : c[4], ["oi"] = c[5],
            });
        }

        return result;
    }

    private static JsonObject? TryObject(string text)
    {
        try
        {
            return JsonNode.Parse(text) as JsonObject;
        }
        catch (System.Text.Json.JsonException)
        {
            return null;
        }
    }

    private static bool Num(JsonNode? node, out double value)
    {
        value = 0;
        if (node is null) return false;
        return BookFormatting.Number(node) is { } n ? (value = n) == n : double.TryParse(JsonRead.Str(node), NumberStyles.Float, CultureInfo.InvariantCulture, out value);
    }

    public override string Name => "zebu";
    public override string DisplayName => "Zebu";
    protected override string Host => "https://go.mynt.in";
    protected override bool ProtectsStopMarket => false;

    public override async Task<BrokerBook> GetHoldingsAsync(BrokerSession session, CancellationToken cancellationToken)
    {
        var holdings = new JsonArray();
        double held = 0, invested = 0;
        foreach (var h in (await BookAsync(session, "/Holdings", Account(session, holdings: true), cancellationToken)).OfType<JsonObject>())
        {
            var entries = (h["exch_tsym"] as JsonArray ?? []).OfType<JsonObject>().Where(e => JsonRead.Str(e["exch"]) == "NSE").ToList();
            var quantity = JsonRead.Int(h["holdqty"]) + Math.Max(JsonRead.Int(h["npoadt1qty"]), JsonRead.Int(h["dpqty"]));
            foreach (var entry in entries)
            {
                var brokerSymbol = JsonRead.Str(entry["tsym"]) ?? "";
                holdings.Add(new JsonObject
                {
                    ["symbol"] = await SymbolByBrokerSymbolAsync(brokerSymbol, "NSE", cancellationToken) ?? brokerSymbol, ["exchange"] = JsonRead.Str(entry["exch"]),
                    ["quantity"] = quantity, ["product"] = JsonRead.Get(entry, "product", "CNC"),
                    ["pnl"] = JsonRead.Get(h, "profitandloss", 0.0), ["pnlpercent"] = JsonRead.Get(h, "pnlpercentage", 0.0),
                });
            }

            if (JsonRead.Str(h["stat"]) != "Ok" || entries.Count == 0) continue;
            var price = JsonRead.Float(h["upldprc"]);
            invested += (JsonRead.Float(h["holdqty"]) + Math.Max(JsonRead.Float(h["npoadt1qty"]), JsonRead.Float(h["dpqty"]))) * price;
            held += (JsonRead.Float(h["btstqty"]) + JsonRead.Float(h["holdqty"]) + JsonRead.Float(h["brkcolqty"]) + JsonRead.Float(h["unplgdqty"]) + JsonRead.Float(h["benqty"])
                + Math.Max(JsonRead.Float(h["npoadt1qty"]), JsonRead.Float(h["dpqty"])) - JsonRead.Float(h["usedqty"])) * price;
        }

        return new BrokerBook(holdings, new JsonObject
        {
            ["totalholdingvalue"] = held, ["totalinvvalue"] = invested, ["totalprofitandloss"] = 0, ["totalpnlpercentage"] = 0,
        });
    }
}

/// <summary>
/// Flattrade (broker/flattrade), piconnect.flattrade.in: the Noren API under /PiConnectAPI, with the token in the body (<c>jData=&lt;json&gt;&amp;jKey=&lt;token&gt;</c>)
/// instead of a bearer header. A call the broker turns away for going over its rate limit is asked again (reads only, never an order): after 2, 4 and 8 seconds.
/// Its books carry more than the others: filled and pending quantity and the average price on an order, the realised and unrealised profit on a position.
/// </summary>
[BrokerPlugin]
public sealed class FlattradePlugin(IHttpClientFactory http, BrokerSessionFactory sessions, ISymbolMaster symbols, ILogger<FlattradePlugin> logger)
    : NorenPluginBase(http, sessions, symbols, logger)
{
    public override string Name => "flattrade";
    public override string DisplayName => "Flattrade";
    protected override string Host => "https://piconnect.flattrade.in";
    protected override string Api => "/PiConnectAPI";

    // ---- sign-in -----------------------------------------------------------------------------------------------

    /// <summary>The trader signs in on Flattrade's page (the app key is the part of the saved key after ":::"), which sends back a code.</summary>
    public override string? LoginUrl(IReadOnlyDictionary<string, string> keys, string state) =>
        ClientId(keys) is { } appKey ? $"https://auth.flattrade.in/?app_key={Uri.EscapeDataString(appKey)}" : null;

    /// <summary>The code is exchanged at authapi.flattrade.in for a token with the SHA-256 of the app key, the code and the secret.</summary>
    public override async Task<BrokerLoginResult> LoginAsync(IReadOnlyDictionary<string, string> keys, IReadOnlyDictionary<string, string> input, CancellationToken cancellationToken)
    {
        if (ClientId(keys) is not { } appKey)
            return BrokerLoginResult.Refused($"The {DisplayName} key must be saved as the user id, \":::\" and the app key. Fix it in the server's settings, then sign in again.", 409);
        if (!keys.TryGetValue("ApiSecret", out var secret) || string.IsNullOrWhiteSpace(secret))
            return BrokerLoginResult.Refused($"The {DisplayName} secret is not set on this server. Add it to the server's settings, then sign in again.", 409);
        if (!input.TryGetValue("code", out var code) || string.IsNullOrWhiteSpace(code))
            return BrokerLoginResult.Refused($"{DisplayName} did not send the sign-in back. Start the sign-in again.");

        var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(appKey + code + secret.Trim()))).ToLowerInvariant();
        using var request = new HttpRequestMessage(HttpMethod.Post, Sessions.BaseUrl(Name + "_auth", "https://authapi.flattrade.in") + "/trade/apitoken")
        {
            Content = new StringContent(new JsonObject { ["api_key"] = appKey, ["request_code"] = code, ["api_secret"] = hash }.ToJsonString(), System.Text.Encoding.UTF8, "application/json"),
        };
        try
        {
            using var response = await Http.CreateClient(Name).SendAsync(request, cancellationToken);
            var body = await JsonRead.ReadAsync(response, cancellationToken) as JsonObject;
            if (response.IsSuccessStatusCode && JsonRead.Str(body?["stat"]) == "Ok" && JsonRead.Str(body?["token"]) is { Length: > 0 } token) return BrokerLoginResult.Success(token, null);
            return JsonRead.Str(body?["emsg"]) is { Length: > 0 } reason
                ? BrokerLoginResult.Refused($"{DisplayName} did not accept the sign-in: {reason}")
                : BrokerLoginResult.Refused($"{DisplayName} did not accept the sign-in. Start it again from the beginning.");
        }
        catch (HttpRequestException)
        {
            return BrokerLoginResult.Refused($"{DisplayName} did not answer. Check your connection, then try again.", 502);
        }
    }

    private static bool IsOrderCall(string endpoint) => endpoint is "/PlaceOrder" or "/ModifyOrder" or "/CancelOrder";

    protected override async Task<JsonNode?> PostAsync(BrokerSession session, string endpoint, JsonObject payload, CancellationToken cancellationToken, bool book = false)
    {
        for (var attempt = 0; ; attempt++)
        {
            // The books go as JSON and the holdings, orders and the rest as a form; the broker reads the same body either way.
            var type = book && endpoint != "/Holdings" ? "application/json" : "application/x-www-form-urlencoded";
            using var request = new HttpRequestMessage(HttpMethod.Post, Sessions.BaseUrl(Name, Host) + Api + endpoint)
            {
                Content = new StringContent("jData=" + payload.ToJsonString(new System.Text.Json.JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }) + "&jKey=" + session.AuthToken, System.Text.Encoding.UTF8, type),
            };
            JsonNode? reply;
            try
            {
                using var response = await Http.CreateClient(Name).SendAsync(request, cancellationToken);
                reply = await JsonRead.ReadAsync(response, cancellationToken);
            }
            catch (HttpRequestException ex)
            {
                throw new BrokerException($"{DisplayName} did not answer. Check your connection, then try again.", 502, ex);
            }

            var overLimit = reply is JsonObject o && JsonRead.Str(o["stat"]) == "Not_Ok" && (JsonRead.Str(o["emsg"]) ?? "").Contains("exceeds limit", StringComparison.OrdinalIgnoreCase);
            if (!overLimit || IsOrderCall(endpoint) || attempt >= 3) return reply;
            await Task.Delay(Sessions.RetryBackoff(Name, TimeSpan.FromSeconds(2 * (1 << attempt))), cancellationToken);
        }
    }

    protected override JsonObject QuoteBody(BrokerSession session, string exchange, string token) => new() { ["uid"] = UserIdOf(session), ["exch"] = exchange, ["token"] = token, ["actid"] = UserIdOf(session) };

    protected override JsonObject QuoteData(JsonObject q)
    {
        var data = base.QuoteData(q);
        data["volume"] = (long)Math.Truncate(JsonRead.Float(q["v"]));
        data["tick_size"] = JsonRead.Truthy(q["ti"]) ? JsonRead.Float(q["ti"]) : null;
        return data;
    }

    /// <summary>Five levels a side, each with the number of orders, and the open interest.</summary>
    public override async Task<JsonObject> GetDepthAsync(BrokerSession session, string symbol, string exchange, CancellationToken cancellationToken)
    {
        var record = await Symbols.FindAsync(Name, symbol, exchange, cancellationToken)
            ?? throw new BrokerException($"{symbol} on {exchange} is not in the {DisplayName} master contract. Check the symbol, or download the master contract again.", 400);
        var q = await QuoteForMarginAsync(session, record, cancellationToken);
        var bids = new JsonArray();
        var asks = new JsonArray();
        long totalBuy = 0, totalSell = 0;
        for (var i = 1; i <= 5; i++)
        {
            bids.Add(new JsonObject { ["price"] = JsonRead.Float(q[$"bp{i}"]), ["quantity"] = JsonRead.Int(q[$"bq{i}"]), ["orders"] = JsonRead.Int(q[$"bo{i}"]) });
            asks.Add(new JsonObject { ["price"] = JsonRead.Float(q[$"sp{i}"]), ["quantity"] = JsonRead.Int(q[$"sq{i}"]), ["orders"] = JsonRead.Int(q[$"so{i}"]) });
            totalBuy += JsonRead.Int(q[$"bq{i}"]);
            totalSell += JsonRead.Int(q[$"sq{i}"]);
        }

        return new JsonObject
        {
            ["bids"] = bids, ["asks"] = asks, ["totalbuyqty"] = totalBuy, ["totalsellqty"] = totalSell, ["high"] = JsonRead.Float(q["h"]), ["low"] = JsonRead.Float(q["l"]),
            ["ltp"] = JsonRead.Float(q["lp"]), ["ltq"] = JsonRead.Int(q["ltq"]), ["open"] = JsonRead.Float(q["o"]), ["prev_close"] = JsonRead.Float(q["c"]),
            ["volume"] = (long)Math.Truncate(JsonRead.Float(q["v"])), ["oi"] = JsonRead.Int(q["oi"]),
        };
    }

    // ---- books -----------------------------------------------------------------------------------------------

    public override async Task<BrokerBook> GetOrderBookAsync(BrokerSession session, CancellationToken cancellationToken)
    {
        int buy = 0, sell = 0, completed = 0, open = 0, rejected = 0;
        var orders = new JsonArray();
        foreach (var o in (await BookAsync(session, "/OrderBook", Account(session), cancellationToken)).OfType<JsonObject>())
        {
            var exchange = JsonRead.Str(o["exch"]) ?? "";
            var symbol = await SymbolByTokenAsync(JsonRead.Str(o["token"]) ?? "", exchange, cancellationToken) ?? JsonRead.Str(o["tsym"]) ?? "";
            var side = JsonRead.Str(o["trantype"]);
            if (side == "B") buy++;
            else if (side == "S") sell++;
            var status = NormalizeStatus(JsonRead.Str(o["status"]));
            if (status == "complete") completed++;
            else if (status == "open") open++;
            else if (status == "rejected") rejected++;
            var priceType = (JsonRead.Str(o["prctyp"]) ?? "").ToUpperInvariant() switch
            {
                "MKT" => "MARKET", "LMT" => "LIMIT", "SL-MKT" or "SLMKT" => "SL-M", "SL-LMT" or "SLLMT" => "SL", _ => JsonRead.Str(o["prctyp"]) ?? "",
            };
            // An order that has traded shows its average price; a market order that has not shows the price the broker reports back.
            JsonNode? price = JsonRead.Get(o, "prc", 0.0);
            if (JsonRead.Truthy(o["instname"]) && JsonRead.Truthy(o["avgprc"]))
            {
                if (JsonRead.Float(o["avgprc"]) > 0) price = o["avgprc"]!.DeepClone();
            }
            else if (priceType is "MARKET" or "SL-M" && JsonRead.Float(o["prc"]) == 0.0 && JsonRead.Truthy(o["rprc"]) && JsonRead.Float(o["rprc"]) > 0)
            {
                price = o["rprc"]!.DeepClone();
            }

            var quantity = JsonRead.Int(o["qty"]);
            var filled = JsonRead.Int(o["fillshares"]);
            orders.Add(new JsonObject
            {
                ["symbol"] = symbol, ["exchange"] = exchange, ["action"] = side == "B" ? "BUY" : side == "S" ? "SELL" : JsonRead.Str(o["trantype"]) ?? "",
                ["quantity"] = JsonRead.Get(o, "qty", 0), ["price"] = price, ["trigger_price"] = JsonRead.Get(o, "trgprc", 0.0), ["pricetype"] = priceType,
                ["product"] = BookProduct(exchange, JsonRead.Str(o["prd"]) ?? ""), ["orderid"] = JsonRead.Get(o, "norenordno", ""), ["order_status"] = status,
                ["timestamp"] = JsonRead.Get(o, "norentm", ""), ["filled_quantity"] = filled, ["pending_quantity"] = Math.Max(quantity - filled, 0),
                ["average_price"] = JsonRead.Float(o["avgprc"]),
            });
        }

        return new BrokerBook(orders, new JsonObject
        {
            ["total_buy_orders"] = buy, ["total_sell_orders"] = sell, ["total_completed_orders"] = completed, ["total_open_orders"] = open, ["total_rejected_orders"] = rejected,
        });
    }

    public override async Task<JsonArray> GetTradeBookAsync(BrokerSession session, CancellationToken cancellationToken)
    {
        var trades = new JsonArray();
        foreach (var t in (await BookAsync(session, "/TradeBook", Account(session), cancellationToken)).OfType<JsonObject>())
        {
            var exchange = JsonRead.Str(t["exch"]) ?? "";
            var known = await SymbolByBrokerSymbolAsync(JsonRead.Str(t["tsym"]) ?? "", exchange, cancellationToken);
            var side = JsonRead.Str(t["trantype"]) ?? "";
            var average = BookFormatting.Round2(JsonRead.Float(t["avgprc"]));
            var quantity = JsonRead.Int(t["qty"]);
            trades.Add(new JsonObject
            {
                ["symbol"] = known ?? JsonRead.Str(t["tsym"]) ?? "", ["exchange"] = exchange, ["product"] = known is null ? JsonRead.Get(t, "prd", "") : BookProduct(exchange, JsonRead.Str(t["prd"]) ?? ""),
                ["action"] = known is not null ? (side == "B" ? "BUY" : side == "S" ? "SELL" : side) : side, ["quantity"] = quantity, ["average_price"] = average,
                ["trade_value"] = BookFormatting.Round2(average * quantity), ["orderid"] = JsonRead.Get(t, "norenordno", ""), ["timestamp"] = JsonRead.Get(t, "norentm", ""),
            });
        }

        return trades;
    }

    /// <summary>The realised and unrealised profit of a position row: the broker's own figures, else last price against average price over the net quantity.</summary>
    private static (double Realised, double Unrealised) Pnl(JsonObject p)
    {
        var realised = JsonRead.Float(p["rpnl"]);
        var unrealised = JsonRead.Float(p["urmtom"]);
        if (unrealised == 0 && JsonRead.Float(p["netqty"]) != 0)
        {
            var factor = p.ContainsKey("prcftr") ? JsonRead.Float(p["prcftr"]) : 1;
            unrealised = (JsonRead.Float(p["lp"]) - JsonRead.Float(p["netavgprc"])) * JsonRead.Float(p["netqty"]) * factor;
        }

        return (realised, unrealised);
    }

    public override async Task<JsonArray> GetPositionsAsync(BrokerSession session, CancellationToken cancellationToken)
    {
        var positions = new JsonArray();
        foreach (var p in (await BookAsync(session, "/PositionBook", Account(session), cancellationToken)).OfType<JsonObject>())
        {
            var exchange = JsonRead.Str(p["exch"]) ?? "";
            var known = await SymbolByBrokerSymbolAsync(JsonRead.Str(p["tsym"]) ?? "", exchange, cancellationToken);
            var product = JsonRead.Str(p["prd"]) ?? "";
            if (known is not null) product = BookProduct(exchange, product);
            var (realised, unrealised) = Pnl(p);
            positions.Add(new JsonObject
            {
                ["symbol"] = known ?? JsonRead.Str(p["tsym"]) ?? "", ["exchange"] = exchange, ["product"] = product, ["quantity"] = JsonRead.Get(p, "netqty", 0),
                ["average_price"] = JsonRead.Get(p, "netavgprc", 0.0), ["realized_pnl"] = realised, ["unrealized_pnl"] = unrealised, ["ltp"] = JsonRead.Get(p, "lp", 0.0),
                ["pnl"] = BookFormatting.Round2(realised + unrealised),
            });
        }

        return positions;
    }

    public override async Task<BrokerBook> GetHoldingsAsync(BrokerSession session, CancellationToken cancellationToken)
    {
        var holdings = new JsonArray();
        double held = 0, invested = 0;
        foreach (var h in (await BookAsync(session, "/Holdings", Account(session, holdings: true), cancellationToken)).OfType<JsonObject>())
        {
            var entries = (h["exch_tsym"] as JsonArray ?? []).OfType<JsonObject>().Where(e => JsonRead.Str(e["exch"]) == "NSE").ToList();
            var quantity = JsonRead.Int(h["holdqty"]) + Math.Max(JsonRead.Int(h["npoadqty"]), JsonRead.Int(h["dpqty"]));
            foreach (var entry in entries)
            {
                var brokerSymbol = JsonRead.Str(entry["tsym"]) ?? "";
                holdings.Add(new JsonObject
                {
                    ["symbol"] = await SymbolByBrokerSymbolAsync(brokerSymbol, "NSE", cancellationToken) ?? brokerSymbol, ["exchange"] = JsonRead.Str(entry["exch"]),
                    ["quantity"] = quantity, ["product"] = JsonRead.Get(entry, "product", "CNC"), ["avg_price"] = JsonRead.Float(h["upldprc"]), ["pnl"] = 0.0, ["pnlpercent"] = 0.0,
                });
            }

            if (JsonRead.Str(h["stat"]) != "Ok" || entries.Count == 0) continue;
            var price = JsonRead.Float(h["upldprc"]);
            invested += (JsonRead.Float(h["holdqty"]) + Math.Max(JsonRead.Float(h["npoadqty"]), JsonRead.Float(h["dpqty"]))) * price;
            held += (JsonRead.Float(h["btstqty"]) + JsonRead.Float(h["holdqty"]) + JsonRead.Float(h["brkcolqty"]) + JsonRead.Float(h["unplgdqty"]) + JsonRead.Float(h["benqty"])
                + Math.Max(JsonRead.Float(h["npoadqty"]), JsonRead.Float(h["dpqty"])) - JsonRead.Float(h["usedqty"])) * price;
        }

        return new BrokerBook(holdings, new JsonObject { ["totalholdingvalue"] = held, ["totalinvvalue"] = invested, ["totalprofitandloss"] = 0, ["totalpnlpercentage"] = 0 });
    }

    public override async Task<JsonObject> GetFundsAsync(BrokerSession session, CancellationToken cancellationToken)
    {
        var limits = await PostAsync(session, "/Limits", Account(session), cancellationToken) as JsonObject;
        if (JsonRead.Str(limits?["stat"]) != "Ok")
        {
            if (SessionLapsed(JsonRead.Str(limits?["emsg"]))) throw new BrokerException($"{DisplayName} says the session has expired. Log in to {DisplayName} again.", 403);
            throw new BrokerException($"{DisplayName} could not report funds: {(JsonRead.Str(limits?["emsg"]) is { Length: > 0 } m ? m : "try again in a moment.")}");
        }

        double realised = 0, unrealised = 0;
        if (await PostAsync(session, "/PositionBook", Account(session), cancellationToken) is JsonArray rows)
        {
            foreach (var p in rows.OfType<JsonObject>())
            {
                var (r, u) = Pnl(p);
                realised += r;
                unrealised += u;
            }
        }

        var used = JsonRead.Float(limits!["marginused"]);
        var collateral = JsonRead.Float(limits["collateral"]);
        if (collateral == 0) collateral = JsonRead.Float(limits["brkcollamt"]);
        return new JsonObject
        {
            ["availablecash"] = BookFormatting.Fixed2(JsonRead.Float(limits["cash"]) + JsonRead.Float(limits["payin"]) - used), ["collateral"] = BookFormatting.Fixed2(collateral),
            ["m2munrealized"] = BookFormatting.Fixed2(unrealised), ["m2mrealized"] = BookFormatting.Fixed2(realised), ["utiliseddebits"] = BookFormatting.Fixed2(used),
        };
    }
}
