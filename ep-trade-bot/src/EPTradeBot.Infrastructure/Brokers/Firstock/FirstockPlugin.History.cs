using System.Globalization;
using System.Text.Json.Nodes;
using EPTradeBot.Core.Brokers;
using static EPTradeBot.Infrastructure.Brokers.JsonRead;

namespace EPTradeBot.Infrastructure.Brokers.Firstock;

/// <summary>
/// Firstock history (broker/firstock/api/data.py): POST /timePriceSeries. One-minute candles are asked for a day at a time; every other interval in chunks of days that grow with the interval (two for three minutes up to
/// thirty for daily), a half second apart. A range that fits in one chunk is one call whose failure leaves the answer empty; in a longer range a chunk that fails is left out. Daily candles are stamped 09:15 of their date
/// in UTC terms, as OpenAlgo stamps them. A candle with a figure that is not a number is skipped.
/// </summary>
public sealed partial class FirstockPlugin
{
    private static readonly Dictionary<string, string> Intervals = new()
    {
        ["1m"] = "1mi", ["3m"] = "3mi", ["5m"] = "5mi", ["10m"] = "10mi", ["15m"] = "15mi", ["30m"] = "30mi", ["1h"] = "60mi", ["2h"] = "120mi", ["4h"] = "240mi", ["D"] = "1d",
    };

    private static readonly Dictionary<string, int> ChunkDays = new() { ["3m"] = 2, ["5m"] = 3, ["10m"] = 5, ["15m"] = 7, ["30m"] = 10, ["1h"] = 15, ["2h"] = 15, ["4h"] = 15, ["D"] = 30 };

    public IReadOnlyList<string> HistoryTimeframes { get; } = [.. Intervals.Keys];

    private static string Day(DateOnly date) => date.ToString("dd-MM-yyyy", CultureInfo.InvariantCulture);

    private async Task<JsonArray?> SeriesAsync(BrokerSession session, SymbolRecord instrument, string interval, string startTime, string endTime, CancellationToken cancellationToken)
    {
        var body = new JsonObject
        {
            ["exchange"] = MapExchange(instrument.Exchange), ["tradingSymbol"] = instrument.BrokerSymbol, ["startTime"] = startTime, ["endTime"] = endTime, ["interval"] = Intervals[interval],
        };
        JsonObject answer;
        try
        {
            answer = await DataPostAsync(session, "/timePriceSeries", body, cancellationToken);
        }
        catch (BrokerException)
        {
            return null;
        }

        return Succeeded(answer) ? answer["data"] as JsonArray ?? [] : null;
    }

    /// <summary>int(value) of a candle's volume: a whole number, text of a whole number, or a number that is cut; text with a fraction or a comma, or no value at all, is not one.</summary>
    private static bool TryVolume(JsonObject candle, out long volume)
    {
        volume = 0;
        if (!candle.ContainsKey("volume")) return true;
        var node = candle["volume"];
        if (node is null) return false;
        if (node is JsonValue v && v.TryGetValue<string>(out var text)) return long.TryParse(text.Trim(), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out volume);
        if (BookFormatting.Number(node) is not { } n) return false;
        volume = (long)Math.Truncate(n);
        return true;
    }

    /// <summary>The candles of one answer; a candle whose time or figures cannot be read is skipped.</summary>
    private static List<JsonObject> Candles(JsonArray rows, bool daily)
    {
        var candles = new List<JsonObject>();
        foreach (var candle in rows.OfType<JsonObject>())
        {
            long stamp;
            if (candle.ContainsKey("epochTime")) stamp = (long)Math.Truncate(Num(candle["epochTime"]));
            else if (Str(candle["time"]) is { } text && DateTime.TryParse(text.Replace('T', ' '), CultureInfo.InvariantCulture, DateTimeStyles.None, out var moment)) stamp = new DateTimeOffset(moment, TimeSpan.FromHours(5.5)).ToUnixTimeSeconds();
            else continue;
            if (!TryVolume(candle, out var volume)) continue;
            if (daily) stamp = stamp - stamp % 86400 + 33300;
            candles.Add(new JsonObject { ["timestamp"] = stamp, ["open"] = Num(candle["open"]), ["high"] = Num(candle["high"]), ["low"] = Num(candle["low"]), ["close"] = Num(candle["close"]), ["volume"] = volume, ["oi"] = 0 });
        }

        return candles;
    }

    public async Task<JsonArray> GetHistoryAsync(BrokerSession session, SymbolRecord instrument, string interval, DateOnly start, DateOnly end, CancellationToken cancellationToken)
    {
        if (!Intervals.ContainsKey(interval)) throw new BrokerException($"Interval {interval} is not supported by {DisplayName}. Use one of: {string.Join(", ", HistoryTimeframes)}.", 400);
        var daily = interval == "D";
        var gap = sessions.HistoryGap(Name, TimeSpan.FromMilliseconds(500));
        var all = new List<JsonObject>();

        if (interval == "1m")
        {
            for (var day = start; day <= end; day = day.AddDays(1))
            {
                if (await SeriesAsync(session, instrument, interval, $"00:00:00 {Day(day)}", $"23:59:59 {Day(day)}", cancellationToken) is { } rows) all.AddRange(Candles(rows, false));
                await Task.Delay(gap, cancellationToken);
            }

            return Ordered(all);
        }

        var days = end.DayNumber - start.DayNumber + 1;
        var chunk = ChunkDays.GetValueOrDefault(interval, 30);
        string From(DateOnly d) => $"00:00:00 {Day(d)}";
        string To(DateOnly d) => daily ? $"00:00:00 {Day(d)}" : $"23:59:59 {Day(d)}";

        if (days <= chunk)
        {
            // One call: a failure leaves the whole answer empty.
            return Ordered(await SeriesAsync(session, instrument, interval, From(start), To(end), cancellationToken) is { } rows ? Candles(rows, daily) : [], deduplicate: false);
        }

        for (var from = start; from <= end;)
        {
            var to = from.AddDays(chunk - 1) < end ? from.AddDays(chunk - 1) : end;
            if (await SeriesAsync(session, instrument, interval, From(from), To(to), cancellationToken) is { } rows) all.AddRange(Candles(rows, daily));
            from = to.AddDays(1);
            if (from <= end) await Task.Delay(gap, cancellationToken);
        }

        return Ordered(all);
    }

    private static JsonArray Ordered(List<JsonObject> candles, bool deduplicate = true)
    {
        var ordered = deduplicate ? candles.GroupBy(c => c["timestamp"]!.GetValue<long>()).Select(g => g.First()).OrderBy(c => c["timestamp"]!.GetValue<long>()) : candles.OrderBy(c => c["timestamp"]!.GetValue<long>());
        return new JsonArray(ordered.Select(c => (JsonNode?)c).ToArray());
    }
}
