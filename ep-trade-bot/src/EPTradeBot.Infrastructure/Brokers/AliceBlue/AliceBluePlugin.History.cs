using System.Globalization;
using System.Text.Json.Nodes;
using EPTradeBot.Core.Brokers;
using static EPTradeBot.Infrastructure.Brokers.JsonRead;

namespace EPTradeBot.Infrastructure.Brokers.AliceBlue;

/// <summary>
/// AliceBlue history (broker/aliceblue api/data.py): one POST to the chart service with millisecond bounds in IST. An intraday request starts at a quarter past nine on the first day and a daily one at midnight; the end is the last second of
/// the last day, capped to the present, and for a daily request taken to the midnight that follows (the service answers nothing for a daily request that ends part way through a day). A range shorter than an hour is stretched to an hour. AliceBlue gives
/// one-minute and daily candles; three, five, ten, fifteen, thirty and sixty minutes are built from the minutes in bins that start on the clock. A minute candle is stamped at the end of its minute and is moved to the start of it; a daily
/// one is stamped at midnight of its day. An index asks for its exchange with "::index" added, and the currency exchange (BCD) is not served.
/// </summary>
public sealed partial class AliceBluePlugin : IBrokerHistory
{
    private const string ChartPath = "/open-api/od/ChartAPIService/api/chart/history";
    private static readonly Dictionary<string, int> Resampled = new() { ["3m"] = 3, ["5m"] = 5, ["10m"] = 10, ["15m"] = 15, ["30m"] = 30, ["1h"] = 60 };
    private static readonly TimeSpan Ist = TimeSpan.FromHours(5.5);

    public IReadOnlyList<string> HistoryTimeframes { get; } = ["1m", "3m", "5m", "10m", "15m", "30m", "1h", "D"];

    private sealed record Candle(long Stamp, double Open, double High, double Low, double Close, double Volume);

    private static long Millis(DateTime ist) => new DateTimeOffset(ist, Ist).ToUnixTimeMilliseconds();

    public async Task<JsonArray> GetHistoryAsync(BrokerSession session, SymbolRecord instrument, string interval, DateOnly start, DateOnly end, CancellationToken cancellationToken)
    {
        if (!HistoryTimeframes.Contains(interval)) throw new BrokerException($"Interval {interval} is not supported by {Display}. Use one of: {string.Join(", ", HistoryTimeframes)}.", 400);
        var exchange = instrument.Exchange.ToUpperInvariant();
        if (exchange == "BCD") return [];
        exchange = exchange switch { "NSE_INDEX" => "NSE::index", "BSE_INDEX" => "BSE::index", "MCX_INDEX" => "MCX::index", _ => exchange };
        var daily = interval == "D";
        var now = clock.GetUtcNow().ToUnixTimeMilliseconds();
        var from = Millis(daily ? start.ToDateTime(TimeOnly.MinValue) : start.ToDateTime(new TimeOnly(9, 15)));
        var to = Millis(end.ToDateTime(new TimeOnly(23, 59, 59)));
        if (from > now) return [];
        if (to > now) to = now;
        if (daily)
        {
            var boundary = to / 86_400_000 * 86_400_000;
            if (boundary != to) to = boundary + 86_400_000;
        }

        if (from == to) to += 86_400_000;
        if (!daily && to - from < 3_600_000) to = from + 3_600_000;

        var body = new JsonObject { ["token"] = Instrument(instrument.Token), ["exchange"] = exchange, ["from"] = from.ToString(CultureInfo.InvariantCulture), ["to"] = to.ToString(CultureInfo.InvariantCulture), ["resolution"] = daily ? "D" : "1" };
        var reply = await SendAsync(session, HttpMethod.Post, ChartPath, body, read: true, cancellationToken);
        if (reply.Status >= 400 || reply.Body is not JsonObject answer) throw new BrokerException($"{Display} could not return candles for {instrument.Exchange}:{instrument.Symbol} right now. Try again in a moment.", 500);
        var stat = (Str(answer["stat"]) ?? "").ToLowerInvariant();
        if (stat is "not_ok" or "not ok" || answer["result"] is not JsonArray rows) return [];

        var candles = ReadCandles(rows, daily);
        if (candles is null) return [];
        // Sorted by time and one candle for each time, the first of equal ones kept.
        var ordered = candles.Select((c, i) => (c, i)).OrderBy(x => x.c.Stamp).ThenBy(x => x.i).Select(x => x.c).GroupBy(c => c.Stamp).Select(g => g.First()).ToList();
        if (Resampled.TryGetValue(interval, out var minutes)) ordered = Resample(ordered, minutes);
        var result = new JsonArray();
        foreach (var c in ordered) result.Add(new JsonObject { ["timestamp"] = c.Stamp, ["open"] = c.Open, ["high"] = c.High, ["low"] = c.Low, ["close"] = c.Close, ["volume"] = c.Volume, ["oi"] = 0 });
        return result;
    }

    /// <summary>The candles of an answer, or null when it cannot be read (a column missing, a time or a number that is not one).</summary>
    private static List<Candle>? ReadCandles(JsonArray rows, bool daily)
    {
        var objects = rows.OfType<JsonObject>().ToList();
        var columns = new[] { "time", "open", "high", "low", "close", "volume" };
        if (objects.Count == 0 || !columns.All(c => objects.Any(o => o.ContainsKey(c)))) return null;
        var list = new List<Candle>();
        foreach (var o in objects)
        {
            var time = Str(o["time"]);
            if (time is null || !DateTime.TryParse(time, CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed)) return null;
            var local = daily ? parsed.Date : new DateTime(parsed.Year, parsed.Month, parsed.Day, parsed.Hour, parsed.Minute, 0);
            var stamp = new DateTimeOffset(local, Ist).ToUnixTimeSeconds();
            var numbers = new double[5];
            for (var i = 0; i < 5; i++)
            {
                if (!o.TryGetPropertyValue(columns[i + 1], out var node) || node is null) { numbers[i] = 0; continue; }
                if (Number(node) is not { } n) return null;
                numbers[i] = n;
            }

            list.Add(new Candle(stamp, numbers[0], numbers[1], numbers[2], numbers[3], numbers[4]));
        }

        return list;
    }

    /// <summary>Bins that start on the clock (midnight, then every n minutes), a bin with no candle left out.</summary>
    private static List<Candle> Resample(List<Candle> candles, int minutes)
    {
        var size = minutes * 60L;
        var bins = new SortedDictionary<long, List<Candle>>();
        foreach (var c in candles)
        {
            var local = c.Stamp + (long)Ist.TotalSeconds;
            var start = Math.DivRem(local, size, out _) * size - (long)Ist.TotalSeconds;
            if (!bins.TryGetValue(start, out var list)) bins[start] = list = [];
            list.Add(c);
        }

        return bins.Select(b => new Candle(b.Key, b.Value[0].Open, b.Value.Max(c => c.High), b.Value.Min(c => c.Low), b.Value[^1].Close, b.Value.Sum(c => c.Volume))).ToList();
    }
}
