using System.Text;
using System.Text.Json.Nodes;
using EPTradeBot.Core.Brokers;
using static EPTradeBot.Infrastructure.Brokers.JsonRead;

namespace EPTradeBot.Infrastructure.Brokers.Kotak;

/// <summary>
/// Kotak market data (broker/kotak/api/data.py): GET /script-details/1.0/quotes/neosymbol/{exchange|token,...}/all, with the access token as the Authorization header. A request that gets 429 is tried again after half a second,
/// a second and two (a Retry-After is honoured). At most 25 instruments go in one request, 0.2 s apart.
/// </summary>
public sealed partial class KotakPlugin
{
    private const int QuoteBatch = 25;

    /// <summary>Python's quote(text, safe="|,"): letters, digits and _ . - ~ | , stay; every other byte is its percent form.</summary>
    private static string QuoteQuery(string text)
    {
        var result = new StringBuilder();
        foreach (var b in Encoding.UTF8.GetBytes(text))
        {
            var c = (char)b;
            if (b < 128 && (char.IsAsciiLetterOrDigit(c) || c is '_' or '.' or '-' or '~' or '|' or ',')) result.Append(c);
            else result.Append('%').Append(b.ToString("X2", System.Globalization.CultureInfo.InvariantCulture));
        }

        return result.ToString();
    }

    private async Task<JsonArray?> QuotesAsync(Neo neo, string query, CancellationToken cancellationToken)
    {
        for (var attempt = 0; ; attempt++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, $"{neo.Base}/script-details/1.0/quotes/neosymbol/{QuoteQuery(query)}/all");
            request.Headers.TryAddWithoutValidation("Authorization", neo.Access);
            HttpResponseMessage response;
            try
            {
                response = await Client().SendAsync(request, cancellationToken);
            }
            catch (HttpRequestException)
            {
                return null;
            }

            using (response)
            {
                if ((int)response.StatusCode == 429 && attempt < 3)
                {
                    var wait = response.Headers.RetryAfter?.Delta ?? TimeSpan.FromSeconds(0.5 * Math.Pow(2, attempt));
                    await Task.Delay(sessions.RetryBackoff(Name, wait), cancellationToken);
                    continue;
                }

                return (int)response.StatusCode == 200 ? await ReadAsync(response, cancellationToken) as JsonArray : null;
            }
        }
    }

    /// <summary>The last price of each (symbol, exchange) the positions hold, for those Kotak answers. A read that fails leaves its instruments without one: a price is a convenience and never fails the positions.</summary>
    private async Task<Dictionary<(string Symbol, string Exchange), double>> LtpsAsync(Neo neo, List<JsonObject> rows, CancellationToken cancellationToken)
    {
        var prices = new Dictionary<(string, string), double>();
        var wanted = new List<(string Symbol, string Exchange)>();
        foreach (var r in rows)
        {
            var exchange = Exchange(Str(r["exSeg"]));
            var symbol = (await SymbolOrNullAsync(r, exchange, cancellationToken)) ?? Str(r["trdSym"]) ?? "";
            if (symbol.Length > 0 && !wanted.Contains((symbol, exchange))) wanted.Add((symbol, exchange));
        }

        for (var i = 0; i < wanted.Count; i += QuoteBatch)
        {
            var queries = new List<(string Query, (string Symbol, string Exchange) Item)>();
            foreach (var item in wanted.Skip(i).Take(QuoteBatch))
            {
                var record = await symbols.FindAsync(Name, item.Symbol, item.Exchange, cancellationToken);
                if (record is null || string.IsNullOrEmpty(record.Token) || string.IsNullOrEmpty(record.BrokerExchange)) continue;
                var segment = Segment(record.BrokerExchange) is { } mapped ? Str(mapped)! : record.BrokerExchange;
                queries.Add(($"{segment}|{record.Token}", item));
            }

            if (queries.Count > 0 && await QuotesAsync(neo, string.Join(',', queries.Select(q => q.Query)), cancellationToken) is { } answer)
            {
                var lookup = new Dictionary<string, JsonObject>();
                foreach (var quote in answer.OfType<JsonObject>())
                {
                    var exchange = Str(quote["exchange"]) ?? "";
                    lookup[$"{exchange}|{Str(quote["exchange_token"]) ?? ""}"] = quote;
                    if (Truthy(quote["display_symbol"])) lookup[$"{exchange}|{(Str(quote["display_symbol"]) ?? "").Replace("-EQ", "", StringComparison.Ordinal).Replace("-IN", "", StringComparison.Ordinal)}"] = quote;
                }

                foreach (var (query, item) in queries)
                {
                    var quote = lookup.GetValueOrDefault(query) ?? lookup.FirstOrDefault(kv => string.Equals(kv.Key, query, StringComparison.OrdinalIgnoreCase)).Value;
                    if (quote is not null && Float(quote["ltp"]) is var ltp && ltp != 0) prices[item] = ltp;
                }
            }

            if (i + QuoteBatch < wanted.Count) await Task.Delay(sessions.RetryBackoff(Name, TimeSpan.FromMilliseconds(200)), cancellationToken);
        }

        return prices;
    }

    // ---- quotes, depth and several quotes ----------------------------------------------------------------------

    private static readonly Dictionary<string, string[]> IndexNames = new(StringComparer.Ordinal)
    {
        ["NIFTY"] = ["Nifty 50"], ["NIFTY50"] = ["Nifty 50"], ["BANKNIFTY"] = ["Nifty Bank"], ["FINNIFTY"] = ["Nifty Fin Service"],
        ["MIDCPNIFTY"] = ["Nifty Mid Select", "Nifty Midcap Sel", "Nifty Midcap Select", "NIFTY MID SELECT"], ["NIFTYNXT50"] = ["Nifty Next 50"], ["INDIAVIX"] = ["India VIX"], ["SENSEX"] = ["SENSEX"], ["BANKEX"] = ["BANKEX"],
    };

    private static string[] IndexCandidates(string symbol) => IndexNames.GetValueOrDefault(symbol.ToUpperInvariant(), [symbol]);

    private static string IndexSegment(string exchange) => exchange == "BSE_INDEX" ? "bse_cm" : "nse_cm";

    /// <summary>The segment Kotak's quote query uses for a master-contract exchange: the segment as stored, or the plain exchange name mapped to it.</summary>
    private static string QuerySegment(string brokerExchange) => Segment(brokerExchange) is { } mapped ? Str(mapped)! : brokerExchange;

    private static double Dbl(JsonNode? node, double fallback = 0) => Float(node, fallback);

    /// <summary>int(float(x)) that gives zero for anything else.</summary>
    private static long Whole(JsonNode? node) => Float(node) is var f && double.IsFinite(f) ? (long)Math.Truncate(f) : 0;

    /// <summary>Python's int(x) on a quote field: a whole number, whole-number text, or a number that is cut; text with a fraction is not one.</summary>
    private static long PyInt(JsonNode? node)
    {
        if (node is null) return 0;
        if (Str(node) is { } text && node is JsonValue v && v.TryGetValue<string>(out _)) return long.TryParse(text.Trim(), System.Globalization.NumberStyles.AllowLeadingSign, System.Globalization.CultureInfo.InvariantCulture, out var n) ? n : throw new FormatException("Not a whole number.");
        return (long)Math.Truncate(Float(node));
    }

    private async Task<JsonObject> QuoteDataAsync(BrokerSession session, string symbol, string exchange, string what, CancellationToken cancellationToken)
    {
        var neo = Parse(session);
        JsonArray? answer = null;
        if (exchange.Contains("INDEX", StringComparison.OrdinalIgnoreCase))
        {
            foreach (var name in IndexCandidates(symbol))
            {
                answer = await QuotesAsync(neo, $"{IndexSegment(exchange)}|{name}", cancellationToken);
                if (answer is { Count: > 0 }) break;
                answer = null;
            }
        }
        else
        {
            var record = await symbols.FindAsync(Name, symbol, exchange, cancellationToken);
            if (record is not null) answer = await QuotesAsync(neo, $"{QuerySegment(record.BrokerExchange)}|{record.Token}", cancellationToken);
        }

        if (answer is { Count: > 0 } && answer[0] is JsonObject first) return first;
        throw new BrokerException($"{Display} could not give the {what}. Try again in a moment.", 500);
    }

    private static JsonObject QuoteOf(JsonObject quote)
    {
        var ohlc = quote["ohlc"] as JsonObject ?? [];
        var book = quote["depth"] as JsonObject ?? [];
        var buy = book["buy"] as JsonArray ?? [];
        var sell = book["sell"] as JsonArray ?? [];
        var ltp = Float(quote["ltp"]);
        return new JsonObject
        {
            ["bid"] = buy.Count > 0 && buy[0] is JsonObject b ? Float(b["price"]) : ltp, ["ask"] = sell.Count > 0 && sell[0] is JsonObject a ? Float(a["price"]) : ltp, ["open"] = Float(ohlc["open"]),
            ["high"] = Float(ohlc["high"]), ["low"] = Float(ohlc["low"]), ["ltp"] = ltp, ["prev_close"] = Float(ohlc["close"]), ["volume"] = Float(quote["last_volume"]), ["oi"] = PyInt(quote["open_int"]),
        };
    }

    public async Task<JsonObject> GetQuoteAsync(BrokerSession session, string symbol, string exchange, CancellationToken cancellationToken)
    {
        var quote = await QuoteDataAsync(session, symbol, exchange, "quote", cancellationToken);
        try
        {
            return QuoteOf(quote);
        }
        catch (FormatException)
        {
            return new JsonObject { ["bid"] = 0, ["ask"] = 0, ["open"] = 0, ["high"] = 0, ["low"] = 0, ["ltp"] = 0, ["prev_close"] = 0, ["volume"] = 0, ["oi"] = 0 };
        }
    }

    private static JsonArray Levels(JsonNode? side)
    {
        var levels = new JsonArray();
        if (side is JsonArray list)
        {
            foreach (var level in list.Take(5).OfType<JsonObject>()) levels.Add(new JsonObject { ["price"] = Float(level["price"]), ["quantity"] = PyInt(level["quantity"]) });
        }

        while (levels.Count < 5) levels.Add(new JsonObject { ["price"] = 0, ["quantity"] = 0 });
        return levels;
    }

    public async Task<JsonObject> GetDepthAsync(BrokerSession session, string symbol, string exchange, CancellationToken cancellationToken)
    {
        var quote = await QuoteDataAsync(session, symbol, exchange, "depth", cancellationToken);
        var book = quote["depth"] as JsonObject ?? [];
        var bids = Levels(book["buy"]);
        var asks = Levels(book["sell"]);
        var bidTotal = bids.OfType<JsonObject>().Sum(l => Math.Max(0, Whole(l["quantity"])));
        var askTotal = asks.OfType<JsonObject>().Sum(l => Math.Max(0, Whole(l["quantity"])));
        var ohlc = quote["ohlc"] as JsonObject ?? [];
        return new JsonObject
        {
            ["bids"] = bids, ["asks"] = asks, ["totalbuyqty"] = Whole(quote["total_buy"]) is var tb && tb != 0 ? tb : bidTotal, ["totalsellqty"] = Whole(quote["total_sell"]) is var ts && ts != 0 ? ts : askTotal,
            ["ltp"] = Float(quote["ltp"]), ["ltq"] = Whole(quote["last_traded_quantity"]), ["open"] = Float(ohlc["open"]), ["high"] = Float(ohlc["high"]), ["low"] = Float(ohlc["low"]), ["prev_close"] = Float(ohlc["close"]),
            ["volume"] = Whole(quote["last_volume"]), ["oi"] = Whole(quote["open_int"]),
        };
    }

    public async Task<JsonArray> GetMultiQuotesAsync(BrokerSession session, IReadOnlyList<SymbolRecord> instruments, CancellationToken cancellationToken)
    {
        var neo = Parse(session);
        var results = new JsonArray();
        var failed = 0;
        var batches = (instruments.Count + QuoteBatch - 1) / QuoteBatch;
        for (var i = 0; i < instruments.Count; i += QuoteBatch)
        {
            var batch = instruments.Skip(i).Take(QuoteBatch).ToList();
            var outcome = await BatchQuotesAsync(neo, batch, cancellationToken);
            if (outcome is null)
            {
                failed++;
                foreach (var item in batch) results.Add(new JsonObject { ["symbol"] = item.Symbol, ["exchange"] = item.Exchange, ["error"] = $"{Display} could not give this quote. Try again in a moment." });
            }
            else
            {
                foreach (var item in outcome) results.Add(item);
            }

            if (i + QuoteBatch < instruments.Count) await Task.Delay(sessions.RetryBackoff(Name, TimeSpan.FromMilliseconds(200)), cancellationToken);
        }

        if (failed > 0 && failed == batches) throw new BrokerException($"{Display} could not give the quotes. Try again in a moment.", 500);
        return results;
    }

    private async Task<List<JsonObject>?> BatchQuotesAsync(Neo neo, List<SymbolRecord> batch, CancellationToken cancellationToken)
    {
        var queries = new List<(string Query, SymbolRecord Item)>();
        foreach (var item in batch)
        {
            var query = item.Exchange.Contains("INDEX", StringComparison.OrdinalIgnoreCase) ? $"{IndexSegment(item.Exchange)}|{IndexCandidates(item.Symbol)[0]}" : $"{QuerySegment(item.BrokerExchange)}|{item.Token}";
            queries.Add((query, item));
        }

        var answer = await QuotesAsync(neo, string.Join(',', queries.Select(q => q.Query)), cancellationToken);
        if (answer is null) return null;
        if (answer.Count == 0) return [];
        var lookup = new Dictionary<string, JsonObject>();
        foreach (var quote in answer.OfType<JsonObject>())
        {
            var exchange = Str(quote["exchange"]) ?? "";
            lookup[$"{exchange}|{Str(quote["exchange_token"]) ?? ""}"] = quote;
            if (Truthy(quote["display_symbol"])) lookup[$"{exchange}|{(Str(quote["display_symbol"]) ?? "").Replace("-EQ", "", StringComparison.Ordinal).Replace("-IN", "", StringComparison.Ordinal)}"] = quote;
        }

        var results = new List<JsonObject>();
        foreach (var (query, item) in queries)
        {
            var quote = lookup.GetValueOrDefault(query) ?? lookup.FirstOrDefault(kv => string.Equals(kv.Key, query, StringComparison.OrdinalIgnoreCase)).Value;
            if (quote is null) results.Add(new JsonObject { ["symbol"] = item.Symbol, ["exchange"] = item.Exchange, ["error"] = "No quote data available" });
            else results.Add(new JsonObject { ["symbol"] = item.Symbol, ["exchange"] = item.Exchange, ["data"] = QuoteOf(quote) });
        }

        return results;
    }
}
