using System.Globalization;
using System.Text.Json.Nodes;
using EPTradeBot.Core.Brokers;
using static EPTradeBot.Infrastructure.Brokers.JsonRead;

namespace EPTradeBot.Infrastructure.Brokers.Samco;

/// <summary>
/// Samco history (broker/samco/api/data.py): daily candles from /history/candleData (or indexCandleData), minute candles from /intraday/candleData. A daily request that ends today stops at yesterday, as OpenAlgo
/// does, and Samco's own candle keys differ from its documentation (an index's intraday candles arrive under intradayCandleData), so each documented and observed key is read.
/// </summary>
public sealed partial class SamcoPlugin
{
    private static readonly Dictionary<string, string> Intervals = new() { ["1m"] = "1", ["5m"] = "5", ["10m"] = "10", ["15m"] = "15", ["30m"] = "30", ["1h"] = "60", ["D"] = "DAY" };

    public IReadOnlyList<string> HistoryTimeframes { get; } = [.. Intervals.Keys];

    private static readonly TimeSpan Ist = TimeSpan.FromHours(5.5);

    private static double Coerced(JsonNode? node) => node is null ? 0.0 : SafeFloat(JsonValue.Create(Str(node)?.Replace(",", "", StringComparison.Ordinal)), 0.0);

    /// <summary>pd.to_datetime of a candle time: the date text Samco sends, with or without a clock time.</summary>
    private static DateTime? ParseTime(JsonNode? node)
    {
        var text = Str(node);
        return text is not null && DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed) ? parsed : null;
    }

    public async Task<JsonArray> GetHistoryAsync(BrokerSession session, SymbolRecord instrument, string interval, DateOnly start, DateOnly end, CancellationToken cancellationToken)
    {
        if (!Intervals.ContainsKey(interval)) throw new BrokerException($"Interval {interval} is not supported by {DisplayName}. Use one of: {string.Join(", ", Intervals.Keys)}.", 400);
        var index = IsIndex(instrument.Exchange);
        var today = DateOnly.FromDateTime(clock.GetUtcNow().ToOffset(Ist).DateTime);
        if (interval == "D")
        {
            var last = end == today && start < today ? today.AddDays(-1) : end;
            return await CandlesAsync(session, instrument, index, true, start.ToDateTime(TimeOnly.MinValue), last.ToDateTime(TimeOnly.MinValue), "", cancellationToken);
        }

        var intervalParam = interval != "1m" ? $"&interval={Intervals[interval]}" : "";
        return await CandlesAsync(session, instrument, index, false, start.ToDateTime(TimeOnly.MinValue), end.ToDateTime(new TimeOnly(23, 59, 59)), intervalParam, cancellationToken);
    }

    private async Task<JsonArray> CandlesAsync(BrokerSession session, SymbolRecord instrument, bool index, bool daily, DateTime from, DateTime to, string intervalParam, CancellationToken cancellationToken)
    {
        string path;
        string[] keys;
        if (daily)
        {
            var span = $"fromDate={from:yyyy-MM-dd}&toDate={to:yyyy-MM-dd}";
            if (index)
            {
                path = $"/history/indexCandleData?indexName={Escape(IndexName(instrument))}&{span}";
                keys = ["indexCandleData", "historicalCandleData"];
            }
            else
            {
                path = $"/history/candleData?symbolName={Escape(instrument.BrokerSymbol)}&{span}" + (instrument.Exchange is not ("" or "NSE") ? $"&exchange={instrument.Exchange}" : "");
                keys = ["historicalCandleData"];
            }
        }
        else
        {
            var span = $"fromDate={Escape(from.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture))}&toDate={Escape(to.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture))}{intervalParam}";
            if (index)
            {
                path = $"/intraday/indexCandleData?indexName={Escape(IndexName(instrument))}&{span}";
                keys = ["indexIntraDayCandleData", "intradayCandleData"];
            }
            else
            {
                path = $"/intraday/candleData?symbolName={Escape(instrument.BrokerSymbol)}&{span}" + (instrument.Exchange is not ("" or "NSE") ? $"&exchange={instrument.Exchange}" : "");
                keys = ["intradayCandleData"];
            }
        }

        var answer = await DataAsync(session, HttpMethod.Get, path, null, cancellationToken);
        if (!Succeeded(answer)) return [];
        var candles = keys.Select(k => answer[k] as JsonArray).FirstOrDefault(a => a is { Count: > 0 });
        if (candles is null) return [];

        var bars = new SortedDictionary<long, JsonObject>();
        foreach (var row in candles.OfType<JsonObject>())
        {
            var stamp = ParseTime(row[daily ? "date" : "dateTime"] ?? row["timestamp"]);
            if (stamp is null) continue;
            var epoch = daily
                ? new DateTimeOffset(stamp.Value.Date, TimeSpan.Zero).ToUnixTimeSeconds()
                : new DateTimeOffset(stamp.Value, Ist).ToUnixTimeSeconds();
            bars.TryAdd(epoch, new JsonObject
            {
                ["timestamp"] = epoch, ["open"] = Coerced(row["open"]), ["high"] = Coerced(row["high"]), ["low"] = Coerced(row["low"]), ["close"] = Coerced(row["close"]), ["volume"] = (long)Coerced(row["volume"]),
                ["oi"] = row["oi"] is null ? 0 : SafeInt(row["oi"]),
            });
        }

        return new JsonArray(bars.Values.Select(v => (JsonNode?)v).ToArray());
    }
}
