using System.Globalization;
using System.Text.Json.Nodes;
using EPTradeBot.Core.Brokers;
using static EPTradeBot.Infrastructure.Brokers.JsonRead;

namespace EPTradeBot.Infrastructure.Brokers.Tradejini;

/// <summary>
/// Tradejini history (broker/tradejini api/data.py get_history): one GET of the chart interval data with the symbol id, the interval (1, 5, 15 and 30 for the minute ones, anything else as it is) and epoch seconds, the first day from 09:15
/// India time and the last to 23:59:59. The answer's d.bars are the candles, as objects (time, open, high, low, close, volume) or as lists; a bar that cannot be read is left out, times beyond 1e12 are milliseconds, and the candles come back
/// in time order.
/// </summary>
public sealed partial class TradejiniPlugin : IBrokerHistory
{
    private static readonly Dictionary<string, string> HistoryIntervals = new() { ["1m"] = "1", ["5m"] = "5", ["15m"] = "15", ["30m"] = "30" };
    private static readonly TimeSpan Ist = TimeSpan.FromHours(5.5);

    public IReadOnlyList<string> HistoryTimeframes { get; } = ["1m", "5m", "15m", "30m"];

    private sealed record Bar(long Time, double Open, double High, double Low, double Close, long Volume);

    private static bool TryNumber(JsonNode? node, out double value)
    {
        value = 0;
        if (node is null) return false;
        if (BookFormatting.Number(node) is { } n) { value = n; return true; }
        return Str(node) is { } s && double.TryParse(s.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out value);
    }

    /// <summary>int(x): a number is cut, text has to be a whole number, absent or null is not a number.</summary>
    private static bool TryWhole(JsonNode? node, out long value)
    {
        value = 0;
        if (node is JsonValue v && v.TryGetValue<long>(out value)) return true;
        if (BookFormatting.Number(node) is { } n && double.IsFinite(n)) { value = (long)Math.Truncate(n); return true; }
        return Str(node) is { } s && long.TryParse(s.Trim(), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out value);
    }

    private static Bar? ReadBar(JsonNode? raw)
    {
        if (raw is JsonObject o)
        {
            var time = 0L;
            if (o.ContainsKey("time") && !TryWhole(o["time"], out time)) return null;
            double Pick(string name, out bool ok)
            {
                ok = true;
                if (!o.ContainsKey(name)) return 0;
                if (TryNumber(o[name], out var n)) return n;
                ok = false;
                return 0;
            }

            var open = Pick("open", out var a);
            var high = Pick("high", out var b);
            var low = Pick("low", out var c);
            var close = Pick("close", out var d);
            if (!(a && b && c && d)) return null;
            var volume = 0L;
            if (Truthy(o["volume"]) && !TryWhole(o["volume"], out volume)) return null;
            return new Bar(time, open, high, low, close, volume);
        }

        if (raw is JsonArray list && list.Count >= 5)
        {
            if (!TryWhole(list[0], out var time) || !TryNumber(list[1], out var open) || !TryNumber(list[2], out var high) || !TryNumber(list[3], out var low) || !TryNumber(list[4], out var close)) return null;
            var volume = 0L;
            if (list.Count > 5 && !TryWhole(list[5], out volume)) return null;
            return new Bar(time, open, high, low, close, volume);
        }

        return null;
    }

    public async Task<JsonArray> GetHistoryAsync(BrokerSession session, SymbolRecord instrument, string interval, DateOnly start, DateOnly end, CancellationToken cancellationToken)
    {
        if (!HistoryTimeframes.Contains(interval, StringComparer.Ordinal)) throw new BrokerException($"Interval {interval} is not supported by {Display}. Use one of: {string.Join(", ", HistoryTimeframes)}.", 400);
        var from = new DateTimeOffset(start.ToDateTime(new TimeOnly(9, 15)), Ist).ToUnixTimeSeconds();
        var to = new DateTimeOffset(end.ToDateTime(new TimeOnly(23, 59, 59)), Ist).ToUnixTimeSeconds();
        var query = new List<KeyValuePair<string, string>>
        {
            new("id", instrument.BrokerSymbol), new("interval", HistoryIntervals.GetValueOrDefault(interval, interval)), new("from", from.ToString(CultureInfo.InvariantCulture)), new("to", to.ToString(CultureInfo.InvariantCulture)),
        };
        var failure = new BrokerException($"{Display} could not give candles for {instrument.Exchange}:{instrument.Symbol} right now. Try again in a moment.", 500);
        var (status, body, _) = await SendAsync(session, HttpMethod.Get, "/api/mkt-data/chart/interval-data", query, null, cancellationToken, "application/json", accept: true);
        if (status >= 400 || body is not JsonObject answer) throw failure;
        if (Str(answer["s"]) == "no-data") return [];
        if (Str(answer["s"]) != "ok") throw failure;
        var bars = ((answer["d"] as JsonObject)?["bars"] as JsonArray ?? []).Select(ReadBar).OfType<Bar>().ToList();
        if (bars.Count == 0) return [];
        var millis = bars.Max(b => b.Time) > 1_000_000_000_000;
        var result = new JsonArray();
        foreach (var bar in bars.Select((b, i) => (b, i)).OrderBy(x => millis ? x.b.Time / 1000 : x.b.Time).ThenBy(x => x.i).Select(x => x.b))
            result.Add(new JsonObject { ["timestamp"] = millis ? bar.Time / 1000 : bar.Time, ["open"] = bar.Open, ["high"] = bar.High, ["low"] = bar.Low, ["close"] = bar.Close, ["volume"] = bar.Volume, ["oi"] = 0 });
        return result;
    }
}
