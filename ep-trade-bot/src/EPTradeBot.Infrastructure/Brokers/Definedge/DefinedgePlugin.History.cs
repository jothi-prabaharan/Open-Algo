using System.Globalization;
using System.Text.Json.Nodes;
using EPTradeBot.Core.Brokers;

namespace EPTradeBot.Infrastructure.Brokers.Definedge;

/// <summary>
/// Definedge history (broker/definedge api/data.py): GET /history/{segment}/{token}/{minute|day}/{from}/{to} on the data host, CSV rows of a date and time (ddMMyyyyHHmm), open, high, low, close, volume and sometimes open interest.
/// Five, fifteen, thirty and sixty minutes are built from the minute rows in bins that start at the session open (a quarter past nine; on commodity and currency exchanges, on the hour). Chunks of 30, 90, 150, 180, 180 and 365 days
/// for one minute up to daily; a chunk is asked for up to three times (a server error, a timeout or a lost connection is tried again, another client error is not) and one that fails is left out. A chunk's last day ends at 23:59, or at the minute it is now
/// when the range reaches today. Candles outside the range are dropped, a price that is not a number is zero, daily candles are stamped at midnight of their date and the others in UTC.
/// </summary>
public sealed partial class DefinedgePlugin
{
    private static readonly Dictionary<string, string> HistoryFrames = new() { ["1m"] = "minute", ["5m"] = "minute", ["15m"] = "minute", ["30m"] = "minute", ["1h"] = "minute", ["D"] = "day" };
    private static readonly Dictionary<string, int> ChunkDays = new() { ["1m"] = 30, ["5m"] = 90, ["15m"] = 150, ["30m"] = 180, ["1h"] = 180, ["D"] = 365 };
    private static readonly Dictionary<string, int> BinMinutes = new() { ["5m"] = 5, ["15m"] = 15, ["30m"] = 30, ["1h"] = 60 };
    private static readonly HashSet<string> NoOffsetExchanges = ["MCX", "MCX_INDEX", "CDS", "BCD", "NCDEX"];

    public IReadOnlyList<string> HistoryTimeframes { get; } = ["1m", "5m", "15m", "30m", "1h", "D"];

    private sealed record Row(DateTime Time, double Open, double High, double Low, double Close, double Volume, double Oi);

    /// <summary>One chunk's CSV as rows, or null when every attempt failed (or the CSV could not be read).</summary>
    private async Task<(List<Row>? Rows, bool Failed)> ChunkAsync(BrokerSession session, string url, CancellationToken cancellationToken)
    {
        (int Status, JsonNode? Body, string Text)? reply = null;
        for (var attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                reply = await SendRawAsync(session, url, cancellationToken);
                if (reply.Value.Status == 200) break;
                if (reply.Value.Status is >= 400 and < 500 && reply.Value.Status is not (408 or 425)) break;
            }
            catch (BrokerException)
            {
                reply = null;
            }

            if (attempt < 2) await Task.Delay(sessions.RetryBackoff(Name, TimeSpan.FromSeconds(0.5 * (attempt + 1))), cancellationToken);
        }

        if (reply is not { Status: 200 }) return (null, true);
        var csv = reply.Value.Text.Trim();
        if (csv.Length == 0) return ([], false);
        var lines = csv.Split('\n').Select(l => l.TrimEnd('\r')).Where(l => l.Length > 0).ToList();
        var columns = lines[0].Split(',').Length == 6 ? 6 : 7;
        var rows = new List<Row>();
        foreach (var line in lines)
        {
            var fields = line.Split(',');
            if (fields.Length > columns) return (null, true);
            double Number(int i) => i < fields.Length && double.TryParse(fields[i].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : double.NaN;
            var stamp = fields[0].Trim();
            if (long.TryParse(stamp, NumberStyles.None, CultureInfo.InvariantCulture, out var asNumber)) stamp = asNumber.ToString(CultureInfo.InvariantCulture);
            stamp = stamp.PadLeft(12, '0');
            if (!DateTime.TryParseExact(stamp, "ddMMyyyyHHmm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var time)) continue;
            rows.Add(new Row(time, Number(1), Number(2), Number(3), Number(4), Number(5), columns == 6 ? 0 : Number(6)));
        }

        return (rows, false);
    }

    private static List<Row> Resample(List<Row> rows, int minutes, int offset)
    {
        var bins = new SortedDictionary<DateTime, List<Row>>();
        foreach (var row in rows)
        {
            var midnight = row.Time.Date;
            var since = (row.Time - midnight).TotalMinutes - offset;
            var start = midnight.AddMinutes(offset + Math.Floor(since / minutes) * minutes);
            if (!bins.TryGetValue(start, out var list)) bins[start] = list = [];
            list.Add(row);
        }

        var built = new List<Row>();
        foreach (var (start, list) in bins)
        {
            double? First(Func<Row, double> pick) => list.Select(pick).Where(v => !double.IsNaN(v)).Cast<double?>().FirstOrDefault();
            double? Last(Func<Row, double> pick) => list.Select(pick).Where(v => !double.IsNaN(v)).Cast<double?>().LastOrDefault();
            var highs = list.Select(r => r.High).Where(v => !double.IsNaN(v)).ToList();
            var lows = list.Select(r => r.Low).Where(v => !double.IsNaN(v)).ToList();
            if (First(r => r.Open) is not { } open || highs.Count == 0 || lows.Count == 0 || Last(r => r.Close) is not { } close || Last(r => r.Oi) is not { } oi) continue;
            built.Add(new Row(start, open, highs.Max(), lows.Min(), close, list.Select(r => r.Volume).Where(v => !double.IsNaN(v)).Sum(), oi));
        }

        return built;
    }

    public async Task<JsonArray> GetHistoryAsync(BrokerSession session, SymbolRecord instrument, string interval, DateOnly start, DateOnly end, CancellationToken cancellationToken)
    {
        if (!HistoryFrames.TryGetValue(interval, out var frame)) throw new BrokerException($"Interval {interval} is not supported by {Display}. Use one of: {string.Join(", ", HistoryTimeframes)}.", 400);
        var daily = interval == "D";
        var now = clock.GetUtcNow().ToOffset(TimeSpan.FromHours(5.5)).DateTime;
        var from = start.ToDateTime(TimeOnly.MinValue);
        var to = end.ToDateTime(TimeOnly.MinValue);
        if (!daily) to = to.Date == now.Date ? new DateTime(now.Year, now.Month, now.Day, now.Hour, now.Minute, 0) : to.AddHours(23).AddMinutes(59);

        var segment = ApiExchange(instrument.Exchange.ToUpperInvariant());
        var all = new List<Row>();
        var failed = 0;
        var asked = 0;
        for (var current = from; current <= to;)
        {
            var currentEnd = current.AddDays(ChunkDays[interval] - 1) < to ? current.AddDays(ChunkDays[interval] - 1) : to;
            DateTime next;
            if (daily)
            {
                next = currentEnd.AddDays(1);
            }
            else
            {
                var endOfDay = currentEnd.Date.AddHours(23).AddMinutes(59);
                currentEnd = endOfDay < to ? endOfDay : to;
                next = currentEnd.Date.AddDays(1);
            }

            var url = $"{DataUrl}/history/{segment}/{instrument.Token}/{frame}/{current:ddMMyyyyHHmm}/{currentEnd:ddMMyyyyHHmm}";
            asked++;
            var (rows, chunkFailed) = await ChunkAsync(session, url, cancellationToken);
            if (chunkFailed)
            {
                failed++;
            }
            else if (rows is { Count: > 0 })
            {
                all.AddRange(!daily && BinMinutes.TryGetValue(interval, out var minutes) ? Resample(rows, minutes, NoOffsetExchanges.Contains(instrument.Exchange.ToUpperInvariant()) ? 0 : 15) : rows);
            }

            current = next;
        }

        if (all.Count == 0 && failed > 0 && failed == asked) throw new BrokerException($"{Display} could not return candles for {instrument.Exchange}:{instrument.Symbol} right now. Try again in a moment.", 500);
        var result = new JsonArray();
        var seen = new HashSet<long>();
        foreach (var row in all.Where(r => r.Time >= from && r.Time <= to).Select((r, i) => (r, i)).OrderBy(x => x.r.Time).ThenBy(x => x.i).Select(x => x.r))
        {
            var stamp = daily ? new DateTimeOffset(row.Time.Date, TimeSpan.Zero).ToUnixTimeSeconds() : new DateTimeOffset(row.Time, TimeSpan.FromHours(5.5)).ToUnixTimeSeconds();
            if (!seen.Add(stamp)) continue;
            double Fixed(double v) => double.IsNaN(v) ? 0 : v;
            result.Add(new JsonObject { ["timestamp"] = stamp, ["open"] = Fixed(row.Open), ["high"] = Fixed(row.High), ["low"] = Fixed(row.Low), ["close"] = Fixed(row.Close), ["volume"] = Fixed(row.Volume), ["oi"] = (long)Math.Truncate(Fixed(row.Oi)) });
        }

        return result;
    }
}
