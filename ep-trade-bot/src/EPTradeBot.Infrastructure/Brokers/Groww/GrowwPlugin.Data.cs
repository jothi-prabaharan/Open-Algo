using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using EPTradeBot.Core.Brokers;
using static EPTradeBot.Infrastructure.Brokers.JsonRead;

namespace EPTradeBot.Infrastructure.Brokers.Groww;

/// <summary>
/// Groww quotes, depth and several quotes (broker/groww api/data.py). One quote or one depth is GET /v1/live-data/quote with the exchange (NSE or BSE), the segment and the trading symbol; an index is asked for in the cash segment of its
/// exchange. Several quotes ask /v1/live-data/ohlc and /v1/live-data/ltp for each segment, 50 symbols a request, and for F&amp;O symbols then add the bid, ask, volume and open interest from one quote request each, giving up after four throttles in a row.
/// A call Groww refuses is an error beside its symbol.
/// </summary>
public sealed partial class GrowwPlugin : IBrokerMultiQuotes
{
    private const int MultiBatch = 50;
    private const int MaxThrottles = 4;

    private static string Unavailable(string exchange) => $"{Display} does not provide market data for the {exchange} exchange. Supported: NSE, BSE, NFO, BFO, NSE_INDEX and BSE_INDEX.";

    private static (string Exchange, string Segment) MarketOf(string exchange) => exchange switch
    {
        "NSE" or "NSE_INDEX" => ("NSE", "CASH"),
        "BSE" or "BSE_INDEX" => ("BSE", "CASH"),
        "NFO" => ("NSE", "FNO"),
        "BFO" => ("BSE", "FNO"),
        _ => throw new BrokerException(Unavailable(exchange), 500),
    };

    /// <summary>A derivative's OpenAlgo symbol in Groww's own spelling when the master contract has no broker symbol for it (the day of the expiry is dropped).</summary>
    internal static string ConvertDerivative(string symbol)
    {
        var future = Regex.Match(symbol, @"^([A-Z]+)(\d{2})([A-Z]{3})(\d{2})(FUT)$");
        if (future.Success) return $"{future.Groups[1].Value}{future.Groups[4].Value}{future.Groups[3].Value}{future.Groups[5].Value}";
        var option = Regex.Match(symbol, @"^([A-Z]+)(\d{2})([A-Z]{3})(\d{2})(\d+)(CE|PE)$");
        return option.Success ? $"{option.Groups[1].Value}{option.Groups[4].Value}{option.Groups[3].Value}{option.Groups[5].Value}{option.Groups[6].Value}" : symbol;
    }

    // ---- numbers as OpenAlgo reads them ------------------------------------------------------------------------

    private static double SafeFloat(JsonNode? node)
    {
        if (node is null) return 0;
        var value = Float(node, double.NaN);
        return double.IsFinite(value) ? value : 0;
    }

    private static long SafeInt(JsonNode? node)
    {
        if (node is null) return 0;
        if (node is JsonValue v && v.TryGetValue<string>(out var text)) return long.TryParse(text.Trim(), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var whole) ? whole : 0;
        var value = Float(node, double.NaN);
        return double.IsFinite(value) ? (long)Math.Truncate(value) : 0;
    }

    /// <summary>The open, high, low and close of a quote: an object, or the text {open: 1,high: 2,low: 0.5,close: 1.5}, of which the pairs read before one that is not a number are kept.</summary>
    private static Dictionary<string, double> OhlcOf(JsonNode? node)
    {
        var ohlc = new Dictionary<string, double>();
        if (node is JsonObject o)
        {
            foreach (var key in new[] { "open", "high", "low", "close" })
                if (o.ContainsKey(key)) ohlc[key] = SafeFloat(o[key]);
        }
        else if (node is JsonValue v && v.TryGetValue<string>(out var text))
        {
            foreach (var part in text.Trim('{', '}').Split(','))
            {
                var pair = part.Split(':');
                if (pair.Length != 2) continue;
                if (!double.TryParse(pair[1].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var number)) break;
                ohlc[pair[0].Trim()] = number;
            }
        }

        return ohlc;
    }

    private static double Price(Dictionary<string, double> ohlc, string key) => ohlc.GetValueOrDefault(key);

    // ---- the call, as OpenAlgo makes it ------------------------------------------------------------------------

    /// <summary>
    /// One live-data call, answered as OpenAlgo's get_api_response answers: the body when it was an object, else {"error": ...} with Groww's text beside it: HTTP error for a status of 400 or more, not JSON, or the failure to reach Groww.
    /// </summary>
    private async Task<JsonObject> ApiAsync(BrokerSession session, string path, string category, IEnumerable<KeyValuePair<string, string>> query, CancellationToken cancellationToken)
    {
        try
        {
            var (status, body, text) = await SendAsync(session, HttpMethod.Get, path, category, query, null, cancellationToken);
            if (status >= 400) return new JsonObject { ["error"] = $"HTTP error: {status}", ["details"] = text };
            if (body is JsonObject answer) return answer;
            return new JsonObject { ["error"] = body is null ? "Response is not valid JSON" : $"{Display} sent an answer that could not be read", ["content"] = text };
        }
        catch (BrokerException ex) when (ex.HttpStatus == 502)
        {
            return new JsonObject { ["error"] = ex.Message };
        }
    }

    /// <summary>Groww's own reason for a call that failed: the error in the details it sent, else the one in the answer, else <paramref name="fallback"/>.</summary>
    private static string ReasonOf(JsonObject response, string fallback)
    {
        JsonNode? details = response["details"];
        if (details is JsonValue v && v.TryGetValue<string>(out var text))
        {
            try
            {
                details = JsonNode.Parse(text);
            }
            catch (System.Text.Json.JsonException)
            {
                details = null;
            }
        }

        if (details is JsonObject d && ReasonOrNull(d) is { } reason) return reason;
        return ReasonOrNull(response) ?? fallback;
    }

    private static string? ReasonOrNull(JsonObject body)
    {
        if (body["error"] is JsonObject error && Truthy(error["message"])) return Str(error["message"]);
        return Truthy(body["message"]) ? Str(body["message"]) : null;
    }

    /// <summary>The first of the names that holds something (Python's <c>a or b or c</c>).</summary>
    private static JsonNode? FirstOf(JsonObject payload, params string[] names)
    {
        foreach (var name in names)
            if (Truthy(payload[name])) return payload[name];
        return null;
    }

    private async Task<(string Exchange, string Segment, string TradingSymbol)> QuoteTargetAsync(string symbol, string exchange, CancellationToken cancellationToken)
    {
        var market = MarketOf(exchange);
        var record = await symbols.FindAsync(Name, symbol, exchange, cancellationToken);
        var trading = !string.IsNullOrEmpty(record?.BrokerSymbol) ? record.BrokerSymbol : exchange is "NFO" or "BFO" ? ConvertDerivative(symbol) : symbol;
        return (market.Exchange, market.Segment, trading);
    }

    private Task<JsonObject> QuoteCallAsync(BrokerSession session, (string Exchange, string Segment, string TradingSymbol) target, CancellationToken cancellationToken) =>
        ApiAsync(session, "/v1/live-data/quote", "live", [KeyValuePair.Create("exchange", target.Exchange), KeyValuePair.Create("segment", target.Segment), KeyValuePair.Create("trading_symbol", target.TradingSymbol)], cancellationToken);

    // ---- quote -----------------------------------------------------------------------------------------------

    public async Task<JsonObject> GetQuoteAsync(BrokerSession session, string symbol, string exchange, CancellationToken cancellationToken)
    {
        var target = await QuoteTargetAsync(symbol, exchange, cancellationToken);
        var response = await QuoteCallAsync(session, target, cancellationToken);
        if (Truthy(response["error"])) throw new BrokerException($"{Display} did not return a quote for {symbol}: {ReasonOf(response, "no response")}", 500);
        if (Str(response["status"]) != "SUCCESS" || response["payload"] is not JsonObject payload) throw new BrokerException($"{Display} returned no quote for {symbol}", 500);
        var ohlc = OhlcOf(payload.ContainsKey("ohlc") ? payload["ohlc"] : new JsonObject());
        var depth = payload["depth"] as JsonObject;
        var topBid = (depth?["buy"] as JsonArray)?.FirstOrDefault() as JsonObject;
        var topAsk = (depth?["sell"] as JsonArray)?.FirstOrDefault() as JsonObject;
        var bid = FirstOf(payload, "bid_price", "bid", "best_bid_price") ?? topBid?["price"];
        var ask = FirstOf(payload, "offer_price", "ask", "best_offer_price", "best_ask_price") ?? topAsk?["price"];
        var bidQty = FirstOf(payload, "bid_quantity", "bid_size", "best_bid_quantity") ?? topBid?["quantity"];
        var askQty = FirstOf(payload, "offer_quantity", "ask_quantity", "ask_size", "offer_size", "best_offer_quantity") ?? topAsk?["quantity"];
        var volume = FirstOf(payload, "volume", "total_volume", "traded_volume");
        var oi = FirstOf(payload, "open_interest", "oi");
        return new JsonObject
        {
            ["ltp"] = SafeFloat(payload["last_price"]), ["open"] = Price(ohlc, "open"), ["high"] = Price(ohlc, "high"), ["low"] = Price(ohlc, "low"), ["prev_close"] = Price(ohlc, "close"), ["volume"] = SafeInt(volume), ["bid"] = SafeFloat(bid),
            ["ask"] = SafeFloat(ask), ["bid_qty"] = SafeInt(bidQty), ["ask_qty"] = SafeInt(askQty), ["oi"] = exchange is "NFO" or "BFO" ? SafeInt(oi) : 0,
        };
    }

    // ---- depth -----------------------------------------------------------------------------------------------

    public async Task<JsonObject> GetDepthAsync(BrokerSession session, string symbol, string exchange, CancellationToken cancellationToken)
    {
        var target = await QuoteTargetAsync(symbol, exchange, cancellationToken);
        var response = await QuoteCallAsync(session, target, cancellationToken);
        if (Str(response["status"]) != "SUCCESS" || response["payload"] is not JsonObject payload)
            throw new BrokerException($"{Display} did not give the market depth for {symbol}: {(Truthy(response["error"]) ? ReasonOf(response, "it did not answer the request") : "it sent no quote")}. Try again in a moment.", 502);
        var ohlc = OhlcOf(payload.ContainsKey("ohlc") ? payload["ohlc"] : new JsonObject());
        var depth = payload["depth"] as JsonObject;
        JsonArray Side(string key)
        {
            var rows = new JsonArray();
            foreach (var level in (depth?[key] as JsonArray ?? []).OfType<JsonObject>().Take(5))
                rows.Add(new JsonObject { ["price"] = SafeFloat(level.ContainsKey("price") ? level["price"] : 0), ["quantity"] = SafeInt(level.ContainsKey("quantity") ? level["quantity"] : 0) });
            while (rows.Count < 5) rows.Add(new JsonObject { ["price"] = 0, ["quantity"] = 0 });
            return rows;
        }

        return new JsonObject
        {
            ["bids"] = Side("buy"), ["asks"] = Side("sell"), ["ltp"] = SafeFloat(payload["last_price"]), ["ltq"] = SafeInt(payload["last_trade_quantity"]), ["open"] = Price(ohlc, "open"), ["high"] = Price(ohlc, "high"), ["low"] = Price(ohlc, "low"),
            ["prev_close"] = Price(ohlc, "close"), ["volume"] = SafeInt(payload["volume"]), ["totalbuyqty"] = SafeInt(payload["total_buy_quantity"]), ["totalsellqty"] = SafeInt(payload["total_sell_quantity"]),
            ["oi"] = exchange is "NFO" or "BFO" ? SafeInt(payload["open_interest"]) : 0,
        };
    }

    // ---- several quotes --------------------------------------------------------------------------------------

    private sealed record Wanted(string Symbol, string Exchange);

    public async Task<JsonArray> GetMultiQuotesAsync(BrokerSession session, IReadOnlyList<SymbolRecord> instruments, CancellationToken cancellationToken)
    {
        var all = new JsonArray();
        for (var start = 0; start < instruments.Count; start += MultiBatch)
            foreach (var item in await BatchAsync(session, instruments.Skip(start).Take(MultiBatch).ToList(), cancellationToken)) all.Add(item);
        return all;
    }

    private static JsonObject Failed(Wanted who, string error) => new() { ["symbol"] = who.Symbol, ["exchange"] = who.Exchange, ["error"] = error };

    private async Task<List<JsonObject>> BatchAsync(BrokerSession session, IReadOnlyList<SymbolRecord> batch, CancellationToken cancellationToken)
    {
        var cash = new List<string>();
        var fno = new List<string>();
        var map = new Dictionary<string, Wanted>();
        var skipped = new List<JsonObject>();
        foreach (var record in batch)
        {
            var who = new Wanted(record.Symbol, record.Exchange);
            if (record.Exchange is not ("NSE" or "BSE" or "NFO" or "BFO" or "NSE_INDEX" or "BSE_INDEX"))
            {
                skipped.Add(Failed(who, $"{Display} does not provide market data for the {record.Exchange} exchange"));
                continue;
            }

            var brokerSymbol = record.BrokerSymbol;
            if (string.IsNullOrEmpty(brokerSymbol) && record.Exchange is "NFO" or "BFO") brokerSymbol = ConvertDerivative(record.Symbol);
            if (string.IsNullOrEmpty(brokerSymbol))
            {
                skipped.Add(Failed(who, "Could not resolve broker symbol"));
                continue;
            }

            var key = $"{(record.Exchange is "NSE" or "NFO" or "NSE_INDEX" ? "NSE" : "BSE")}_{brokerSymbol}";
            map[key] = who;
            (record.Exchange is "NFO" or "BFO" ? fno : cash).Add(key);
        }

        if (cash.Count == 0 && fno.Count == 0) return skipped;
        var results = new List<JsonObject>();
        if (cash.Count > 0) results.AddRange(await OhlcBatchAsync(session, cash, "CASH", map, 0, cancellationToken));
        if (fno.Count > 0)
        {
            var found = await OhlcBatchAsync(session, fno, "FNO", map, 0, cancellationToken);
            await OverlayAsync(session, found, fno, map, cancellationToken);
            results.AddRange(found);
        }

        return [.. skipped, .. results];
    }

    private static JsonObject Priced(Wanted who, double open, double high, double low, double ltp, double close) => new()
    {
        ["symbol"] = who.Symbol, ["exchange"] = who.Exchange,
        ["data"] = new JsonObject { ["bid"] = 0, ["ask"] = 0, ["bid_qty"] = 0, ["ask_qty"] = 0, ["open"] = open, ["high"] = high, ["low"] = low, ["ltp"] = ltp, ["prev_close"] = close, ["volume"] = 0, ["oi"] = 0 },
    };

    private static string ErrorText(JsonNode? error) => error switch
    {
        JsonObject o when Truthy(o["message"]) => Str(o["message"])!,
        JsonValue v when v.TryGetValue<string>(out var text) => text,
        null => "API Error",
        _ => error.ToJsonString(),
    };

    private async Task<List<JsonObject>> OhlcBatchAsync(BrokerSession session, List<string> keys, string segment, Dictionary<string, Wanted> map, int retries, CancellationToken cancellationToken)
    {
        var results = new List<JsonObject>();
        Wanted Who(string key) => map.GetValueOrDefault(key) ?? new Wanted(key, "UNKNOWN");
        var joined = string.Join(",", keys);
        var response = await ApiAsync(session, "/v1/live-data/ohlc", "live", [KeyValuePair.Create("segment", segment), KeyValuePair.Create("exchange_symbols", joined)], cancellationToken);
        if (Truthy(response["error"]))
        {
            // A symbol Groww names as not valid is left out and the rest asked again, up to five times.
            var details = Str(response["details"]) ?? "";
            if (details.Contains("Invalid trading symbol", StringComparison.Ordinal) && Regex.Match(details, @"Invalid trading symbol: (\w+)") is { Success: true } match)
            {
                var invalid = match.Groups[1].Value;
                var rest = keys.Where(k => !k.Contains(invalid, StringComparison.Ordinal)).ToList();
                if (rest.Count > 0 && rest.Count < keys.Count && retries < 5)
                {
                    foreach (var key in keys.Where(k => k.Contains(invalid, StringComparison.Ordinal))) results.Add(Failed(Who(key), "Invalid trading symbol in Groww"));
                    results.AddRange(await OhlcBatchAsync(session, rest, segment, map, retries + 1, cancellationToken));
                    return results;
                }
            }

            foreach (var key in keys)
                if (!results.Any(r => Str(r["symbol"]) == Who(key).Symbol)) results.Add(Failed(Who(key), ErrorText(response["error"])));
            return results;
        }

        var payload = Str(response["status"]) == "SUCCESS" ? response["payload"] as JsonObject ?? [] : response;
        var prices = await ApiAsync(session, "/v1/live-data/ltp", "live", [KeyValuePair.Create("segment", segment), KeyValuePair.Create("exchange_symbols", joined)], cancellationToken);
        var ltpPayload = Str(prices["status"]) == "SUCCESS" ? prices["payload"] as JsonObject : null;
        var noPrice = "Groww returned no live price for this symbol";
        if (ltpPayload is null)
        {
            ltpPayload = [];
            noPrice = $"{Display} did not return live prices: {ReasonOf(prices, "no response")}";
        }

        try
        {
            foreach (var key in keys)
            {
                var who = Who(key);
                var ohlcNode = payload[key];
                var ltpNode = ltpPayload[key];
                if (!Truthy(ohlcNode) && ltpNode is null)
                {
                    results.Add(Failed(who, noPrice));
                    continue;
                }

                Dictionary<string, double>? ohlc = null;
                if (ohlcNode is JsonObject) ohlc = OhlcOf(ohlcNode);
                else if (ohlcNode is JsonValue v && v.TryGetValue<string>(out var text) && ParseText(text) is { Count: > 0 } parsed) ohlc = parsed;
                if (ltpNode is null)
                {
                    results.Add(Failed(who, noPrice));
                    continue;
                }

                var ltp = Float(ltpNode, double.NaN);
                if (double.IsNaN(ltp)) throw new FormatException($"could not convert string to float: {Str(ltpNode)}");
                ohlc ??= [];
                results.Add(Priced(who, ohlc.GetValueOrDefault("open"), ohlc.GetValueOrDefault("high"), ohlc.GetValueOrDefault("low"), ltp, ohlc.GetValueOrDefault("close")));
            }
        }
        catch (FormatException ex)
        {
            results.Clear();
            foreach (var key in keys) results.Add(Failed(Who(key), ex.Message));
        }

        return results;
    }

    /// <summary>The pairs of an OHLC text, or nothing when one is not a number (the pairs without a colon are skipped).</summary>
    private static Dictionary<string, double>? ParseText(string text)
    {
        var parsed = new Dictionary<string, double>();
        foreach (var part in text.Trim('{', '}', ' ').Split(','))
        {
            var at = part.IndexOf(':');
            if (at < 0) continue;
            if (!double.TryParse(part[(at + 1)..].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var number)) return null;
            parsed[part[..at].Trim()] = number;
        }

        return parsed;
    }

    /// <summary>
    /// Best-effort: the bid, ask, quantities, volume and open interest of each F&amp;O symbol from its own quote, one request at a time, added to what the batch gave. A symbol whose quote fails keeps its baseline, and four throttles in a row end the
    /// round so that a banned account is not spun against.
    /// </summary>
    private async Task OverlayAsync(BrokerSession session, List<JsonObject> found, List<string> keys, Dictionary<string, Wanted> map, CancellationToken cancellationToken)
    {
        var throttled = 0;
        foreach (var key in keys)
        {
            var who = map.GetValueOrDefault(key) ?? new Wanted(key, "UNKNOWN");
            var split = key.IndexOf('_');
            if (split < 0) continue;
            var response = await ApiAsync(session, "/v1/live-data/quote", "live", [KeyValuePair.Create("exchange", key[..split]), KeyValuePair.Create("segment", "FNO"), KeyValuePair.Create("trading_symbol", key[(split + 1)..])], cancellationToken);
            if (Str(response["status"]) != "SUCCESS" || response["payload"] is not JsonObject payload)
            {
                var error = response["error"] is { } e ? ErrorText(e) : "No quote data available";
                if (error.Contains("429", StringComparison.Ordinal) || error.Contains("Rate limit", StringComparison.Ordinal))
                {
                    if (++throttled >= MaxThrottles) break;
                    continue;
                }

                throttled = 0;
                continue;
            }

            throttled = 0;
            var depth = payload["depth"] as JsonObject;
            var topBid = (depth?["buy"] as JsonArray)?.FirstOrDefault() as JsonObject;
            var topAsk = (depth?["sell"] as JsonArray)?.FirstOrDefault() as JsonObject;
            var target = found.FirstOrDefault(r => Str(r["symbol"]) == who.Symbol && Str(r["exchange"]) == who.Exchange);
            if (target?["data"] is not JsonObject data) continue;
            data["bid"] = SafeFloat(payload["bid_price"] ?? topBid?["price"]);
            data["ask"] = SafeFloat(payload["offer_price"] ?? topAsk?["price"]);
            data["bid_qty"] = SafeInt(payload["bid_quantity"] ?? topBid?["quantity"]);
            data["ask_qty"] = SafeInt(payload["offer_quantity"] ?? topAsk?["quantity"]);
            data["volume"] = SafeInt(payload["volume"]);
            data["oi"] = SafeInt(payload["open_interest"]);
        }
    }
}
