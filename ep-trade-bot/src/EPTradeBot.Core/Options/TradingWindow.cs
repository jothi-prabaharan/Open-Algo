using System.Text.Json.Nodes;

namespace EPTradeBot.Core.Options;

/// <summary>How many trading days of candles the charts ask for and keep, as OpenAlgo's strategy chart service does: ask generously, keep the last N dates that actually have data.</summary>
public static class TradingWindow
{
    private static readonly TimeSpan IstOffset = TimeSpan.FromHours(5.5);

    /// <summary>The calendar window (start, end as yyyy-MM-dd) that holds at least <paramref name="days"/> trading dates: today back by days x 3 + 2 (never less than 2).</summary>
    public static (string Start, string End) Resolve(int days, DateTime nowIst)
    {
        var buffer = Math.Max(2, days * 3 + 2);
        var today = nowIst.Date;
        return (today.AddDays(-buffer).ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture), today.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture));
    }

    private static DateOnly IstDate(long unixSeconds) => DateOnly.FromDateTime(DateTimeOffset.FromUnixTimeSeconds(unixSeconds).UtcDateTime.Add(IstOffset));

    /// <summary>Keeps the rows on the last N distinct Indian dates present; every row when N is not positive.</summary>
    public static JsonArray CapLastTradingDates(JsonArray series, int n)
    {
        if (series.Count == 0 || n <= 0) return series;
        var tagged = series.Select(r => (Date: IstDate((long)OptionChainReading.Num(r, "time")), Row: r)).ToList();
        var keep = tagged.Select(t => t.Date).Distinct().OrderByDescending(d => d).Take(n).ToHashSet();
        var result = new JsonArray();
        foreach (var t in tagged.Where(t => keep.Contains(t.Date))) result.Add(t.Row!.DeepClone());
        return result;
    }

    /// <summary>Gives each candle a <c>time</c> in Unix seconds from its <c>timestamp</c>, which a broker may send in milliseconds.</summary>
    public static void TagTime(JsonArray candles)
    {
        foreach (var c in candles)
        {
            if (c is not JsonObject o || o.ContainsKey("time") || !o.ContainsKey("timestamp")) continue;
            if (o["timestamp"] is not JsonValue v) continue;
            double ts;
            if (v.TryGetValue<double>(out var d)) ts = d;
            else if (v.TryGetValue<long>(out var l)) ts = l;
            else continue;
            o["time"] = ts > 1e12 ? (long)Math.Floor(ts / 1000) : (long)ts;
        }
    }
}
