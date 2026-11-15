using System.Globalization;
using System.Text.Json.Nodes;
using EPTradeBot.Core.Brokers;
using static EPTradeBot.Infrastructure.Brokers.JsonRead;

namespace EPTradeBot.Infrastructure.Brokers.Nubra;

/// <summary>
/// Nubra history (broker/nubra api/data.py get_history): POST /charts/timeseries with the broker symbol, the segment (NSE, BSE or MCX; the F&amp;O of NSE and BSE go under their parents) and the instrument type (STOCK, INDEX, FUT, OPT), the fields (open, high, low, close and, but for
/// an index, tick_volume) and the range as ISO times. The range goes in chunks of days that depend on the interval, a second apart, the last cut at the present and a chunk wholly in the future ending the read. Each field is its own series of {ts in nanoseconds, v in paise}; a candle is
/// the fields found at one time, and a volume is added only to a time that has a price. A chunk Nubra rejects does not end the read: only when nothing at all was found is the last rejection what the trader is told.
/// </summary>
public sealed partial class NubraPlugin : IBrokerHistory
{
    private static readonly OrderedDictionary<string, (string Api, int Days)> Frames = new()
    {
        ["1s"] = ("1s", 7), ["1m"] = ("1m", 30), ["2m"] = ("2m", 30), ["3m"] = ("3m", 60), ["5m"] = ("5m", 60), ["15m"] = ("15m", 60), ["30m"] = ("30m", 90), ["1h"] = ("1h", 90), ["D"] = ("1d", 365), ["W"] = ("1w", 1000), ["M"] = ("1mt", 1500),
    };

    public IReadOnlyList<string> HistoryTimeframes { get; } = Frames.Keys.ToList();

    public async Task<JsonArray> GetHistoryAsync(BrokerSession session, SymbolRecord instrument, string interval, DateOnly start, DateOnly end, CancellationToken cancellationToken)
    {
        if (!Frames.TryGetValue(interval, out var frame)) throw new BrokerException($"Timeframe '{interval}' is not supported by {Display}. Supported timeframes are: {string.Join(", ", Frames.Keys)}", 400);
        var optionLike = instrument.Symbol.Contains("CE", StringComparison.Ordinal) || instrument.Symbol.Contains("PE", StringComparison.Ordinal);
        var (type, api) = instrument.Exchange switch
        {
            "NSE_INDEX" => ("INDEX", "NSE"), "BSE_INDEX" => ("INDEX", "BSE"), "NFO" => (optionLike ? "OPT" : "FUT", "NSE"), "BFO" => (optionLike ? "OPT" : "FUT", "BSE"), "MCX" => (optionLike ? "OPT" : "FUT", "MCX"),
            "NSE" or "BSE" => ("STOCK", instrument.Exchange),
            _ => throw new BrokerException($"{Display} has no candles for {instrument.Exchange}. It serves NSE, BSE, NFO, BFO, MCX, NSE_INDEX and BSE_INDEX.", 400),
        };
        var fields = new JsonArray("open", "high", "low", "close");
        if (type != "INDEX") fields.Add("tick_volume");

        var candles = new SortedDictionary<long, double[]>();
        (string Kind, string Detail)? failure = null;
        var from = start.ToDateTime(TimeOnly.MinValue);
        var to = end.ToDateTime(TimeOnly.MinValue);
        var current = from;
        while (current <= to)
        {
            var currentEnd = current.AddDays(frame.Days - 1) < to ? current.AddDays(frame.Days - 1) : to;
            var chunkEnd = currentEnd.Date.AddDays(1).AddSeconds(-1);
            var now = Now.UtcDateTime;
            if (chunkEnd > now) chunkEnd = now;
            if (chunkEnd <= current) break;
            var payload = new JsonObject
            {
                ["query"] = new JsonArray(new JsonObject
                {
                    ["exchange"] = api, ["type"] = type, ["values"] = new JsonArray(instrument.BrokerSymbol), ["fields"] = fields.DeepClone(), ["startDate"] = Iso(current), ["endDate"] = Iso(chunkEnd), ["interval"] = frame.Api, ["intraDay"] = false, ["realTime"] = false,
                }),
            };
            try
            {
                var (status, body, text) = await SendAsync(session, HttpMethod.Post, "/charts/timeseries", payload, true, cancellationToken);
                if (status == SessionExpired) throw new BrokerException(ExpiredMessage, 401);
                if (status == 429) failure = ("rejection", $"{Display} is limiting how fast history is asked for.");
                else if (body is JsonObject answer)
                {
                    if (Truthy(answer["error"])) failure = ("rejection", Str(answer["error"])!);
                    if (status < 400 && Str(answer["message"]) == "charts") Collect(answer, instrument.BrokerSymbol, candles);
                }
                else if (!string.IsNullOrWhiteSpace(text) && status < 400) failure = ("exception", "the answer could not be read");
            }
            catch (BrokerException ex) when (ex.HttpStatus == 502)
            {
                failure = ("exception", ex.Message);
            }

            current = currentEnd.AddDays(1);
            if (current <= to) await Task.Delay(sessions.HistoryGap(Name, TimeSpan.FromSeconds(1)), cancellationToken);
        }

        if (candles.Count == 0)
        {
            if (failure is { } f)
                throw new BrokerException(f.Kind == "rejection" ? $"{Display} rejected the history request for {instrument.Symbol} on {instrument.Exchange} ({interval}): {f.Detail}" : $"{Display} could not give candles for {instrument.Symbol} on {instrument.Exchange} ({interval}): {f.Detail}", 502);
            return [];
        }

        var daily = interval is "D" or "W" or "M";
        var rows = new JsonArray();
        var seen = new HashSet<long>();
        foreach (var (ts, v) in candles.OrderBy(c => c.Key))
        {
            var seconds = FloorDiv(ts, 1_000_000_000L);
            if (daily) seconds -= ((seconds % 86400) + 86400) % 86400;
            if (!seen.Add(seconds)) continue;
            rows.Add(new JsonObject { ["timestamp"] = seconds, ["open"] = v[0], ["high"] = v[1], ["low"] = v[2], ["close"] = v[3], ["volume"] = (long)v[4], ["oi"] = 0 });
        }

        return rows;
    }

    private static long FloorDiv(long a, long b) => a / b - (a % b != 0 && (a < 0) != (b < 0) ? 1 : 0);

    private static string Iso(DateTime time) => time.ToString("yyyy-MM-dd'T'HH:mm:ss'.000Z'", CultureInfo.InvariantCulture);

    private static void Collect(JsonObject answer, string brokerSymbol, SortedDictionary<long, double[]> candles)
    {
        if (answer["result"] is not JsonArray { Count: > 0 } result || result[0] is not JsonObject first) return;
        JsonObject? series = null;
        foreach (var entry in (first["values"] as JsonArray ?? []).OfType<JsonObject>())
            if (entry[brokerSymbol] is JsonObject found) { series = found; break; }
        if (series is null || series.Count == 0) return;
        void Fold(string field, int at, bool volume)
        {
            foreach (var point in (series[field] as JsonArray ?? []).OfType<JsonObject>())
            {
                var ts = Truthy(point["ts"]) ? (long)Math.Truncate(Float(point["ts"])) : 0;
                var value = Truthy(point["v"]) ? Float(point["v"]) : 0;
                if (volume)
                {
                    if (candles.TryGetValue(ts, out var existing)) existing[at] = Math.Truncate(value);
                    continue;
                }

                if (!candles.TryGetValue(ts, out var row)) candles[ts] = row = new double[5];
                row[at] = value / 100;
            }
        }

        Fold("open", 0, false);
        Fold("high", 1, false);
        Fold("low", 2, false);
        Fold("close", 3, false);
        var volumeKey = series["tick_volume"] is JsonArray { Count: > 0 } ? "tick_volume" : "cumulative_volume";
        Fold(volumeKey, 4, true);
    }
}
