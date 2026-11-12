using System.Globalization;
using System.Text.Json.Nodes;

namespace EPTradeBot.Core.Options;

/// <summary>What OpenAlgo's strategy chart and multi-strike OI services share: the history window a caller may name, the legs that count, and candle timestamps read as Indian time.</summary>
public static class StrategyChartSupport
{
    public const int MaxWindowDays = 400;
    private static readonly DateOnly EarliestStart = new(2000, 1, 1);
    private static readonly TimeSpan IstOffset = TimeSpan.FromHours(5.5);
    private const double SecondsLimit = 9223372036.0;

    /// <summary>A refusal the caller can correct; the message is the sentence OpenAlgo gives.</summary>
    public sealed class WindowException(string message) : Exception(message);

    private static DateOnly ParseYmd(string? value, string field)
    {
        if (value is not null && DateTime.TryParseExact(value, ["yyyy-M-d", "yyyy-MM-dd"], CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)) return DateOnly.FromDateTime(d);
        throw new WindowException($"{field} must be a date in YYYY-MM-DD form");
    }

    /// <summary>The window a chart paging older history names, or null when it named none (the day count applies).</summary>
    public static (string Start, string End)? ExplicitWindow(string? startDate, string? endDate, DateTime nowIst)
    {
        if (string.IsNullOrEmpty(startDate)) return null;
        var today = DateOnly.FromDateTime(nowIst);
        var start = ParseYmd(startDate, "start_date");
        var end = !string.IsNullOrEmpty(endDate) ? ParseYmd(endDate, "end_date") : today;
        if (start < EarliestStart) throw new WindowException($"start_date cannot be earlier than {EarliestStart:yyyy-MM-dd}");
        if (end < start) throw new WindowException("end_date cannot be earlier than start_date");
        if (end.DayNumber - start.DayNumber > MaxWindowDays) throw new WindowException($"the window cannot be wider than {MaxWindowDays} days");
        return (start.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), end.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
    }

    public sealed record Leg(string Symbol, string Exchange, string Side, int Sign, double EntryPrice);

    /// <summary>The leg if it counts: an active option with a symbol, a BUY or SELL side and an exchange.</summary>
    public static Leg? NormalizeLeg(JsonNode? node)
    {
        if (node is not JsonObject leg) return null;
        if (leg["active"] is JsonValue a && a.TryGetValue<bool>(out var active) && !active) return null;
        var segment = (TextOrEmpty(leg, "segment") is { Length: > 0 } s ? s : "OPTION").ToUpperInvariant();
        if (segment != "OPTION") return null;
        var symbol = TextOrEmpty(leg, "symbol").Trim();
        var side = TextOrEmpty(leg, "side").ToUpperInvariant();
        var exchange = TextOrEmpty(leg, "exchange").Trim().ToUpperInvariant();
        if (symbol.Length == 0 || side is not ("BUY" or "SELL") || exchange.Length == 0) return null;
        return new Leg(symbol, exchange, side, side == "SELL" ? 1 : -1, EntryPrice(leg["price"]));
    }

    private static string TextOrEmpty(JsonObject o, string key) => o[key] is JsonValue v && v.TryGetValue<string>(out var s) ? s : string.Empty;

    private static double EntryPrice(JsonNode? price)
    {
        if (price is not JsonValue v) return 0.0;
        if (v.TryGetValue<double>(out var d)) return d;
        if (v.TryGetValue<long>(out var l)) return l;
        if (v.TryGetValue<string>(out var s) && double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)) return parsed;
        return 0.0;
    }

    /// <summary>Candles sorted by time, each with its Unix second. Null when the history has no timestamp column. A timestamp too large for seconds is read as milliseconds.</summary>
    public static List<(long Time, JsonObject Row)>? Candles(JsonArray data)
    {
        if (data.Count == 0 || !data.Any(r => r is JsonObject o && o.ContainsKey("timestamp"))) return null;
        var raw = data.OfType<JsonObject>().Select(o => (Row: o, Ts: OptionChainReading.Num(o, "timestamp"))).ToList();
        var milliseconds = raw.Any(r => Math.Abs(r.Ts) >= SecondsLimit);
        return raw.Select(r => ((long)Math.Truncate(milliseconds ? r.Ts / 1000.0 : r.Ts), r.Row)).OrderBy(x => x.Item1).ToList();
    }

    /// <summary>Keeps the rows on the last N distinct Indian dates present.</summary>
    public static List<JsonObject> CapLastTradingDates(List<JsonObject> series, int n)
    {
        if (series.Count == 0 || n <= 0) return series;
        static DateOnly Date(JsonObject o) => DateOnly.FromDateTime(DateTimeOffset.FromUnixTimeSeconds((long)OptionChainReading.Num(o, "time")).UtcDateTime.Add(IstOffset));
        var keep = series.Select(Date).Distinct().OrderByDescending(d => d).Take(n).ToHashSet();
        return series.Where(r => keep.Contains(Date(r))).ToList();
    }

    public static JsonArray ToArray(IEnumerable<JsonObject> rows)
    {
        var a = new JsonArray();
        foreach (var r in rows) a.Add(r.DeepClone());
        return a;
    }
}
