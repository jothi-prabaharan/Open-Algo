using System.Globalization;
using System.Text.Json.Nodes;
using EPTradeBot.Core.Brokers;
using static EPTradeBot.Infrastructure.Brokers.JsonRead;

namespace EPTradeBot.Infrastructure.Brokers.Sahi;

/// <summary>
/// Sahi market data (broker/sahi/api/data.py). Instruments are named by Sahi's instrument id, the token of the master contract. A quote carries no open, high, low or previous close,
/// so they are read from the daily candles; a batch of quotes for option contracts comes from the option chain, which does carry the previous close. Candles come newest page
/// first and are paged backwards while Sahi says there is more.
/// </summary>
public sealed partial class SahiPlugin
{
    private const int QuoteBatch = 100;
    private const int MaxCandlePages = 200;
    private static readonly TimeSpan IstOffset = TimeSpan.FromHours(5.5);

    public IReadOnlyList<string> HistoryTimeframes { get; } = ["1m", "3m", "5m", "15m", "30m", "1h", "D"];

    private static readonly Dictionary<string, string> CandleSizes = new() { ["1m"] = "1m", ["3m"] = "3m", ["5m"] = "5m", ["15m"] = "15m", ["30m"] = "30m", ["1h"] = "1h", ["D"] = "1d" };

    /// <summary>One market-data call; anything but a 200 is a refusal a trader can act on.</summary>
    private async Task<JsonObject> DataAsync(BrokerSession session, string path, string query, CancellationToken cancellationToken)
    {
        (int Status, JsonObject Body) reply;
        try
        {
            reply = await CallAsync(session, HttpMethod.Get, path + "?" + query, null, cancellationToken);
        }
        catch (HttpRequestException ex)
        {
            throw new BrokerException("Could not reach Sahi. Check your connection.", 500, ex);
        }

        if (reply.Status == 200) return reply.Body;
        throw new BrokerException(reply.Status switch
        {
            401 => "Your Sahi session has expired. Log in to EP Trade bot again.",
            403 => "Your Sahi API key does not include market data. Create a key with market data access.",
            _ => Str(reply.Body["message"]) is { Length: > 0 } message ? message : "Sahi could not return market data right now. Try again in a moment.",
        }, 500);
    }

    private static string Ids(IEnumerable<long> ids) => string.Join("&", ids.Select(i => "i=" + i.ToString(CultureInfo.InvariantCulture)));

    private async Task<long> InstrumentIdAsync(string symbol, string exchange, CancellationToken cancellationToken)
    {
        var known = await symbols.FindAsync(Name, symbol, exchange, cancellationToken);
        if (known is null || !long.TryParse(known.Token, out var id)) throw new BrokerException($"{symbol} was not found on {exchange} in the Sahi master contract.", 400);
        return id;
    }

    private async Task<Dictionary<long, JsonObject>> QuotesAsync(BrokerSession session, IReadOnlyList<long> ids, CancellationToken cancellationToken)
    {
        var quotes = new Dictionary<long, JsonObject>();
        for (var i = 0; i < ids.Count; i += QuoteBatch)
        {
            var body = await DataAsync(session, "/v1/marketdata/quotes", Ids(ids.Skip(i).Take(QuoteBatch)), cancellationToken);
            foreach (var (key, value) in (body["data"] as JsonObject) ?? [])
            {
                if (value is JsonObject quote && long.TryParse(key, out var id)) quotes[id] = quote;
            }
        }

        return quotes;
    }

    private DateOnly IstToday() => DateOnly.FromDateTime(clock.GetUtcNow().ToOffset(IstOffset).DateTime);

    private long NowMs() => clock.GetUtcNow().ToUnixTimeMilliseconds();

    /// <summary>The day's open, high, low and previous close from the daily candles. Before the open (the last candle is not today's) the day's own figures are unknown and zero.</summary>
    private async Task<(double Open, double High, double Low, double PrevClose)> DailyAsync(BrokerSession session, long instrumentId, CancellationToken cancellationToken)
    {
        var now = NowMs();
        var query = $"instrument_id={instrumentId}&size=1d&from={now - (15L * 86400 * 1000)}&to={now}&include_partial=true";
        JsonArray candles;
        try
        {
            candles = (await DataAsync(session, "/v1/marketdata/candles", query, cancellationToken))["candles"] as JsonArray ?? [];
        }
        catch (BrokerException)
        {
            return (0, 0, 0, 0);
        }

        if (candles.Count == 0 || candles[^1] is not JsonArray last) return (0, 0, 0, 0);
        var lastDay = DateOnly.FromDateTime(DateTimeOffset.FromUnixTimeMilliseconds(Int(last[0])).ToOffset(IstOffset).DateTime);
        if (lastDay == IstToday())
            return (FromPaise(last[1]), FromPaise(last[2]), FromPaise(last[3]), candles.Count > 1 && candles[^2] is JsonArray before ? FromPaise(before[4]) : 0.0);
        return (0, 0, 0, FromPaise(last[4]));
    }

    private static JsonObject QuoteBody(JsonObject quote, (double Open, double High, double Low, double PrevClose) ohlc)
    {
        var buys = quote["best_buys"] as JsonArray ?? [];
        var sells = quote["best_sells"] as JsonArray ?? [];
        return new JsonObject
        {
            ["ask"] = sells.Count > 0 ? FromPaise(sells[0]?["price"]) : 0, ["bid"] = buys.Count > 0 ? FromPaise(buys[0]?["price"]) : 0, ["high"] = ohlc.High, ["low"] = ohlc.Low,
            ["ltp"] = FromPaise(quote["last_trade_price"]), ["open"] = ohlc.Open, ["prev_close"] = ohlc.PrevClose, ["volume"] = Int(quote["volume_today"]), ["oi"] = Int(quote["open_interest"]),
        };
    }

    public async Task<JsonObject> GetQuoteAsync(BrokerSession session, string symbol, string exchange, CancellationToken cancellationToken)
    {
        var id = await InstrumentIdAsync(symbol, exchange, cancellationToken);
        var quotes = await QuotesAsync(session, [id], cancellationToken);
        if (!quotes.TryGetValue(id, out var quote) || quote.Count == 0) throw new BrokerException($"Sahi returned no quote for {symbol} on {exchange}.", 500);
        return QuoteBody(quote, await DailyAsync(session, id, cancellationToken));
    }

    private static JsonArray Levels(JsonArray? levels)
    {
        var result = new JsonArray();
        foreach (var level in (levels ?? []).Take(5)) result.Add(new JsonObject { ["price"] = FromPaise(level?["price"]), ["quantity"] = Int(level?["qty"]) });
        while (result.Count < 5) result.Add(new JsonObject { ["price"] = 0, ["quantity"] = 0 });
        return result;
    }

    public async Task<JsonObject> GetDepthAsync(BrokerSession session, string symbol, string exchange, CancellationToken cancellationToken)
    {
        var id = await InstrumentIdAsync(symbol, exchange, cancellationToken);
        var quotes = await QuotesAsync(session, [id], cancellationToken);
        if (!quotes.TryGetValue(id, out var quote) || quote.Count == 0) throw new BrokerException($"Sahi returned no depth for {symbol} on {exchange}.", 500);
        var (open, high, low, prevClose) = await DailyAsync(session, id, cancellationToken);
        var buys = quote["best_buys"] as JsonArray ?? [];
        var sells = quote["best_sells"] as JsonArray ?? [];
        return new JsonObject
        {
            ["asks"] = Levels(sells), ["bids"] = Levels(buys), ["high"] = high, ["low"] = low, ["ltp"] = FromPaise(quote["last_trade_price"]), ["ltq"] = 0, ["oi"] = Int(quote["open_interest"]),
            ["open"] = open, ["prev_close"] = prevClose, ["totalbuyqty"] = buys.Sum(l => Int(l?["qty"])), ["totalsellqty"] = sells.Sum(l => Int(l?["qty"])), ["volume"] = Int(quote["volume_today"]),
        };
    }

    // ---- several quotes --------------------------------------------------------------------------------------

    /// <summary>Quotes for option contracts from the option chain of their underlying and expiry. A chain that cannot be read leaves its contracts to the quotes call.</summary>
    private async Task<Dictionary<long, JsonObject>> OptionChainQuotesAsync(BrokerSession session, IReadOnlyList<(long Id, SymbolRecord Info)> options, CancellationToken cancellationToken)
    {
        var groups = new Dictionary<(long Underlying, string Expiry), HashSet<long>>();
        foreach (var (id, info) in options)
        {
            var index = info.Exchange == "NFO" ? "NSE_INDEX" : "BSE_INDEX";
            var cash = info.Exchange == "NFO" ? "NSE" : "BSE";
            var underlying = (info.Name is null ? null : await symbols.FindAsync(Name, info.Name, index, cancellationToken)) ?? (info.Name is null ? null : await symbols.FindAsync(Name, info.Name, cash, cancellationToken));
            if (underlying is null || !long.TryParse(underlying.Token, out var underlyingId) || string.IsNullOrEmpty(info.Expiry)) continue;
            if (!DateTime.TryParseExact(info.Expiry, "dd-MMM-yy", CultureInfo.InvariantCulture, DateTimeStyles.None, out var expiry)) continue;
            var key = (underlyingId, expiry.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
            if (!groups.TryGetValue(key, out var wanted)) groups[key] = wanted = [];
            wanted.Add(id);
        }

        var result = new Dictionary<long, JsonObject>();
        foreach (var ((underlyingId, expiry), wanted) in groups)
        {
            JsonObject chain;
            try
            {
                chain = await DataAsync(session, "/v1/marketdata/optionchain", $"underlying_id={underlyingId}&expiry={expiry}", cancellationToken);
            }
            catch (BrokerException)
            {
                continue;
            }

            foreach (var row in (chain["strikes"] as JsonArray ?? []).OfType<JsonObject>())
            {
                foreach (var leg in new[] { row["ce"] as JsonObject, row["pe"] as JsonObject })
                {
                    if (leg is null || leg.Count == 0 || !wanted.Contains(Int(leg["instrument_id"]))) continue;
                    result[Int(leg["instrument_id"])] = new JsonObject
                    {
                        ["ask"] = FromPaise(leg["top_ask_price"]), ["bid"] = FromPaise(leg["top_bid_price"]), ["high"] = 0.0, ["low"] = 0.0, ["ltp"] = FromPaise(leg["last_trade_price"]),
                        ["open"] = 0.0, ["prev_close"] = FromPaise(leg["prev_close_price"]), ["volume"] = Int(leg["volume_today"]), ["oi"] = Int(leg["open_interest"]),
                    };
                }
            }
        }

        return result;
    }

    public async Task<JsonArray> GetMultiQuotesAsync(BrokerSession session, IReadOnlyList<SymbolRecord> instruments, CancellationToken cancellationToken)
    {
        var resolved = instruments.Where(i => long.TryParse(i.Token, out _)).Select(i => (Id: long.Parse(i.Token, CultureInfo.InvariantCulture), Info: i)).ToList();
        var options = resolved.Where(r => r.Info.InstrumentType is "CE" or "PE").ToList();
        var quotes = options.Count > 0 ? await OptionChainQuotesAsync(session, options, cancellationToken) : [];

        var remaining = resolved.Select(r => r.Id).Where(id => !quotes.ContainsKey(id)).Distinct().Order().ToList();
        if (remaining.Count > 0)
        {
            foreach (var (id, raw) in await QuotesAsync(session, remaining, cancellationToken)) quotes[id] = QuoteBody(raw, (0, 0, 0, 0));
        }

        var results = new JsonArray();
        foreach (var (id, info) in resolved)
        {
            results.Add(quotes.TryGetValue(id, out var quote)
                ? new JsonObject { ["symbol"] = info.Symbol, ["exchange"] = info.Exchange, ["data"] = quote.DeepClone() }
                : new JsonObject { ["symbol"] = info.Symbol, ["exchange"] = info.Exchange, ["error"] = "No quote returned by Sahi" });
        }

        return results;
    }

    // ---- history ---------------------------------------------------------------------------------------------

    public async Task<JsonArray> GetHistoryAsync(BrokerSession session, SymbolRecord instrument, string interval, DateOnly start, DateOnly end, CancellationToken cancellationToken)
    {
        if (!CandleSizes.TryGetValue(interval, out var size))
            throw new BrokerException($"Interval {interval} is not supported by Sahi. Use one of: {string.Join(", ", CandleSizes.Keys)}.", 400);
        var id = await InstrumentIdAsync(instrument.Symbol, instrument.Exchange, cancellationToken);

        long Ms(DateOnly day, TimeOnly time) => new DateTimeOffset(day.ToDateTime(time), IstOffset).ToUnixTimeMilliseconds();
        var from = Ms(start, new TimeOnly(0, 0));
        var to = Math.Min(Ms(end, new TimeOnly(23, 59, 59)), NowMs());
        var wantOi = instrument.InstrumentType is "FUT" or "CE" or "PE";

        var rows = new List<JsonArray>();
        var pages = 0;
        var cursor = to;
        // Only the first (latest) page can hold the candle still forming.
        var includePartial = end >= IstToday();
        while (cursor >= from)
        {
            var query = $"instrument_id={id}&size={size}&from={from}&to={cursor}";
            if (includePartial)
            {
                query += "&include_partial=true";
                includePartial = false;
            }

            if (wantOi) query += "&oi=true";
            var body = await DataAsync(session, "/v1/marketdata/candles", query, cancellationToken);
            var candles = (body["candles"] as JsonArray ?? []).OfType<JsonArray>().ToList();
            rows.InsertRange(0, candles);
            pages++;
            if (!Truthy(body["has_more"]) || candles.Count == 0) break;
            if (pages >= MaxCandlePages) throw new BrokerException("The requested history is too long to fetch in one request. Ask for a shorter range.", 500);
            cursor = Int(candles[0][0]) - 1;
        }

        var bars = new SortedDictionary<long, JsonObject>();
        foreach (var r in rows)
        {
            var ms = Int(r[0]);
            // A daily candle is stamped at 00:00 UTC of its India date, the OpenAlgo convention.
            var timestamp = size == "1d" ? (long)Math.Floor((ms + 19800000) / 86400000.0) * 86400 : (long)Math.Floor(ms / 1000.0);
            bars[timestamp] = new JsonObject
            {
                ["timestamp"] = timestamp, ["open"] = Float(r[1]) / 100.0, ["high"] = Float(r[2]) / 100.0, ["low"] = Float(r[3]) / 100.0, ["close"] = Float(r[4]) / 100.0,
                ["volume"] = Int(r[5]), ["oi"] = r.Count > 6 ? Int(r[6]) : 0,
            };
        }

        return new JsonArray(bars.Values.Select(v => (JsonNode?)v).ToArray());
    }
}
