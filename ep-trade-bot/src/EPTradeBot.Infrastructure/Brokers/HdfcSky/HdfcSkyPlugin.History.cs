using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json.Nodes;
using EPTradeBot.Core.Brokers;
using Microsoft.Extensions.Logging;
using static EPTradeBot.Infrastructure.Brokers.JsonRead;

namespace EPTradeBot.Infrastructure.Brokers.HdfcSky;

/// <summary>
/// HDFC Sky history (broker/hdfcsky/api/data.py). The chart service serves one-minute and daily candles only (31 and 2000 days a call); the other intervals are built from those, the bins
/// starting at midnight UTC (weeks on a Monday, months on the first). The window is cut into calls with each retried on its own, and a chunk that never answers leaves a gap rather than ending the
/// download. A daily candle is stamped five and a half hours after its date's midnight UTC and an intraday one at the true epoch of its India time.
/// </summary>
public sealed partial class HdfcSkyPlugin
{
    private static readonly Dictionary<string, (string ChartType, int? BinMinutes, string? Calendar)> Intervals = new()
    {
        ["1m"] = ("MINUTE", null, null), ["3m"] = ("MINUTE", 3, null), ["5m"] = ("MINUTE", 5, null), ["10m"] = ("MINUTE", 10, null), ["15m"] = ("MINUTE", 15, null),
        ["30m"] = ("MINUTE", 30, null), ["1h"] = ("MINUTE", 60, null), ["D"] = ("DAY", null, null), ["W"] = ("DAY", null, "W"), ["M"] = ("DAY", null, "M"),
    };

    public IReadOnlyList<string> HistoryTimeframes { get; } = [.. Intervals.Keys];

    /// <summary>Which chart-service symbol answered for an instrument, kept for the life of the process (only indexes have two to try).</summary>
    private readonly ConcurrentDictionary<(string Exchange, string Symbol), string> chartSymbolCache = new();

    private static readonly string[] CandleTimeFormats = ["dd-MM-yyyy HH:mm", "dd-MM-yyyy", "d-M-yyyy HH:mm", "d-M-yyyy", "dd-MM-yyyy HH:mm:ss", "d-M-yyyy HH:mm:ss"];

    public async Task<JsonArray> GetHistoryAsync(BrokerSession session, SymbolRecord instrument, string interval, DateOnly start, DateOnly end, CancellationToken cancellationToken)
    {
        if (!Intervals.TryGetValue(interval, out var spec))
            throw new BrokerException($"Interval {interval} is not supported by {DisplayName}. Use one of: {string.Join(", ", Intervals.Keys)}.", 400);

        var startDate = start.ToDateTime(TimeOnly.MinValue);
        var now = IstNow();
        var endDate = end == DateOnly.FromDateTime(now) ? new DateTime(now.Year, now.Month, now.Day, now.Hour, now.Minute, 0) : end.ToDateTime(new TimeOnly(23, 59));
        var chunkDays = spec.ChartType == "DAY" ? 2000 : 31;
        var gap = sessions.HistoryGap(Name, TimeSpan.FromMilliseconds(150));
        var cacheKey = (instrument.Exchange, instrument.Symbol);
        var chartSymbol = chartSymbolCache.GetValueOrDefault(cacheKey);

        var rows = new List<JsonArray>();
        int chunkCount = 0, failedChunks = 0;
        string? lastError = null;
        var current = startDate;
        while (current <= endDate)
        {
            chunkCount++;
            var currentEnd = current.AddDays(chunkDays - 1) < endDate ? current.AddDays(chunkDays - 1) : endDate;
            var chunkStart = current.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            var chunkEnd = currentEnd.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            var candidates = chartSymbol is not null ? [chartSymbol] : ChartSymbols(instrument);

            // A transient failure never moves past a chunk: it is tried again, and only a chunk that never answers is a gap.
            List<JsonArray>? chunk = null;
            for (var attempt = 0; attempt < 4; attempt++)
            {
                try
                {
                    foreach (var candidate in candidates)
                    {
                        var fetched = await FetchCandlesAsync(session, instrument, spec.ChartType, chunkStart, chunkEnd, candidate, cancellationToken);
                        if (fetched.Count > 0)
                        {
                            if (chartSymbol != candidate)
                            {
                                chartSymbol = candidate;
                                chartSymbolCache[cacheKey] = candidate;
                            }

                            chunk = fetched;
                            break;
                        }

                        chunk = [];
                        if (candidates.Count > 1) await Task.Delay(gap, cancellationToken);
                    }

                    break;
                }
                catch (Exception ex) when (ex is ChartRefusal or BrokerException)
                {
                    lastError = ex.Message;
                    if (attempt < 3)
                    {
                        logger.LogWarning(ex, "{Broker} candle chunk {Start} to {End} for {Exchange}:{Symbol} failed; trying it again ({Attempt}/4).", DisplayName, chunkStart, chunkEnd, instrument.Exchange, instrument.Symbol, attempt + 1);
                        await Task.Delay(sessions.RetryBackoff(Name, TimeSpan.FromSeconds(0.5 * (1 << attempt))), cancellationToken);
                        continue;
                    }

                    logger.LogError(ex, "{Broker} candle chunk {Start} to {End} for {Exchange}:{Symbol} never answered.", DisplayName, chunkStart, chunkEnd, instrument.Exchange, instrument.Symbol);
                }
            }

            if (chunk is { Count: > 0 }) rows.AddRange(chunk);
            else if (chunk is null)
            {
                failedChunks++;
                logger.LogWarning("{Broker} candles for {Exchange}:{Symbol} from {Start} to {End} could not be fetched; the series has a gap.", DisplayName, instrument.Exchange, instrument.Symbol, chunkStart, chunkEnd);
            }

            current = currentEnd.AddDays(1);
            if (current <= endDate) await Task.Delay(gap, cancellationToken);
        }

        // Every chunk failing is no data at all: say so, rather than an empty series that reads as no trading.
        if (rows.Count == 0 && chunkCount > 0 && failedChunks == chunkCount)
            throw new BrokerException($"{DisplayName} could not return candles for {instrument.Exchange}:{instrument.Symbol} right now. Try again in a moment.", 500);

        var bars = new SortedDictionary<long, (double Open, double High, double Low, double Close, long Volume, long Oi)>();
        foreach (var row in rows)
        {
            if (row.Count < 7) continue;
            if (!DateTime.TryParseExact((Str(row[6]) ?? "").Trim(), CandleTimeFormats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var stamp)) continue;

            // Daily candles are stamped five and a half hours after their date's midnight UTC, intraday ones at the true epoch of their India time.
            var seconds = spec.ChartType == "DAY"
                ? new DateTimeOffset(stamp.Date, TimeSpan.Zero).ToUnixTimeSeconds() + 19800
                : new DateTimeOffset(stamp, Ist).ToUnixTimeSeconds();
            bars.TryAdd(seconds, (Flt(row[0]), Flt(row[1]), Flt(row[2]), Flt(row[3]), Whole(row[4]), Whole(row[5])));
        }

        IEnumerable<(long Timestamp, double Open, double High, double Low, double Close, long Volume, long Oi)> series = bars.Select(b => (b.Key, b.Value.Open, b.Value.High, b.Value.Low, b.Value.Close, b.Value.Volume, b.Value.Oi));
        if (spec.BinMinutes is not null || spec.Calendar is not null) series = Resample(series, spec);

        return new JsonArray(series.Select(b => (JsonNode?)new JsonObject
        {
            ["timestamp"] = b.Timestamp, ["open"] = b.Open, ["high"] = b.High, ["low"] = b.Low, ["close"] = b.Close, ["volume"] = b.Volume, ["oi"] = b.Oi,
        }).ToArray());
    }

    /// <summary>Candles built into coarser bins that start at midnight UTC (a Monday for weeks, the first for months); first open, highest high, lowest low, last close, summed volume, last open interest.</summary>
    private static IEnumerable<(long Timestamp, double Open, double High, double Low, double Close, long Volume, long Oi)> Resample(IEnumerable<(long Timestamp, double Open, double High, double Low, double Close, long Volume, long Oi)> bars, (string ChartType, int? BinMinutes, string? Calendar) spec)
    {
        static long BinStart(long timestamp, (string ChartType, int? BinMinutes, string? Calendar) spec)
        {
            if (spec.BinMinutes is { } minutes) return timestamp - (timestamp % (minutes * 60L));
            var day = DateTimeOffset.FromUnixTimeSeconds(timestamp).UtcDateTime.Date;
            var first = spec.Calendar == "M" ? new DateTime(day.Year, day.Month, 1, 0, 0, 0, DateTimeKind.Utc) : day.AddDays(-(((int)day.DayOfWeek + 6) % 7));
            return new DateTimeOffset(DateTime.SpecifyKind(first, DateTimeKind.Utc)).ToUnixTimeSeconds();
        }

        foreach (var group in bars.GroupBy(b => BinStart(b.Timestamp, spec)).OrderBy(g => g.Key))
        {
            var items = group.ToList();
            yield return (group.Key, items[0].Open, items.Max(i => i.High), items.Min(i => i.Low), items[^1].Close, items.Sum(i => i.Volume), items[^1].Oi);
        }
    }
}
