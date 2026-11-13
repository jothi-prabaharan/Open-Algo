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
[BrokerPlugin]
public sealed partial class ShoonyaPlugin(IHttpClientFactory http, BrokerSessionFactory sessions, ISymbolMaster symbols, ILogger<ShoonyaPlugin> logger)
    : IBrokerOrders, IBrokerPortfolio, IBrokerFunds, IBrokerMultiQuotes, IBrokerLogin
{
    private const string Host = "https://api.shoonya.com";
    private const string Api = "/NorenWClientAPI";
    private const int QuoteAttempts = 3;

    public string Name => "shoonya";
    public string DisplayName => "Shoonya";
    public BrokerCapability Capabilities => BrokerCapability.Orders | BrokerCapability.Portfolio | BrokerCapability.Funds | BrokerCapability.Quotes | BrokerCapability.Depth;
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

    private async Task<JsonNode?> PostAsync(BrokerSession session, string endpoint, JsonObject payload, CancellationToken cancellationToken)
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

    private JsonObject Account(BrokerSession session, bool holdings = false)
    {
        var id = UserId(session);
        var body = new JsonObject { ["uid"] = id, ["actid"] = id };
        if (holdings) body["prd"] = "C";
        return body;
    }

    // ---- orders ----------------------------------------------------------------------------------------------

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
        if (order.PriceType is "MARKET" or "SL-M")
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
            if (order.PriceType == "SL-M" && !converted)
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
    private async Task<JsonArray> BookAsync(BrokerSession session, string endpoint, JsonObject payload, CancellationToken cancellationToken)
    {
        var reply = await PostAsync(session, endpoint, payload, cancellationToken);
        if (reply is JsonArray rows) return rows;
        var message = Str((reply as JsonObject)?["emsg"]);
        if (SessionExpired(message)) throw new BrokerException($"{DisplayName} says the session has expired. Log in to {DisplayName} again.", 403);
        return [];
    }

    private static string NormalizeStatus(string? raw)
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

    private static readonly HashSet<string> Derivatives = ["NFO", "MCX", "BFO", "CDS"];

    private static string BookProduct(string exchange, string product) =>
        (exchange is "NSE" or "BSE") && product == "C" ? "CNC" : product == "I" ? "MIS" : Derivatives.Contains(exchange) && product == "M" ? "NRML" : product;

    private async Task<string?> SymbolByTokenAsync(string token, string exchange, CancellationToken cancellationToken) =>
        token.Length == 0 ? null : (await symbols.FindByTokenAsync(Name, token, exchange, cancellationToken))?.Symbol;

    private async Task<string?> SymbolByBrokerSymbolAsync(string brokerSymbol, string exchange, CancellationToken cancellationToken) =>
        (await symbols.FindByBrokerSymbolAsync(Name, brokerSymbol, exchange, cancellationToken))?.Symbol;

    public async Task<BrokerBook> GetOrderBookAsync(BrokerSession session, CancellationToken cancellationToken)
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

    public async Task<JsonArray> GetTradeBookAsync(BrokerSession session, CancellationToken cancellationToken)
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

    public async Task<JsonArray> GetPositionsAsync(BrokerSession session, CancellationToken cancellationToken)
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

    public async Task<BrokerBook> GetHoldingsAsync(BrokerSession session, CancellationToken cancellationToken)
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

    public async Task<JsonObject> GetFundsAsync(BrokerSession session, CancellationToken cancellationToken)
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
            reply = await PostAsync(session, "/GetQuotes", new JsonObject { ["exch"] = DataExchange(exchange), ["token"] = token, ["uid"] = UserId(session) }, cancellationToken) as JsonObject
                ?? throw new BrokerException($"{DisplayName} sent a reply EP Trade bot could not read. Try again in a moment.", 502);
            if (Str(reply["stat"]) != "Ok")
            {
                if (SessionExpired(Str(reply["emsg"]))) throw new BrokerException($"{DisplayName} says the session has expired. Log in to {DisplayName} again.", 403);
                throw new BrokerException($"{DisplayName} could not send the quote: {Str(reply["emsg"]) ?? "no reason was given."}");
            }

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

    private static JsonObject QuoteData(JsonObject q) => new()
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

    public async Task<JsonObject> GetDepthAsync(BrokerSession session, string symbol, string exchange, CancellationToken cancellationToken)
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
