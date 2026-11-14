using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using EPTradeBot.Core.Brokers;
using static EPTradeBot.Infrastructure.Brokers.JsonRead;

namespace EPTradeBot.Infrastructure.Brokers.Kotak;

/// <summary>
/// Kotak history (broker/kotak api/data.py): GET /market-data/1.0/historical/details for NSE, BSE, NFO, BFO and their indices (MCX and currency are quote-only), in chunks of days that depend on the interval, a second apart.
/// An instrument is asked for by segment|token and an index also by its names (and their upper-case forms); the name that first answers with candles is kept for the rest of the pull. A range Kotak reports as empty
/// (a 400 fault saying no data) is no candles; a chunk that fails for every name fails the pull. Candles come as positional rows with an ISO time; a candle that breaks low &lt;= open, close &lt;= high is widened,
/// a negative volume is zero and a candle with a price that is not a number is dropped. A request for more than five years back starts at the earliest date Kotak serves.
/// </summary>
public sealed partial class KotakPlugin
{
    private static readonly Dictionary<string, string> HistoryIntervals = new()
    {
        ["1m"] = "1min", ["3m"] = "3min", ["5m"] = "5min", ["10m"] = "10min", ["15m"] = "15min", ["30m"] = "30min", ["1h"] = "60min", ["60m"] = "60min", ["D"] = "D", ["W"] = "W",
    };

    private static readonly Dictionary<string, int> ChunkDays = new() { ["1min"] = 30, ["3min"] = 30, ["5min"] = 30, ["10min"] = 60, ["15min"] = 60, ["30min"] = 90, ["60min"] = 90, ["D"] = 180, ["W"] = 180 };

    private static readonly HashSet<string> HistorySegments = ["nse_cm", "nse_fo", "bse_cm", "bse_fo"];

    private static readonly string[] NoDataMarkers = ["no data found", "data not available", "no data is available", "market has not yet opened"];

    public IReadOnlyList<string> HistoryTimeframes { get; } = ["1m", "3m", "5m", "10m", "15m", "30m", "1h", "D", "W"];

    /// <summary>Python's urlencode(safe='|'): letters, digits and _ . - ~ | stay, a space is a plus, every other byte is its percent form.</summary>
    private static string FormValue(string value)
    {
        var text = new StringBuilder();
        foreach (var b in Encoding.UTF8.GetBytes(value))
        {
            var c = (char)b;
            if (b < 128 && (char.IsAsciiLetterOrDigit(c) || c is '_' or '.' or '-' or '~' or '|')) text.Append(c);
            else if (c == ' ') text.Append('+');
            else text.Append('%').Append(b.ToString("X2", CultureInfo.InvariantCulture));
        }

        return text.ToString();
    }

    private sealed class ChunkFailed(string message) : Exception(message);

    private async Task<JsonArray> ChunkAsync(Neo neo, string neosymbol, string resolution, DateOnly from, DateOnly to, TimeSpan gap, bool first, CancellationToken cancellationToken)
    {
        var url = $"{neo.Base}/market-data/1.0/historical/details?neosymbol={FormValue(neosymbol)}&fromdate={from:yyyy-MM-dd}&todate={to:yyyy-MM-dd}&interval={FormValue(resolution)}";
        HttpResponseMessage response;
        for (var attempt = 0; ; attempt++)
        {
            if (!(first && attempt == 0)) await Task.Delay(gap, cancellationToken);
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.TryAddWithoutValidation("Authorization", neo.Access);
            try
            {
                response = await Client().SendAsync(request, cancellationToken);
            }
            catch (HttpRequestException ex)
            {
                throw new ChunkFailed(ex.Message);
            }

            if ((int)response.StatusCode != 429) break;
            var retryAfter = response.Headers.RetryAfter?.Delta;
            response.Dispose();
            if (attempt == 4) throw new ChunkFailed("rate limited");
            await Task.Delay(sessions.RetryBackoff(Name, retryAfter ?? TimeSpan.FromSeconds(Math.Pow(2, attempt))), cancellationToken);
        }

        using (response)
        {
            if (await ReadAsync(response, cancellationToken) is not JsonObject payload) throw new ChunkFailed("unexpected payload");
            var fault = payload["fault"] as JsonObject;
            var message = Truthy(fault?["message"]) ? Str(fault!["message"]) ?? "" : Truthy(payload["emsg"]) ? Str(payload["emsg"]) ?? "" : "";
            if (NoDataMarkers.Any(m => message.Contains(m, StringComparison.OrdinalIgnoreCase))) return [];
            if ((int)response.StatusCode != 200) throw new ChunkFailed(message);
            if (!string.Equals(Str(payload["status"]) ?? "", "success", StringComparison.OrdinalIgnoreCase)) throw new ChunkFailed(message);
            return (payload["data"] as JsonObject)?["candles"] as JsonArray ?? [];
        }
    }

    private static double? Coerced(JsonNode? node)
    {
        if (BookFormatting.Number(node) is { } n) return n;
        return Str(node) is { } text && double.TryParse(text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : null;
    }

    public async Task<JsonArray> GetHistoryAsync(BrokerSession session, SymbolRecord instrument, string interval, DateOnly start, DateOnly end, CancellationToken cancellationToken)
    {
        if (!HistoryIntervals.TryGetValue(interval, out var resolution)) throw new BrokerException($"Interval {interval} is not supported by {Display}. Use one of: {string.Join(", ", HistoryTimeframes)}.", 400);
        var segment = QuerySegment(instrument.BrokerExchange);
        if (!HistorySegments.Contains(segment)) throw new BrokerException($"{Display} serves historical data for NSE, BSE, NFO, BFO, NSE_INDEX and BSE_INDEX only. {instrument.Exchange} is quote-only.", 400);
        var neo = Parse(session);

        var candidates = new List<string>();
        if (!string.IsNullOrEmpty(instrument.Token)) candidates.Add($"{segment}|{instrument.Token}");
        if (instrument.Exchange.Contains("INDEX", StringComparison.OrdinalIgnoreCase))
        {
            foreach (var name in IndexCandidates(instrument.Symbol))
            {
                foreach (var variant in new[] { name, name.ToUpperInvariant() })
                {
                    var key = $"{segment}|{variant}";
                    if (!candidates.Contains(key)) candidates.Add(key);
                }
            }
        }

        if (start > end) throw new BrokerException($"The start date {start:yyyy-MM-dd} is after the end date {end:yyyy-MM-dd}. Correct the dates, then try again.", 400);
        var today = DateOnly.FromDateTime(clock.GetUtcNow().ToOffset(TimeSpan.FromHours(5.5)).DateTime);
        var earliest = today.AddYears(-5).AddDays(1);
        if (end < earliest) return [];
        if (start < earliest) start = earliest;

        var gap = sessions.HistoryGap(Name, TimeSpan.FromSeconds(1));
        var chunkDays = ChunkDays[resolution];
        string? resolved = null;
        var rows = new List<JsonArray>();
        var firstCall = true;
        for (var from = start; from <= end;)
        {
            var to = from.AddDays(chunkDays - 1) < end ? from.AddDays(chunkDays - 1) : end;
            JsonArray? candles = null;
            foreach (var neosymbol in resolved is null ? candidates : [resolved])
            {
                try
                {
                    var result = await ChunkAsync(neo, neosymbol, resolution, from, to, gap, firstCall, cancellationToken);
                    firstCall = false;
                    candles = result;
                    if (result.Count > 0)
                    {
                        resolved = neosymbol;
                        break;
                    }
                }
                catch (ChunkFailed)
                {
                    firstCall = false;
                }
            }

            if (candles is null) throw new BrokerException($"{Display} could not return candles for {instrument.Exchange}:{instrument.Symbol} right now. Try again in a moment.", 500);
            if (candles.Count > 0) rows.Add(candles);
            from = to.AddDays(1);
        }

        var all = new List<(long Stamp, double O, double H, double L, double C, long V, long Oi)>();
        foreach (var candle in rows.SelectMany(r => r))
        {
            if (candle is not JsonArray row || row.Count < 5) continue;
            JsonNode? At(int i) => i < row.Count ? row[i] : JsonValue.Create(0);
            if (!DateTimeOffset.TryParse(Str(row[0]) ?? "", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var moment)) throw new BrokerException($"{Display} sent a candle time that cannot be read. Try again in a moment.", 500);
            var stamp = moment.ToUnixTimeSeconds();
            if (resolution is "D" or "W") stamp = (stamp + 19800) / 86400 * 86400;
            if (Coerced(At(1)) is not { } o || Coerced(At(2)) is not { } h || Coerced(At(3)) is not { } l || Coerced(At(4)) is not { } c) continue;
            var volume = Coerced(At(5)) is { } v && double.IsFinite(v) ? (long)Math.Truncate(v) : 0;
            var oi = Coerced(At(6)) is { } i && double.IsFinite(i) ? (long)Math.Truncate(i) : 0;
            all.Add((stamp, o, Math.Max(Math.Max(o, h), Math.Max(l, c)), Math.Min(Math.Min(o, h), Math.Min(l, c)), c, volume < 0 ? 0 : volume, oi));
        }

        var result2 = new JsonArray();
        foreach (var group in all.Select((c, index) => (c, index)).OrderBy(x => x.c.Stamp).ThenBy(x => x.index).GroupBy(x => x.c.Stamp))
        {
            var k = group.First().c;
            result2.Add(new JsonObject { ["timestamp"] = k.Stamp, ["open"] = k.O, ["high"] = k.H, ["low"] = k.L, ["close"] = k.C, ["volume"] = k.V, ["oi"] = k.Oi });
        }

        return result2;
    }
}
