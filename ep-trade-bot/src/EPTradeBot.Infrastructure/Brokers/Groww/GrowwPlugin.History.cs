using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using EPTradeBot.Core.Brokers;
using static EPTradeBot.Infrastructure.Brokers.JsonRead;

namespace EPTradeBot.Infrastructure.Brokers.Groww;

/// <summary>
/// Groww history (broker/groww api/data.py get_history). Minute to 4-hour candles come from GET /v1/historical/candles (a groww symbol such as NSE-SBIN or NSE-NIFTY-25Nov26-FUT), cut into requests of the longest range Groww allows for the interval; daily and
/// weekly come from GET /v1/historical/candle/range (the trading symbol and the interval in minutes) and are stamped at midnight UTC of their IST date. 30-minute, 1-hour and 4-hour candles are built from 15-minute ones aligned to the 09:15 open; a candle wholly
/// in the pre-open session, or with no open, is left out. A missing volume is 0, and no price is ever filled in.
/// </summary>
public sealed partial class GrowwPlugin : IBrokerHistory
{
    private static readonly Dictionary<string, string> Intervals = new()
    {
        ["1m"] = "1minute", ["2m"] = "2minute", ["3m"] = "3minute", ["5m"] = "5minute", ["10m"] = "10minute", ["15m"] = "15minute", ["30m"] = "30minute", ["1h"] = "1hour", ["4h"] = "4hour", ["D"] = "1day", ["W"] = "1week",
    };

    private static readonly Dictionary<string, int> MaxDays = new()
    {
        ["1minute"] = 30, ["2minute"] = 30, ["3minute"] = 30, ["5minute"] = 30, ["10minute"] = 90, ["15minute"] = 90, ["30minute"] = 90, ["1hour"] = 180, ["4hour"] = 180, ["1440"] = 1080, ["10080"] = 3650,
    };

    private static readonly Dictionary<string, int> CandleMinutes = new()
    {
        ["1minute"] = 1, ["2minute"] = 2, ["3minute"] = 3, ["5minute"] = 5, ["10minute"] = 10, ["15minute"] = 15, ["30minute"] = 30, ["1hour"] = 60, ["4hour"] = 240,
    };

    private static readonly HashSet<string> Rebuilt = ["30minute", "1hour", "4hour"];

    private static readonly TimeSpan India = TimeSpan.FromHours(5.5);

    public IReadOnlyList<string> HistoryTimeframes { get; } = [.. Intervals.Keys];

    /// <summary>Groww's exchange, segment, groww symbol and trading symbol for an instrument of the master contract.</summary>
    private static (string Exchange, string Segment, string GrowwSymbol, string TradingSymbol) GrowwSymbolOf(SymbolRecord info)
    {
        var exchange = !string.IsNullOrEmpty(info.BrokerExchange) ? info.BrokerExchange : info.Exchange is "BSE" or "BFO" or "BSE_INDEX" ? "BSE" : "NSE";
        if (info.Exchange is not ("NFO" or "BFO")) return (exchange, "CASH", $"{exchange}-{info.BrokerSymbol}", info.BrokerSymbol);
        var match = Regex.Match(info.Symbol, @"^(.+?)(\d{2}[A-Z]{3}\d{2})(FUT|[\d.]+(CE|PE))$");
        if (!match.Success || string.IsNullOrEmpty(info.Expiry)) throw new BrokerException($"{info.Symbol} is not in OpenAlgo F&O format; download the master contract again", 500);
        var underlying = match.Groups[1].Value;
        if (!DateTime.TryParseExact(info.Expiry, "dd-MMM-yy", CultureInfo.InvariantCulture, DateTimeStyles.None, out var expiry)) throw new BrokerException($"{info.Symbol} is not in OpenAlgo F&O format; download the master contract again", 500);
        var day = expiry.ToString("ddMMMyy", CultureInfo.InvariantCulture);
        if (info.InstrumentType == "FUT") return (exchange, "FNO", $"{exchange}-{underlying}-{day}-FUT", info.BrokerSymbol);
        var strike = info.Strike ?? 0;
        var strikeText = strike == Math.Truncate(strike) ? ((long)strike).ToString(CultureInfo.InvariantCulture) : strike.ToString("R", CultureInfo.InvariantCulture);
        return (exchange, "FNO", $"{exchange}-{underlying}-{day}-{strikeText}-{info.InstrumentType}", info.BrokerSymbol);
    }

    /// <summary>Every candle between the dates, one request for each stretch Groww allows; a stretch Groww refuses is an error with its reason.</summary>
    private async Task<List<JsonArray>> FetchCandlesAsync(BrokerSession session, string symbol, string path, List<KeyValuePair<string, string>> fixedQuery, DateOnly start, DateOnly end, int maxDays, CancellationToken cancellationToken)
    {
        var candles = new List<JsonArray>();
        for (var from = start; from <= end;)
        {
            var to = from.AddDays(maxDays - 1) < end ? from.AddDays(maxDays - 1) : end;
            var query = new List<KeyValuePair<string, string>>(fixedQuery)
            {
                KeyValuePair.Create("start_time", $"{from:yyyy-MM-dd} 00:00:00"), KeyValuePair.Create("end_time", $"{to:yyyy-MM-dd} 23:59:59"),
            };
            var (status, body, _) = await SendAsync(session, HttpMethod.Get, path, "history", query, null, cancellationToken);
            if (status != 200 || body is not JsonObject answer || Str(answer["status"]) != "SUCCESS")
                throw new BrokerException($"{Display} did not return history for {symbol}: {(status == 200 && body is not JsonObject ? "the answer could not be read" : ReasonOf(body, $"HTTP {status}"))}", 500);
            candles.AddRange((Payload(body)["candles"] as JsonArray ?? []).OfType<JsonArray>());
            from = to.AddDays(1);
        }

        return candles;
    }

    private sealed record Candle(DateTime Stamp, JsonNode? Open, JsonNode? High, JsonNode? Low, JsonNode? Close, JsonNode? Volume);

    private static double? Number(JsonNode? node) => node is null ? null : BookFormatting.Number(node) ?? (double.TryParse(Str(node), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) ? parsed : null);

    /// <summary>15-minute session candles combined into candles of <paramref name="minutes"/> starting at 09:15 each day: first open, highest high, lowest low, last close, summed volume (none when Groww gave none).</summary>
    private static List<Candle> Rebucket(List<Candle> candles, int minutes)
    {
        var buckets = new SortedDictionary<DateTime, Candle>();
        foreach (var candle in candles.OrderBy(c => c.Stamp))
        {
            var open = candle.Stamp.Date.AddHours(9).AddMinutes(15);
            var key = open.AddMinutes((int)((candle.Stamp - open).TotalMinutes / minutes) * minutes);
            if (!buckets.TryGetValue(key, out var bucket))
            {
                buckets[key] = candle with { Stamp = key };
                continue;
            }

            JsonNode? Pick(Func<double, double, double> pick, JsonNode? a, JsonNode? b) => Number(a) is not { } x ? b : Number(b) is not { } y ? a : JsonValue.Create(pick(x, y));
            buckets[key] = bucket with { High = Pick(Math.Max, bucket.High, candle.High), Low = Pick(Math.Min, bucket.Low, candle.Low), Close = candle.Close, Volume = Pick((x, y) => x + y, bucket.Volume, candle.Volume) };
        }

        return [.. buckets.Values];
    }

    public async Task<JsonArray> GetHistoryAsync(BrokerSession session, SymbolRecord instrument, string interval, DateOnly start, DateOnly end, CancellationToken cancellationToken)
    {
        if (!Intervals.TryGetValue(interval, out var candleInterval))
            throw new BrokerException($"{Display} does not provide {interval} candles. Supported intervals: {string.Join(", ", Intervals.Keys)}", 500);
        var (exchange, segment, growwSymbol, tradingSymbol) = GrowwSymbolOf(instrument);
        var rows = new List<(long Timestamp, JsonNode? Open, JsonNode? High, JsonNode? Low, JsonNode? Close, JsonNode? Volume)>();
        if (interval is "D" or "W")
        {
            var minutes = interval == "D" ? "1440" : "10080";
            var candles = await FetchCandlesAsync(session, instrument.Symbol, "/v1/historical/candle/range",
                [KeyValuePair.Create("exchange", exchange), KeyValuePair.Create("segment", segment), KeyValuePair.Create("trading_symbol", tradingSymbol), KeyValuePair.Create("interval_in_minutes", minutes)], start, end, MaxDays[minutes], cancellationToken);
            foreach (var candle in candles)
            {
                var seconds = (long)(Number(candle[0]) ?? 0);
                if (seconds > 4102444800) seconds /= 1000;
                var day = DateTimeOffset.FromUnixTimeSeconds(seconds).UtcDateTime.Add(India).Date;
                rows.Add((new DateTimeOffset(day, TimeSpan.Zero).ToUnixTimeSeconds(), candle.ElementAtOrDefault(1)?.DeepClone(), candle.ElementAtOrDefault(2)?.DeepClone(), candle.ElementAtOrDefault(3)?.DeepClone(), candle.ElementAtOrDefault(4)?.DeepClone(), candle.ElementAtOrDefault(5)?.DeepClone()));
            }
        }
        else
        {
            var fetchInterval = Rebuilt.Contains(candleInterval) ? "15minute" : candleInterval;
            var candles = await FetchCandlesAsync(session, instrument.Symbol, "/v1/historical/candles",
                [KeyValuePair.Create("exchange", exchange), KeyValuePair.Create("segment", segment), KeyValuePair.Create("groww_symbol", growwSymbol), KeyValuePair.Create("candle_interval", fetchInterval)], start, end, MaxDays[fetchInterval], cancellationToken);
            var session_ = new List<Candle>();
            foreach (var candle in candles)
            {
                if (!DateTime.TryParse((Str(candle[0]) ?? "").Replace(' ', 'T'), CultureInfo.InvariantCulture, DateTimeStyles.None, out var stamp))
                    throw new BrokerException($"{Display} sent a candle with a time that could not be read. Try again in a moment.", 502);
                var open = stamp.Date.AddHours(9).AddMinutes(15);
                if (stamp.AddMinutes(CandleMinutes[fetchInterval]) <= open) continue;
                if (candle.ElementAtOrDefault(1) is null) continue;
                session_.Add(new Candle(stamp, candle[1]?.DeepClone(), candle.ElementAtOrDefault(2)?.DeepClone(), candle.ElementAtOrDefault(3)?.DeepClone(), candle.ElementAtOrDefault(4)?.DeepClone(), candle.ElementAtOrDefault(5)?.DeepClone()));
            }

            if (Rebuilt.Contains(candleInterval)) session_ = Rebucket(session_, CandleMinutes[candleInterval]);
            foreach (var c in session_) rows.Add((new DateTimeOffset(DateTime.SpecifyKind(c.Stamp - India, DateTimeKind.Utc)).ToUnixTimeSeconds(), c.Open, c.High, c.Low, c.Close, c.Volume));
        }

        var result = new JsonArray();
        foreach (var row in rows.GroupBy(r => r.Timestamp).Select(g => g.First()).OrderBy(r => r.Timestamp))
            result.Add(new JsonObject
            {
                ["timestamp"] = row.Timestamp, ["open"] = row.Open, ["high"] = row.High, ["low"] = row.Low, ["close"] = row.Close, ["volume"] = Number(row.Volume) is { } v ? (long)Math.Truncate(v) : 0, ["oi"] = 0,
            });
        return result;
    }
}
