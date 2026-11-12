using System.Globalization;
using System.Text.Json.Nodes;
using EPTradeBot.Core.Calendar;
using EPTradeBot.Core.Time;

namespace EPTradeBot.Core.Flow;

/// <summary>
/// Nodes that read candles and shape them: the history itself (never more than the latest 200 bars), the closed bar N back, the previous hour, day, week or month, and the calendar facts for a date.
/// Dates and "now" are Indian time. OpenAlgo's execute_history, execute_bar_offset, execute_prior_period_ohlc and execute_calendar.
/// </summary>
public sealed class HistoryNodes : IFlowNode
{
    public const int MaxBars = 200;
    public const int MaxCalendarDays = 4000;

    private static readonly Dictionary<string, double> BarsPerDay = new(StringComparer.Ordinal)
    {
        ["1m"] = 375, ["3m"] = 125, ["5m"] = 75, ["10m"] = 38, ["15m"] = 25, ["30m"] = 13, ["1h"] = 7, ["60m"] = 7, ["2h"] = 4, ["3h"] = 3, ["4h"] = 2,
        ["d"] = 1, ["1d"] = 1, ["day"] = 1, ["w"] = 0.2, ["week"] = 0.2, ["m"] = 0.05, ["month"] = 0.05, ["q"] = 0.016, ["quarter"] = 0.016, ["y"] = 0.004, ["year"] = 0.004,
    };

    private static readonly Dictionary<string, int> IntervalMinutes = new(StringComparer.Ordinal)
    {
        ["1m"] = 1, ["3m"] = 3, ["5m"] = 5, ["10m"] = 10, ["15m"] = 15, ["30m"] = 30, ["1h"] = 60, ["60m"] = 60, ["2h"] = 120, ["3h"] = 180, ["4h"] = 240,
    };

    public IReadOnlyList<string> Types => ["history", "barOffset", "priorPeriodOhlc", "calendar"];

    private static string Iso(DateOnly d) => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    /// <summary>The calendar window that holds this many bars at an interval: the 1.6 allowance for weekends and holidays, capped.</summary>
    public static (DateOnly Start, DateOnly End) WindowForBars(int bars, string interval, DateOnly end)
    {
        bars = Math.Max(Math.Min(bars, MaxBars), 1);
        var perDay = BarsPerDay.GetValueOrDefault(interval.ToLowerInvariant(), 75);
        var days = Math.Min((int)(bars / perDay * 1.6) + 5, MaxCalendarDays);
        return (end.AddDays(-days), end);
    }

    /// <summary>Moves a literal range's start forward so it spans at most 200 bars; the end, and the recent data, stay.</summary>
    public static (string Start, string End) ClampRange(string start, string end, string interval)
    {
        var s = start.Length > 10 ? start[..10] : start;
        var e = end.Length > 10 ? end[..10] : end;
        if (!DateOnly.TryParseExact(s, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var requested) ||
            !DateOnly.TryParseExact(e, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var last)) return (start, end);
        var (floor, _) = WindowForBars(MaxBars, interval, last);
        return requested < floor ? (Iso(floor), Iso(last)) : (start, end);
    }

    private static List<JsonObject> Trim(JsonArray? data)
    {
        var rows = data?.OfType<JsonObject>().ToList() ?? [];
        return rows.Count > MaxBars ? rows[^MaxBars..] : rows;
    }

    /// <summary>A candle's time as Indian wall clock: epoch seconds, or text.</summary>
    public static DateTime ParseTimestamp(JsonNode? value)
    {
        if (value is JsonValue v)
        {
            if (v.TryGetValue<double>(out var seconds) || v.TryGetValue<long>(out var l) && (seconds = l) == l) return DateTimeOffset.FromUnixTimeSeconds((long)seconds).UtcDateTime + Ist.Offset;
            if (v.TryGetValue<string>(out var text))
            {
                if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var epoch)) return DateTimeOffset.FromUnixTimeSeconds((long)epoch).UtcDateTime + Ist.Offset;
                if (DateTimeOffset.TryParse(text.Replace("Z", "+00:00"), CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var parsed))
                    return text.Contains('+') || text.EndsWith('Z') || text.LastIndexOf('-') > 9 ? parsed.UtcDateTime + Ist.Offset : parsed.DateTime;
            }
        }

        throw new FormatException($"Invalid isoformat string: '{FlowContext.PyStr(value)}'");
    }

    private static List<JsonObject> DropFormingBar(List<JsonObject> records, string interval, DateTime now)
    {
        if (records.Count == 0) return records;
        var last = ParseTimestamp(records[^1]["timestamp"]);
        var key = interval.Trim().ToLowerInvariant();
        if (key is "d" or "1d" or "day" or "w" or "week" or "m" or "month") return DateOnly.FromDateTime(last) == DateOnly.FromDateTime(now) ? records[..^1] : records;
        if (IntervalMinutes.TryGetValue(key, out var minutes) && now - last < TimeSpan.FromMinutes(minutes)) return records[..^1];
        return records;
    }

    public async Task<FlowNodeResult?> ExecuteAsync(FlowNode node, FlowRun run, CancellationToken ct)
    {
        var host = ApiNodeSupport.HostOf(run);
        var ctx = run.Context;
        var now = run.UtcNow() + Ist.Offset;
        var today = DateOnly.FromDateTime(now);
        switch (node.Type)
        {
            case "history": return await HistoryAsync(node, run, host, today, ct);
            case "calendar": return await CalendarAsync(node, run, host, today, ct);
        }

        var symbol = ctx.Interpolate(node.Data.Str(ctx, "symbol", ""));
        var exchange = node.Data.Str(ctx, "exchange", "NSE");
        var source = node.Data.Str(ctx, "source", "api");
        JsonObject Fail(string message) => new() { ["status"] = "error", ["message"] = message };
        if (node.Type == "barOffset")
        {
            var interval = node.Data.Str(ctx, "interval", "D");
            var offset = Math.Max(Math.Min(MarketInfoNodes.Int(node, ctx, "offsetBars", 0) + 1, MaxBars), 1) - 1;
            if (symbol.Length == 0) return FlowApiResults.ToResult(Fail("Symbol is required"));
            var (start, end) = WindowForBars((int)offset + 5, interval, today);
            run.Log($"Fetching bar offset {offset} for {symbol} ({interval})");
            var fetched = await FetchAsync(host, symbol, exchange, interval, Iso(start), Iso(end), source, ct);
            if (HistoryError(fetched, symbol, interval, source) is { } problem) { run.Log(problem, "error"); return FlowApiResults.ToResult(Fail(problem)); }
            var closed = DropFormingBar(Trim(fetched["data"] as JsonArray), interval, now);
            var index = closed.Count - 1 - (int)offset;
            if (index < 0) return FlowApiResults.ToResult(Fail($"Not enough history to reach {offset} bars back for {symbol}"));
            var row = closed[index];
            var output = new JsonObject
            {
                ["status"] = "success", ["symbol"] = symbol, ["exchange"] = exchange, ["offsetBars"] = offset, ["timestamp"] = row["timestamp"]?.DeepClone(), ["open"] = row["open"]?.DeepClone(),
                ["high"] = row["high"]?.DeepClone(), ["low"] = row["low"]?.DeepClone(), ["close"] = row["close"]?.DeepClone(), ["volume"] = row["volume"]?.DeepClone(),
            };
            run.Log($"Bar offset {offset} for {symbol}: {output.ToJsonString()}");
            ApiNodeSupport.Store(node, run, output);
            return FlowApiResults.ToResult(output);
        }

        var period = node.Data.Str(ctx, "period", "previous_day");
        if (symbol.Length == 0) return FlowApiResults.ToResult(Fail("Symbol is required"));
        var candle = period == "previous_hour" ? "1h" : "D";
        var lookback = period == "previous_hour" ? 5 : period == "previous_month" ? 60 : period == "previous_week" ? 21 : 10;
        var from = Iso(today.AddDays(-lookback));
        run.Log($"Fetching {period} OHLC for: {symbol} ({exchange})");
        var result = await FetchAsync(host, symbol, exchange, candle, from, Iso(today), source, ct);
        if (HistoryError(result, symbol, candle, source) is { } error) { run.Log(error, "error"); return FlowApiResults.ToResult(Fail(error)); }
        JsonObject prior;
        try
        {
            prior = PriorPeriod(Trim(result["data"] as JsonArray), period, now);
        }
        catch (ArgumentException e)
        {
            run.Log($"Prior period OHLC error: {e.Message}", "error");
            return FlowApiResults.ToResult(Fail(e.Message));
        }

        var shaped = new JsonObject { ["status"] = "success", ["symbol"] = symbol, ["exchange"] = exchange };
        foreach (var (k, v) in prior) shaped[k] = v?.DeepClone();
        shaped["pdh"] = prior["high"]?.DeepClone();
        shaped["pdl"] = prior["low"]?.DeepClone();
        shaped["pdc"] = prior["close"]?.DeepClone();
        run.Log($"{period} OHLC for {symbol}: H={prior["high"]?.ToJsonString()} L={prior["low"]?.ToJsonString()} C={prior["close"]?.ToJsonString()} (bucket={prior["date"]})");
        ApiNodeSupport.Store(node, run, shaped);
        return FlowApiResults.ToResult(shaped);
    }

    private static async Task<JsonObject> FetchAsync(FlowHost host, string symbol, string exchange, string interval, string start, string end, string source, CancellationToken ct) =>
        await host.Api.CallAsync("history", new JsonObject { ["symbol"] = symbol, ["exchange"] = exchange, ["interval"] = interval, ["source"] = source, ["start_date"] = start, ["end_date"] = end }, ct);

    private static string? HistoryError(JsonObject response, string symbol, string interval, string source)
    {
        if (FlowApiResults.IsSuccess(response) && !response.ContainsKey("error")) return null;
        var detail = FlowApiResults.Reason(response, "unknown error");
        var hint = source == "db" ? "" : $" If your broker does not support the '{interval}' interval, use the Intervals node to list supported ones, or set Source to Historify DB to resample it locally.";
        return $"History fetch failed for {symbol} ({interval}): {detail}.{hint}";
    }

    private async Task<FlowNodeResult?> HistoryAsync(FlowNode node, FlowRun run, FlowHost host, DateOnly today, CancellationToken ct)
    {
        var ctx = run.Context;
        var symbol = node.Data.Str(ctx, "symbol", "");
        var exchange = node.Data.Str(ctx, "exchange", "NSE");
        var interval = node.Data.Str(ctx, "interval", "5m");
        var start = node.Data.Str(ctx, "startDate", "");
        var end = node.Data.Str(ctx, "endDate", "");
        if (start.Length == 0 || end.Length == 0)
        {
            var days = MarketInfoNodes.Int(node, ctx, "days", 0);
            if (days > 0)
            {
                start = Iso(today.AddDays(-(int)days));
                end = Iso(today);
                run.Log($"History range from days={days}: {start} to {end}");
            }
        }

        if (start.Length > 0 && end.Length > 0) (start, end) = ClampRange(start, end, interval);
        run.Log($"Getting history for: {symbol} ({interval})");
        var body = new JsonObject { ["symbol"] = symbol, ["exchange"] = exchange, ["interval"] = interval, ["start_date"] = start, ["end_date"] = end };
        var result = await host.Api.CallAsync("history", body, ct);
        if (result["data"] is JsonArray { Count: > 0 } data)
        {
            var trimmed = new JsonArray();
            foreach (var row in Trim(data)) trimmed.Add(row.DeepClone());
            result["data"] = trimmed;
        }

        run.Log("History data received");
        ApiNodeSupport.Store(node, run, result);
        return FlowApiResults.ToResult(result);
    }

    private async Task<FlowNodeResult?> CalendarAsync(FlowNode node, FlowRun run, FlowHost host, DateOnly today, CancellationToken ct)
    {
        var raw = node.Data.Str(run.Context, "date", "");
        DateOnly day;
        if (raw.Length > 0)
        {
            if (!DateOnly.TryParseExact(raw.Length > 10 ? raw[..10] : raw, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out day))
            {
                var message = $"Calendar date must be YYYY-MM-DD, got '{raw}'";
                run.Log($"Calendar failed: {message}", "error");
                return FlowApiResults.ToResult(new JsonObject { ["status"] = "error", ["message"] = message });
            }
        }
        else
        {
            // The trading session's date: between midnight and the 03:00 rollover the calendar date is still yesterday's session.
            day = DateOnly.FromDateTime(run.Context.IstNow.AddHours(-3));
        }

        var years = new Dictionary<int, (IReadOnlySet<DateOnly>, IReadOnlySet<DateOnly>)>();
        foreach (var year in new[] { day.Year - 1, day.Year, day.Year + 1 })
            years[year] = TradingCalendar.FromHolidays(await host.Api.CallAsync("holidays", new JsonObject { ["year"] = year }, ct));
        var info = new TradingCalendar(years).Describe(day);
        var result = new JsonObject { ["status"] = "success" };
        foreach (var (k, v) in info) result[k] = v?.DeepClone();
        run.Log($"Calendar {info["date"]} ({info["weekday"]}): trading={(info["is_trading_day"]!.GetValue<bool>() ? "True" : "False")} newWeek={(info["is_new_week"]!.GetValue<bool>() ? "True" : "False")} newMonth={(info["is_new_month"]!.GetValue<bool>() ? "True" : "False")} newQuarter={(info["is_new_quarter"]!.GetValue<bool>() ? "True" : "False")}");
        ApiNodeSupport.Store(node, run, result);
        return FlowApiResults.ToResult(result);
    }

    /// <summary>The previous full hour, day, week or month from candles; throws when there is not enough history to say.</summary>
    public static JsonObject PriorPeriod(List<JsonObject> records, string period, DateTime now)
    {
        if (records.Count == 0) throw new ArgumentException("no historical data available for this symbol/date range");
        static JsonObject Row(string period, string date, JsonObject r) => new()
        {
            ["period"] = period, ["date"] = date, ["open"] = r["open"]?.DeepClone(), ["high"] = r["high"]?.DeepClone(), ["low"] = r["low"]?.DeepClone(), ["close"] = r["close"]?.DeepClone(), ["volume"] = r["volume"]?.DeepClone(),
        };
        if (period == "previous_hour")
        {
            var bucket = now.ToString("yyyy-MM-dd HH", CultureInfo.InvariantCulture);
            var closed = records.Where(r => ParseTimestamp(r["timestamp"]).ToString("yyyy-MM-dd HH", CultureInfo.InvariantCulture) != bucket).ToList();
            if (closed.Count == 0) throw new ArgumentException("not enough history to determine previous_hour");
            return Row(period, ParseTimestamp(closed[^1]["timestamp"]).ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture), closed[^1]);
        }

        if (period == "previous_day")
        {
            var todayText = now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            var closed = records.Where(r => ParseTimestamp(r["timestamp"]).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) != todayText).ToList();
            if (closed.Count == 0) throw new ArgumentException("not enough history to determine previous_day");
            return Row(period, ParseTimestamp(closed[^1]["timestamp"]).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), closed[^1]);
        }

        if (period is "previous_week" or "previous_month")
        {
            var weekly = period == "previous_week";
            static DateOnly WeekStart(DateTime t) => DateOnly.FromDateTime(t).AddDays(-(((int)t.DayOfWeek + 6) % 7));
            DateOnly Bucket(DateTime t) => weekly ? WeekStart(t) : new DateOnly(t.Year, t.Month, 1);
            var current = Bucket(now);
            var dated = records.Select(r => (Row: r, Time: ParseTimestamp(r["timestamp"]))).Select(x => (x.Row, x.Time, Bucket: Bucket(x.Time))).Where(x => x.Bucket < current).ToList();
            if (dated.Count == 0) throw new ArgumentException($"not enough history to determine {period}");
            var last = dated.Max(x => x.Bucket);
            var rows = dated.Where(x => x.Bucket == last).OrderBy(x => x.Time).ToList();
            double N(JsonObject r, string key) => r[key] is JsonValue v && FlowData.TryParseOrNumber(v, out var d) ? d : double.NaN;
            var label = weekly ? $"{last:yyyy-MM-dd}/{last.AddDays(6):yyyy-MM-dd}" : $"{last:yyyy-MM}";
            return new JsonObject
            {
                ["period"] = period, ["date"] = label, ["open"] = N(rows[0].Row, "open"), ["high"] = rows.Max(x => N(x.Row, "high")), ["low"] = rows.Min(x => N(x.Row, "low")), ["close"] = N(rows[^1].Row, "close"),
                ["volume"] = rows.Any(x => x.Row.ContainsKey("volume")) ? rows.Sum(x => N(x.Row, "volume")) : null,
            };
        }

        throw new ArgumentException($"unknown period '{period}' \u2014 expected previous_hour/previous_day/previous_week/previous_month");
    }
}
