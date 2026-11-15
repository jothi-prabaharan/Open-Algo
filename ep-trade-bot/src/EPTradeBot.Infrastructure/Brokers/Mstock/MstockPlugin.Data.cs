using System.Globalization;
using System.Text.Json.Nodes;
using EPTradeBot.Core.Brokers;
using static EPTradeBot.Infrastructure.Brokers.JsonRead;

namespace EPTradeBot.Infrastructure.Brokers.Mstock;

/// <summary>
/// mStock quotes and history (broker/mstock api/data.py). A quote is a GET with a JSON body (mode OHLC and the tokens grouped by exchange); the answer carries last price and OHLC only, so bid, ask, volume and open interest are zero. Several
/// quotes go five hundred to a batch, a second apart. History is a GET with a JSON body too, in chunks of days by interval a second apart, and today's candles come from a separate intraday call.
/// </summary>
public sealed partial class MstockPlugin : IBrokerMultiQuotes, IBrokerHistory, IBrokerMargin
{
    private const int QuoteBatch = 500;
    private static readonly Dictionary<string, string> QuoteExchanges = new() { ["NSE"] = "NSE", ["BSE"] = "BSE", ["NFO"] = "NFO", ["BFO"] = "BFO", ["CDS"] = "CDS", ["MCX"] = "MCX", ["NSE_INDEX"] = "NSE", ["BSE_INDEX"] = "BSE", ["MCX_INDEX"] = "MCX" };

    private TimeSpan Pause() => sessions.HistoryGap(Name, TimeSpan.FromSeconds(1));

    private static JsonObject FormatQuote(JsonObject quote) => new()
    {
        ["bid"] = 0, ["ask"] = 0, ["open"] = Flt(quote["open"]), ["high"] = Flt(quote["high"]), ["low"] = Flt(quote["low"]), ["ltp"] = Flt(quote["ltp"]), ["prev_close"] = Flt(quote["close"]),
        ["volume"] = Truthy(quote["volume"]) ? WholeNumber(quote["volume"]) : 0, ["oi"] = 0,
    };

    /// <summary>float(x.get(name, 0)): absent is zero, anything present has to read as a number.</summary>
    private static double Flt(JsonNode? node)
    {
        if (node is null) return 0;
        return Number(node) ?? throw new BrokerException($"{Display} sent a quote with a figure that is not a number.", 500);
    }

    private static string Reason(JsonNode? body) => Message(body) ?? "no reason was given.";

    // ---- quotes ----------------------------------------------------------------------------------------------

    public async Task<JsonObject> GetQuoteAsync(BrokerSession session, string symbol, string exchange, CancellationToken cancellationToken)
    {
        var record = await symbols.FindAsync(Name, symbol, exchange, cancellationToken)
            ?? throw new BrokerException($"{symbol} on {exchange} is not in the {Display} master contract. Check the symbol, or download the master contract again.", 400);
        if (!QuoteExchanges.TryGetValue(exchange, out var api)) throw new BrokerException($"{Display} does not give quotes for the {exchange} exchange.", 400);
        var body = new JsonObject { ["mode"] = "OHLC", ["exchangeTokens"] = new JsonObject { [api] = new JsonArray(record.Token) } };
        var (status, reply) = await SendAsync(session, HttpMethod.Get, "/instruments/quote", body, cancellationToken);
        if (status >= 400 || reply is not JsonObject answer || !Truthy(answer["status"])) throw new BrokerException($"{Display} could not give the quote: {Reason(reply)}", 500);
        var fetched = (answer["data"] as JsonObject)?["fetched"] as JsonArray;
        if (fetched is null || fetched.Count == 0 || fetched[0] is not JsonObject first) throw new BrokerException($"{Display} sent no quote for {symbol} on {exchange}. Try again in a moment.", 500);
        return FormatQuote(first);
    }

    public Task<JsonObject> GetDepthAsync(BrokerSession session, string symbol, string exchange, CancellationToken cancellationToken) =>
        throw new BrokerException($"{Display} market depth is not available yet.", 501);

    public async Task<JsonArray> GetMultiQuotesAsync(BrokerSession session, IReadOnlyList<SymbolRecord> instruments, CancellationToken cancellationToken)
    {
        var all = new JsonArray();
        for (var start = 0; start < instruments.Count; start += QuoteBatch)
        {
            if (start > 0) await Task.Delay(Pause(), cancellationToken);
            foreach (var item in await BatchAsync(session, instruments.Skip(start).Take(QuoteBatch).ToList(), cancellationToken)) all.Add(item);
        }

        return all;
    }

    private async Task<List<JsonNode>> BatchAsync(BrokerSession session, List<SymbolRecord> batch, CancellationToken cancellationToken)
    {
        var skipped = new List<JsonNode>();
        var tokens = new OrderedDictionary<string, JsonArray>();
        var names = new OrderedDictionary<string, (string Symbol, string Exchange)>();
        foreach (var r in batch)
        {
            if (!QuoteExchanges.TryGetValue(r.Exchange, out var api))
            {
                skipped.Add(new JsonObject { ["symbol"] = r.Symbol, ["exchange"] = r.Exchange, ["data"] = null, ["error"] = $"Exchange '{r.Exchange}' not supported" });
                continue;
            }

            if (!tokens.TryGetValue(api, out var list)) tokens[api] = list = [];
            list.Add(r.Token);
            names[r.Token] = (r.Symbol, r.Exchange);
        }

        if (names.Count == 0) return skipped;
        var results = new List<JsonNode>();
        try
        {
            var body = new JsonObject { ["mode"] = "OHLC", ["exchangeTokens"] = new JsonObject(tokens.Select(t => KeyValuePair.Create(t.Key, (JsonNode?)t.Value))) };
            var (status, reply) = await SendAsync(session, HttpMethod.Get, "/instruments/quote", body, cancellationToken);
            if (status >= 400 || reply is not JsonObject answer || !Truthy(answer["status"])) throw new BrokerException($"{Display} could not give the quotes: {Reason(reply)}", 500);
            foreach (var quote in ((answer["data"] as JsonObject)?["fetched"] as JsonArray)?.OfType<JsonObject>() ?? [])
            {
                var token = Str(quote["symbolToken"]) ?? "";
                if (!names.Remove(token, out var name)) continue;
                results.Add(new JsonObject { ["symbol"] = name.Symbol, ["exchange"] = name.Exchange, ["data"] = FormatQuote(quote) });
            }

            foreach (var (_, name) in names) results.Add(new JsonObject { ["symbol"] = name.Symbol, ["exchange"] = name.Exchange, ["error"] = "No data received" });
        }
        catch (BrokerException ex)
        {
            foreach (var (_, name) in names) results.Add(new JsonObject { ["symbol"] = name.Symbol, ["exchange"] = name.Exchange, ["error"] = ex.Message });
        }

        return [.. skipped, .. results];
    }

    // ---- margin ----------------------------------------------------------------------------------------------

    public async Task<JsonObject> CalculateMarginAsync(BrokerSession session, IReadOnlyList<MarginLeg> legs, CancellationToken cancellationToken)
    {
        var orders = new JsonArray();
        foreach (var leg in legs)
        {
            var token = leg.Instrument.Token.Trim();
            if (token.Length == 0 || !token.Replace(".", "", StringComparison.Ordinal).Replace("-", "", StringComparison.Ordinal).All(char.IsAsciiDigit)) continue;
            orders.Add(new JsonObject
            {
                ["product_type"] = Product(leg.Product), ["transaction_type"] = leg.Action.ToUpperInvariant(), ["quantity"] = leg.Quantity.ToString(CultureInfo.InvariantCulture), ["price"] = Text(leg.Price),
                ["exchange"] = leg.Instrument.Exchange, ["symbol_name"] = leg.Instrument.BrokerSymbol, ["token"] = token, ["trigger_price"] = (double)leg.TriggerPrice,
            });
        }

        if (orders.Count == 0) throw new BrokerException("None of the positions could be priced for margin. Check that the symbols are valid.", 400);
        var (_, reply) = await SendAsync(session, HttpMethod.Post, "/margins/orders", new JsonObject { ["orders"] = orders }, cancellationToken);
        if (reply is not JsonObject answer) throw new BrokerException($"{Display} sent an answer that could not be read. Try again in a moment.", 502);
        if (!Success(answer)) throw new BrokerException($"{Display} could not work out the margin: {Message(answer) ?? "no reason was given."}", 400);
        var summary = (answer["data"] as JsonObject)?["summary"] as JsonObject;
        JsonNode? Amount(string name) => (summary?["breakup"] as JsonArray)?.OfType<JsonObject>().LastOrDefault(i => Str(i["name"]) == name)?["amount"]?.DeepClone() ?? 0;
        return new JsonObject { ["total_margin_required"] = summary?["total_charges"]?.DeepClone() ?? 0, ["span_margin"] = Amount("SPANMARGIN"), ["exposure_margin"] = Amount("EXPOMARGIN") };
    }

    // ---- history ---------------------------------------------------------------------------------------------

    public IReadOnlyList<string> HistoryTimeframes { get; } = ["1m", "3m", "5m", "10m", "15m", "30m", "1h", "D"];

    private static readonly Dictionary<string, string> Intervals = new() { ["1m"] = "ONE_MINUTE", ["3m"] = "THREE_MINUTE", ["5m"] = "FIVE_MINUTE", ["10m"] = "TEN_MINUTE", ["15m"] = "FIFTEEN_MINUTE", ["30m"] = "THIRTY_MINUTE", ["1h"] = "ONE_HOUR", ["D"] = "ONE_DAY" };
    private static readonly Dictionary<string, int> ChunkDays = new() { ["1m"] = 2, ["3m"] = 8, ["5m"] = 13, ["10m"] = 26, ["15m"] = 40, ["30m"] = 76, ["1h"] = 166, ["D"] = 1000 };
    private static readonly Dictionary<string, string> IntradayCodes = new() { ["NSE"] = "1", ["BSE"] = "4", ["NFO"] = "2", ["BFO"] = "5", ["CDS"] = "3", ["MCX"] = "6", ["NSE_INDEX"] = "1", ["BSE_INDEX"] = "4", ["MCX_INDEX"] = "6" };
    private static readonly TimeSpan Ist = TimeSpan.FromHours(5.5);

    private sealed record Candle(long Stamp, double Open, double High, double Low, double Close, double Volume);

    /// <summary>The candles of an answer's data.candles, [time, open, high, low, close, volume] each, or null when they cannot be read.</summary>
    private static List<Candle>? ReadCandles(JsonNode? list, bool daily, bool zoned)
    {
        if (list is not JsonArray rows) return [];
        var candles = new List<Candle>();
        foreach (var row in rows)
        {
            if (row is not JsonArray cells || cells.Count < 6) return null;
            if (Str(cells[0]) is not { } text) return null;
            long stamp;
            try
            {
                stamp = EpochOf(text, daily);
            }
            catch (FormatException)
            {
                return null;
            }

            var numbers = new double[5];
            for (var i = 0; i < 5; i++)
            {
                if (Number(cells[i + 1]) is not { } n) return null;
                numbers[i] = n;
            }

            candles.Add(new Candle(stamp, numbers[0], numbers[1], numbers[2], numbers[3], numbers[4]));
        }

        _ = zoned;
        return candles;
    }

    /// <summary>mStock's time as epoch seconds: one with a zone is taken to UTC (and, for a day, to the UTC midnight of that), one without is India time.</summary>
    private static long EpochOf(string text, bool daily)
    {
        var match = System.Text.RegularExpressions.Regex.Match(text.Trim(), @"^(.*?)(Z|[+-]\d{2}(?::?\d{2})?)$");
        DateTimeOffset utc;
        if (match.Success && match.Groups[1].Value.Contains(':', StringComparison.Ordinal))
        {
            var offset = match.Groups[2].Value;
            if (offset != "Z" && offset.Length == 3) offset += ":00";
            else if (offset != "Z" && offset.Length == 5) offset = offset[..3] + ":" + offset[3..];
            if (!DateTimeOffset.TryParse(match.Groups[1].Value.Replace("T", " ", StringComparison.Ordinal) + (offset == "Z" ? "+00:00" : offset), CultureInfo.InvariantCulture, DateTimeStyles.None, out utc)) throw new FormatException("Not a time.");
            utc = utc.ToUniversalTime();
        }
        else
        {
            if (!DateTime.TryParse(text.Trim(), CultureInfo.InvariantCulture, DateTimeStyles.None, out var local)) throw new FormatException("Not a time.");
            utc = new DateTimeOffset(local, Ist).ToUniversalTime();
        }

        var instant = daily ? new DateTimeOffset(utc.Year, utc.Month, utc.Day, 0, 0, 0, TimeSpan.Zero) : utc;
        return instant.ToUnixTimeSeconds();
    }

    public async Task<JsonArray> GetHistoryAsync(BrokerSession session, SymbolRecord instrument, string interval, DateOnly start, DateOnly end, CancellationToken cancellationToken)
    {
        if (!Intervals.ContainsKey(interval)) throw new BrokerException($"Interval {interval} is not supported by {Display}. Use one of: {string.Join(", ", HistoryTimeframes)}.", 400);
        var today = DateOnly.FromDateTime(clock.GetUtcNow().ToOffset(Ist).DateTime);
        var daily = interval == "D";
        List<Candle> candles;
        if (start == end && start == today)
        {
            candles = await IntradayAsync(session, instrument, interval, cancellationToken);
        }
        else if (end == today && start < today)
        {
            candles = await HistoricalAsync(session, instrument, interval, start.ToDateTime(TimeOnly.MinValue), today.AddDays(-1).ToDateTime(TimeOnly.MinValue), cancellationToken);
            try
            {
                var intraday = await IntradayAsync(session, instrument, interval, cancellationToken);
                candles = [.. candles, .. intraday];
            }
            catch (BrokerException)
            {
                // A failed intraday read leaves the earlier days.
            }
        }
        else
        {
            candles = await HistoricalAsync(session, instrument, interval, start.ToDateTime(TimeOnly.MinValue), end.ToDateTime(TimeOnly.MinValue), cancellationToken);
        }

        _ = daily;
        var result = new JsonArray();
        foreach (var c in candles.Select((c, i) => (c, i)).OrderBy(x => x.c.Stamp).ThenBy(x => x.i).Select(x => x.c).GroupBy(c => c.Stamp).Select(g => g.First()))
            result.Add(new JsonObject { ["timestamp"] = c.Stamp, ["open"] = c.Open, ["high"] = c.High, ["low"] = c.Low, ["close"] = c.Close, ["volume"] = c.Volume, ["oi"] = 0 });
        return result;
    }

    private async Task<List<Candle>> HistoricalAsync(BrokerSession session, SymbolRecord instrument, string interval, DateTime from, DateTime to, CancellationToken cancellationToken)
    {
        var exchange = QuoteExchanges.GetValueOrDefault(instrument.Exchange, instrument.Exchange);
        if (to.Hour == 0 && to.Minute == 0) to = to.Date.AddHours(23).AddMinutes(59);
        var daily = interval == "D";
        var all = new List<Candle>();
        var first = true;
        for (var current = from; current <= to;)
        {
            if (!first) await Task.Delay(Pause(), cancellationToken);
            first = false;
            var currentEnd = current.AddDays(ChunkDays[interval] - 1) < to ? current.AddDays(ChunkDays[interval] - 1) : to;
            var body = new JsonObject
            {
                ["exchange"] = exchange, ["symboltoken"] = instrument.Token, ["interval"] = Intervals[interval], ["fromdate"] = current.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture),
                ["todate"] = currentEnd.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture),
            };
            current = currentEnd.AddDays(1);
            JsonNode? reply;
            int status;
            try
            {
                (status, reply) = await SendAsync(session, HttpMethod.Get, "/instruments/historical", body, cancellationToken);
            }
            catch (BrokerException)
            {
                continue;
            }

            if (status >= 400 || reply is not JsonObject answer || !Truthy(answer["status"])) continue;
            var read = ReadCandles((answer["data"] as JsonObject)?["candles"], daily, true);
            if (read is null) throw new BrokerException($"{Display} sent candles that could not be read. Try again in a moment.", 500);
            all.AddRange(read);
        }

        return all;
    }

    private async Task<List<Candle>> IntradayAsync(BrokerSession session, SymbolRecord instrument, string interval, CancellationToken cancellationToken)
    {
        if (!IntradayCodes.TryGetValue(instrument.Exchange, out var code)) throw new BrokerException($"{Display} does not give today's candles for the {instrument.Exchange} exchange.", 400);
        var body = new JsonObject { ["exchange"] = code, ["symboltoken"] = instrument.Token, ["interval"] = Intervals[interval] };
        var (status, reply) = await SendAsync(session, HttpMethod.Post, "/instruments/intraday", body, cancellationToken);
        if (status >= 400 || reply is not JsonObject answer || !Truthy(answer["status"])) throw new BrokerException($"{Display} could not give today's candles for {instrument.Exchange}:{instrument.Symbol}. Try again in a moment.", 500);
        return ReadCandles((answer["data"] as JsonObject)?["candles"], false, false) ?? throw new BrokerException($"{Display} sent candles that could not be read. Try again in a moment.", 500);
    }
}
