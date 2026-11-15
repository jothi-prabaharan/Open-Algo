using System.Globalization;
using System.Text.Json.Nodes;
using EPTradeBot.Core.Brokers;
using static EPTradeBot.Infrastructure.Brokers.JsonRead;

namespace EPTradeBot.Infrastructure.Brokers.IndMoney;

/// <summary>
/// IndMoney quotes, depth, several quotes and history (broker/indmoney api/data.py). Scrip codes are the segment, an underscore and the security id (NIDX and BIDX for the indices). A quote is the full quote; when that fails or holds no price the last price and the depth
/// are read separately. Depth is the market depth call with the full quote's figures beside it. Several quotes are one full call (five hundred to a batch), split in halves when IndMoney refuses the batch for a scrip it does not know, the bad scrip remembered for five
/// minutes. History is one call for each chunk of days (seven for minute candles, fourteen for hourly, a year for daily and longer), and a range of a single day makes no chunk at all.
/// </summary>
public sealed partial class IndMoneyPlugin : IBrokerMultiQuotes, IBrokerHistory
{
    private const int QuoteBatch = 500;
    private static readonly TimeSpan BadScripTtl = TimeSpan.FromMinutes(5);
    private static readonly Lock BadGate = new();
    private static readonly Dictionary<string, long> BadScrips = [];
    private static readonly Dictionary<string, string> ScripSegments = new() { ["NSE"] = "NSE", ["BSE"] = "BSE", ["NFO"] = "NFO", ["BFO"] = "BFO", ["MCX"] = "MCX", ["CDS"] = "CDS", ["BCD"] = "BCD", ["NSE_INDEX"] = "NIDX", ["BSE_INDEX"] = "BIDX" };

    /// <summary>Forgets the scrips that were found unquotable (tests start each case with none).</summary>
    public static void ForgetBadScrips()
    {
        lock (BadGate) BadScrips.Clear();
    }

    private sealed class CallFailed(string message) : Exception(message);

    private static string ScripCode(SymbolRecord record, string exchange) =>
        ScripSegments.TryGetValue(exchange, out var segment) ? $"{segment}_{record.Token}" : throw new BrokerException($"{Display} does not give quotes for the {exchange} exchange.", 400);

    /// <summary>One data call, read as OpenAlgo's data module reads it: a status other than 200, a reply that is not JSON, or one that is neither successful nor carries data fails.</summary>
    private async Task<JsonObject> DataCallAsync(BrokerSession session, string path, IEnumerable<KeyValuePair<string, string>> query, CancellationToken cancellationToken)
    {
        var (status, body, text) = await SendAsync(session, HttpMethod.Get, path, query, null, cancellationToken);
        if (status != 200) throw new CallFailed($"Indmoney API HTTP Error {status}: {text}");
        if (body is not JsonObject response) throw new CallFailed("Indmoney API returned invalid JSON");
        if (response["success"] is JsonValue s && s.TryGetValue<bool>(out var ok) && ok) return response;
        var data = response["data"];
        var hasData = data is JsonArray { Count: > 0 }
            || (data is JsonObject d && (d["candles"] is JsonArray { Count: > 0 } || d.Any(kv => kv.Value is JsonObject nested && nested["candles"] is JsonArray { Count: > 0 })));
        if (hasData) return response;
        if (Str(response["status"]) != "success") throw new CallFailed($"Indmoney API Error ({(response["code"] is { } c ? Str(c) : "unknown")}): {(Truthy(response["message"]) ? Str(response["message"]) : response["error"] is { } e ? Str(e) : "Unknown error")}");
        return response;
    }

    /// <summary>A number as OpenAlgo cleans one: commas out, a whole number as such, anything that does not read as the default.</summary>
    private static double Clean(JsonNode? node, double fallback = 0)
    {
        if (node is null) return fallback;
        var text = (Str(node) ?? node.ToJsonString()).Replace(",", "", StringComparison.Ordinal).Trim();
        return text.Length > 0 && double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : fallback;
    }

    private static JsonObject? Scrip(JsonObject response, string code) => (response["data"] as JsonObject)?[code] as JsonObject;

    private static JsonObject MarketDepth(JsonNode? container, string code)
    {
        if (container is not JsonObject c) return [];
        if (c[code] is JsonObject nested && (nested.ContainsKey("depth") || nested.ContainsKey("aggregate"))) return nested;
        if (c.ContainsKey("depth") || c.ContainsKey("aggregate")) return c;
        if (c.Count == 1 && c.First().Value is JsonObject only && (only.ContainsKey("depth") || only.ContainsKey("aggregate"))) return only;
        return [];
    }

    private static (double Bid, double Ask) BestLevel(JsonObject depth)
    {
        double bid = 0, ask = 0;
        if (depth["depth"] is JsonArray { Count: > 0 } levels && levels[0] is JsonObject first)
        {
            if (first["buy"] is JsonObject buy && buy.ContainsKey("price")) bid = Clean(buy["price"]);
            if (first["sell"] is JsonObject sell && sell.ContainsKey("price")) ask = Clean(sell["price"]);
        }

        return (bid, ask);
    }

    private static JsonNode? First(JsonObject o, string first, string second) => o.ContainsKey(first) ? o[first] : o[second];

    public async Task<JsonObject> GetQuoteAsync(BrokerSession session, string symbol, string exchange, CancellationToken cancellationToken)
    {
        var record = await symbols.FindAsync(Name, symbol, exchange, cancellationToken)
            ?? throw new BrokerException($"{symbol} on {exchange} is not in the {Display} master contract. Check the symbol, or download the master contract again.", 400);
        var code = ScripCode(record, exchange);
        var query = new[] { new KeyValuePair<string, string>("scrip-codes", code) };
        var failures = 0;
        try
        {
            var full = Scrip(await DataCallAsync(session, "/market/quotes/full", query, cancellationToken), code);
            if (full is { Count: > 0 } && new[] { "ltp", "live_price", "open", "high", "low" }.Any(full.ContainsKey))
            {
                var (bid, ask) = BestLevel(MarketDepth(full["market_depth"], code));
                return new JsonObject
                {
                    ["ltp"] = Clean(First(full, "live_price", "ltp")), ["open"] = Clean(full["day_open"]), ["high"] = Clean(full["day_high"]), ["low"] = Clean(full["day_low"]), ["volume"] = Clean(full["volume"]),
                    ["prev_close"] = Clean(First(full, "prev_close", "close")), ["oi"] = Clean(First(full, "oi", "open_interest")), ["bid"] = bid, ["ask"] = ask,
                };
            }
        }
        catch (CallFailed)
        {
            failures++;
        }

        JsonObject? lastPrice = null;
        try
        {
            lastPrice = Scrip(await DataCallAsync(session, "/market/quotes/ltp", query, cancellationToken), code);
        }
        catch (CallFailed)
        {
            failures++;
        }

        double bidPrice = 0, askPrice = 0;
        try
        {
            var raw = Scrip(await DataCallAsync(session, "/market/quotes/mkt", query, cancellationToken), code);
            (bidPrice, askPrice) = BestLevel(MarketDepth(raw?["market_depth"], code));
        }
        catch (CallFailed)
        {
            failures++;
        }

        if (failures == 3) throw new BrokerException($"{Display} could not give a quote for {symbol} on {exchange}. Try again in a moment.", 500);
        return new JsonObject { ["ltp"] = lastPrice is { Count: > 0 } ? Clean(lastPrice["live_price"]) : 0, ["open"] = 0, ["high"] = 0, ["low"] = 0, ["volume"] = 0, ["oi"] = 0, ["bid"] = bidPrice, ["ask"] = askPrice, ["prev_close"] = 0 };
    }

    public async Task<JsonObject> GetDepthAsync(BrokerSession session, string symbol, string exchange, CancellationToken cancellationToken)
    {
        var record = await symbols.FindAsync(Name, symbol, exchange, cancellationToken)
            ?? throw new BrokerException($"{symbol} on {exchange} is not in the {Display} master contract. Check the symbol, or download the master contract again.", 400);
        var code = ScripCode(record, exchange);
        var query = new[] { new KeyValuePair<string, string>("scrip-codes", code) };
        JsonObject? full = null;
        try
        {
            full = Scrip(await DataCallAsync(session, "/market/quotes/full", query, cancellationToken), code);
        }
        catch (CallFailed)
        {
        }

        JsonObject? depthData;
        try
        {
            depthData = Scrip(await DataCallAsync(session, "/market/quotes/mkt", query, cancellationToken), code);
        }
        catch (CallFailed ex)
        {
            throw new BrokerException($"{Display} could not give the market depth for {symbol} on {exchange}: {ex.Message}", 500);
        }

        JsonObject? lastPrice = null;
        try
        {
            lastPrice = Scrip(await DataCallAsync(session, "/market/quotes/ltp", query, cancellationToken), code);
        }
        catch (CallFailed)
        {
        }

        double Of(string a, string? b = null) => full is { Count: > 0 } ? Clean(b is null ? full[a] : First(full, a, b)) : 0;
        JsonArray Pad() => new(Enumerable.Range(0, 5).Select(_ => (JsonNode?)new JsonObject { ["price"] = 0, ["quantity"] = 0 }).ToArray());

        if (depthData is not { Count: > 0 })
        {
            var ltp = full is { Count: > 0 } ? Of("live_price", "ltp") : lastPrice is not null && lastPrice.ContainsKey("live_price") ? Clean(lastPrice["live_price"]) : 0;
            return new JsonObject
            {
                ["bids"] = Pad(), ["asks"] = Pad(), ["ltp"] = ltp, ["ltq"] = 0, ["volume"] = Of("volume"), ["open"] = Of("day_open"), ["high"] = Of("day_high"), ["low"] = Of("day_low"), ["prev_close"] = Of("prev_close", "close"),
                ["oi"] = Of("oi", "open_interest"), ["totalbuyqty"] = 0, ["totalsellqty"] = 0,
            };
        }

        var market = MarketDepth(depthData["market_depth"], code);
        var levels = market["depth"] as JsonArray ?? [];
        var aggregate = market["aggregate"] as JsonObject ?? [];
        var bids = new JsonArray();
        var asks = new JsonArray();
        double sumBid = 0, sumAsk = 0;
        for (var i = 0; i < 5; i++)
        {
            if (i < levels.Count && levels[i] is JsonObject level)
            {
                var buy = level["buy"] as JsonObject ?? [];
                var sell = level["sell"] as JsonObject ?? [];
                var bq = Clean(buy["quantity"]);
                var sq = Clean(sell["quantity"]);
                bids.Add(new JsonObject { ["price"] = Clean(buy["price"]), ["quantity"] = bq });
                asks.Add(new JsonObject { ["price"] = Clean(sell["price"]), ["quantity"] = sq });
                sumBid += bq;
                sumAsk += sq;
            }
            else
            {
                bids.Add(new JsonObject { ["price"] = 0, ["quantity"] = 0 });
                asks.Add(new JsonObject { ["price"] = 0, ["quantity"] = 0 });
            }
        }

        var totalBuy = aggregate.ContainsKey("total_buy") ? aggregate["total_buy"] : JsonValue.Create("0");
        var totalSell = aggregate.ContainsKey("total_sell") ? aggregate["total_sell"] : JsonValue.Create("0");
        double lastTraded = full is { Count: > 0 } ? Of("live_price", "ltp") : lastPrice is not null && lastPrice.ContainsKey("live_price") ? Clean(lastPrice["live_price"]) : bids[0]!["price"]!.GetValue<double>() > 0 ? bids[0]!["price"]!.GetValue<double>() : 0;
        return new JsonObject
        {
            ["bids"] = bids, ["asks"] = asks, ["ltp"] = lastTraded, ["ltq"] = 0, ["volume"] = Of("volume"), ["open"] = Of("day_open"), ["high"] = Of("day_high"), ["low"] = Of("day_low"), ["prev_close"] = Of("prev_close", "close"), ["oi"] = Of("oi", "open_interest"),
            ["totalbuyqty"] = Truthy(totalBuy) ? Clean(totalBuy) : sumBid, ["totalsellqty"] = Truthy(totalSell) ? Clean(totalSell) : sumAsk,
        };
    }

    private bool IsBad(string code)
    {
        lock (BadGate)
        {
            if (!BadScrips.TryGetValue(code, out var at)) return false;
            if (TimeSpan.FromSeconds((double)(System.Diagnostics.Stopwatch.GetTimestamp() - at) / System.Diagnostics.Stopwatch.Frequency) > BadScripTtl)
            {
                BadScrips.Remove(code);
                return false;
            }

            return true;
        }
    }

    private static void MarkBad(string code)
    {
        lock (BadGate) BadScrips[code] = System.Diagnostics.Stopwatch.GetTimestamp();
    }

    /// <summary>The full quotes of a list of scrips, split in halves where IndMoney refuses the batch for an unknown scrip, so the good ones still come back.</summary>
    private async Task<Dictionary<string, JsonObject>> FullQuotesAsync(BrokerSession session, IReadOnlyList<string> codes, CancellationToken cancellationToken)
    {
        var result = new Dictionary<string, JsonObject>();
        var live = codes.Where(c => !IsBad(c)).ToList();
        if (live.Count == 0) return result;
        try
        {
            var response = await DataCallAsync(session, "/market/quotes/full", [new("scrip-codes", string.Join(',', live))], cancellationToken);
            foreach (var (key, value) in response["data"] as JsonObject ?? [])
                if (value is JsonObject quote) result[key] = quote;
            return result;
        }
        catch (CallFailed ex)
        {
            var bad = ex.Message.Contains("Invalid scrip", StringComparison.Ordinal);
            if (live.Count == 1)
            {
                if (bad) MarkBad(live[0]);
                return result;
            }

            if (!bad) return result;
            var mid = live.Count / 2;
            foreach (var (key, value) in await FullQuotesAsync(session, live.Take(mid).ToList(), cancellationToken)) result[key] = value;
            foreach (var (key, value) in await FullQuotesAsync(session, live.Skip(mid).ToList(), cancellationToken)) result[key] = value;
            return result;
        }
    }

    public async Task<JsonArray> GetMultiQuotesAsync(BrokerSession session, IReadOnlyList<SymbolRecord> instruments, CancellationToken cancellationToken)
    {
        var all = new JsonArray();
        for (var start = 0; start < instruments.Count; start += QuoteBatch)
        {
            var batch = instruments.Skip(start).Take(QuoteBatch).ToList();
            var byCode = new OrderedDictionary<string, SymbolRecord>();
            foreach (var record in batch) byCode[ScripCode(record, record.Exchange)] = record;
            var quotes = await FullQuotesAsync(session, byCode.Keys.ToList(), cancellationToken);
            foreach (var (code, record) in byCode)
            {
                if (quotes.TryGetValue(code, out var quote) && quote.Count > 0 && new[] { "ltp", "live_price", "day_open", "day_high", "day_low" }.Any(quote.ContainsKey))
                {
                    all.Add(new JsonObject
                    {
                        ["symbol"] = record.Symbol, ["exchange"] = record.Exchange,
                        ["data"] = new JsonObject
                        {
                            ["bid"] = 0, ["ask"] = 0, ["open"] = Clean(quote["day_open"]), ["high"] = Clean(quote["day_high"]), ["low"] = Clean(quote["day_low"]), ["ltp"] = Clean(First(quote, "live_price", "ltp")),
                            ["prev_close"] = Clean(First(quote, "prev_close", "close")), ["volume"] = Clean(quote["volume"]), ["oi"] = Clean(First(quote, "oi", "open_interest")),
                        },
                    });
                }
                else
                {
                    all.Add(new JsonObject { ["symbol"] = record.Symbol, ["exchange"] = record.Exchange, ["error"] = "No data received" });
                }
            }
        }

        return all;
    }

    // ---- history ---------------------------------------------------------------------------------------------

    private static readonly Dictionary<string, string> Intervals = new()
    {
        ["1m"] = "1minute", ["2m"] = "2minute", ["3m"] = "3minute", ["4m"] = "4minute", ["5m"] = "5minute", ["10m"] = "10minute", ["15m"] = "15minute", ["30m"] = "30minute", ["1h"] = "60minute", ["2h"] = "120minute", ["3h"] = "180minute",
        ["4h"] = "240minute", ["D"] = "1day", ["W"] = "1week", ["M"] = "1month",
    };

    public IReadOnlyList<string> HistoryTimeframes { get; } = ["1m", "2m", "3m", "4m", "5m", "10m", "15m", "30m", "1h", "2h", "3h", "4h", "D", "W", "M"];

    private static long Millis(DateOnly date, bool endOfDay)
    {
        var local = date.ToDateTime(endOfDay ? new TimeOnly(23, 59, 59) : TimeOnly.MinValue);
        return new DateTimeOffset(local, TimeZoneInfo.Local.GetUtcOffset(local)).ToUnixTimeMilliseconds();
    }

    public async Task<JsonArray> GetHistoryAsync(BrokerSession session, SymbolRecord instrument, string interval, DateOnly start, DateOnly end, CancellationToken cancellationToken)
    {
        if (!Intervals.TryGetValue(interval, out var api)) throw new BrokerException($"Interval {interval} is not supported by {Display}. Use one of: {string.Join(", ", HistoryTimeframes)}.", 400);
        var code = ScripCode(instrument, instrument.Exchange);
        var days = api.EndsWith("minute", StringComparison.Ordinal) ? (api == "60minute" || api is "120minute" or "180minute" or "240minute" ? 14 : 7) : 365;
        var candles = new List<(long Time, double Open, double High, double Low, double Close, long Volume)>();
        var asked = 0;
        var failed = 0;
        for (var current = start; current < end;)
        {
            var chunkEnd = current.AddDays(days - 1) < end ? current.AddDays(days - 1) : end;
            var query = new KeyValuePair<string, string>[]
            {
                new("scrip-codes", code), new("start_time", Millis(current, false).ToString(CultureInfo.InvariantCulture)), new("end_time", Millis(chunkEnd, true).ToString(CultureInfo.InvariantCulture)),
            };
            current = chunkEnd.AddDays(1);
            asked++;
            JsonObject response;
            try
            {
                response = await DataCallAsync(session, $"/market/historical/{api}", query, cancellationToken);
            }
            catch (CallFailed)
            {
                failed++;
                continue;
            }

            var data = response["data"];
            JsonArray? rows = null;
            if (data is JsonObject d && d[code] is JsonObject scrip && scrip.ContainsKey("candles")) rows = scrip["candles"] as JsonArray ?? [];
            else if (data is JsonObject d2 && d2.ContainsKey("candles")) rows = d2["candles"] as JsonArray ?? [];
            else if (data is JsonArray list) rows = list;
            foreach (var row in rows ?? [])
            {
                if (row is JsonObject o && o.ContainsKey("ts"))
                {
                    if (TryWhole(o["ts"], out var ts) && TryNumber(o["o"], out var open, true) && TryNumber(o["h"], out var high, true) && TryNumber(o["l"], out var low, true) && TryNumber(o["c"], out var close, true) && TryWhole(o["v"], out var volume, true))
                        candles.Add((ts, open, high, low, close, volume));
                }
                else if (row is JsonArray cells && cells.Count >= 6 && TryNumber(cells[0], out var ms) && TryNumber(cells[1], out var o1) && TryNumber(cells[2], out var h1) && TryNumber(cells[3], out var l1) && TryNumber(cells[4], out var c1))
                {
                    var volume = Truthy(cells[5]) ? (TryWhole(cells[5], out var v) ? v : -1) : 0;
                    if (volume >= 0) candles.Add(((long)(ms / 1000), o1, h1, l1, c1, volume));
                }
            }
        }

        if (candles.Count == 0 && asked > 0 && failed == asked) throw new BrokerException($"{Display} could not give candles for {instrument.Exchange}:{instrument.Symbol} right now. Try again in a moment.", 500);
        var result = new JsonArray();
        var seen = new HashSet<long>();
        foreach (var c in candles.Select((c, i) => (c, i)).OrderBy(x => x.c.Time).ThenBy(x => x.i).Select(x => x.c))
        {
            if (!seen.Add(c.Time)) continue;
            result.Add(new JsonObject { ["timestamp"] = c.Time, ["open"] = c.Open, ["high"] = c.High, ["low"] = c.Low, ["close"] = c.Close, ["volume"] = c.Volume, ["oi"] = 0 });
        }

        return result;
    }

    private static bool TryNumber(JsonNode? node, out double value, bool nullIsZero = false)
    {
        value = 0;
        if (node is null) return nullIsZero && false;
        if (BookFormatting.Number(node) is { } n) { value = n; return true; }
        return Str(node) is { } s && double.TryParse(s.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out value);
    }

    /// <summary>int(x): a number is cut, text has to be a whole number; an absent field is zero where OpenAlgo reads it with a default of zero.</summary>
    private static bool TryWhole(JsonNode? node, out long value, bool absentIsZero = false)
    {
        value = 0;
        if (node is null) return absentIsZero;
        if (node is JsonValue v && v.TryGetValue<long>(out value)) return true;
        if (BookFormatting.Number(node) is { } n && double.IsFinite(n)) { value = (long)Math.Truncate(n); return true; }
        return Str(node) is { } s && long.TryParse(s.Trim(), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out value);
    }
}
