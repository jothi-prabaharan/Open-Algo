using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using EPTradeBot.Core.Indicators;

namespace EPTradeBot.Core.Flow;

/// <summary>
/// The indicator node: any indicator over a symbol's history, or nested over another indicator node's series when <c>sourceSeries</c> is set. Port of OpenAlgo's
/// execute_indicator and compute_indicator: bounded history, extra warm-up rows for recursive indicators, the latest and previous values, a fixed-length tail, and a
/// value N bars back; an indicator that produces nothing usable is an error, never a quiet null.
/// </summary>
public sealed class IndicatorNode : IFlowNode
{
    /// <summary>The most bars a flow ever holds or asks for.</summary>
    public const int MaxBars = 200;

    public const int MaxCalendarDays = 4000;

    public const int WarmupBars = 50;

    private static readonly string[] TwoSeries = ["correlation", "beta", "crossover", "crossunder", "cross", "exrem", "flip", "valuewhen"];

    private static readonly string[] Unsupported = ["median_bands", "ulcerindex", "vi"];

    private static readonly Dictionary<string, double> BarsPerDay = new(StringComparer.Ordinal)
    {
        ["1m"] = 375, ["3m"] = 125, ["5m"] = 75, ["10m"] = 38, ["15m"] = 25, ["30m"] = 13, ["1h"] = 7, ["60m"] = 7, ["2h"] = 4, ["3h"] = 3, ["4h"] = 2,
        ["d"] = 1, ["1d"] = 1, ["day"] = 1, ["w"] = 0.2, ["week"] = 0.2, ["m"] = 0.05, ["month"] = 0.05,
    };

    public IReadOnlyList<string> Types => ["indicator"];

    public static int ClampBars(long requested) => requested > MaxBars ? MaxBars : (int)Math.Max(requested, 1);

    /// <summary>The calendar span that should hold this many bars: the trading-day allowance of 1.6 and a few days spare, capped.</summary>
    public static int CalendarDays(int bars, string interval)
    {
        var perDay = BarsPerDay.GetValueOrDefault(interval.ToLowerInvariant(), 75);
        return Math.Min((int)(bars / perDay * 1.6) + 5, MaxCalendarDays);
    }

    public async Task<FlowNodeResult?> ExecuteAsync(FlowNode node, FlowRun run, CancellationToken ct)
    {
        var ctx = run.Context;
        var name = node.Data.Str(ctx, "indicatorName", "sma");
        var lookback = (int)Int(node, ctx, "lookbackBars", 100);
        var tail = (int)Int(node, ctx, "tailBars", 5);
        var offsetBars = (int)Int(node, ctx, "offsetBars", 0);
        var sourceField = node.Data.Str(ctx, "sourceField", "");

        JsonObject parameters;
        if (node.Data["params"] is JsonObject asObject)
        {
            run.Log("Indicator params were stored as an object; normalising to the JSON string form. Re-save the node to store it correctly.", "warning");
            parameters = asObject;
        }
        else
        {
            var raw = node.Data.Str(ctx, "params", "{}");
            try
            {
                parameters = raw.Trim().Length == 0 ? [] : JsonNode.Parse(raw) as JsonObject ?? throw new JsonException();
            }
            catch (JsonException)
            {
                // Not an object: the text, or the kind of thing it parsed to.
                var parsed = TryKind(raw);
                return parsed is null ? FlowNodeResult.Fail($"Invalid params JSON: {raw}") : FlowNodeResult.Fail($"params must resolve to a JSON object, got {parsed}");
            }
        }

        if (node.Data["sourceSeries"] is { } sourceRaw && !(sourceRaw is JsonValue sv && sv.TryGetValue<string>(out var st) && st.Length == 0))
            return Nested(node, run, name, tail, offsetBars, sourceField, parameters, sourceRaw);

        var host = ApiNodeSupport.HostOf(run);
        var symbol = ctx.Interpolate(node.Data.Str(ctx, "symbol", ""));
        var exchange = node.Data.Str(ctx, "exchange", "NSE");
        var interval = node.Data.Str(ctx, "interval", "D");
        var source = node.Data.Str(ctx, "source", "api");
        if (symbol.Length == 0) return FlowNodeResult.Fail("Symbol is required");

        var bars = ClampBars(lookback);
        var end = DateOnly.FromDateTime(ctx.IstNow);
        var start = end.AddDays(-CalendarDays(bars, interval));
        run.Log($"Computing {name} for {symbol} ({interval}, {lookback} bars)");
        var history = await host.Api.CallAsync("history", new JsonObject
        {
            ["symbol"] = symbol, ["exchange"] = exchange, ["interval"] = interval, ["source"] = source,
            ["start_date"] = start.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), ["end_date"] = end.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        }, ct);
        if (!FlowApiResults.IsSuccess(history))
        {
            var detail = FlowApiResults.Reason(history, "unknown error");
            var hint = source == "db" ? "" : $" If your broker does not support the '{interval}' interval, use the Intervals node to list supported ones, or set Source to Historify DB to resample it locally.";
            var message = $"History fetch failed for {symbol} ({interval}): {detail}.{hint}";
            run.Log(message, "error");
            return FlowNodeResult.Fail(message);
        }

        var records = (history["data"] as JsonArray)?.OfType<JsonObject>().ToList() ?? [];
        if (records.Count > MaxBars) records = records[^MaxBars..];
        try
        {
            var output = Compute(records, name, parameters, bars, tail, null, offsetBars);
            return Finish(node, run, name, output);
        }
        catch (ArgumentException ex)
        {
            run.Log($"Indicator error: {ex.Message}", "error");
            return FlowNodeResult.Fail(ex.Message);
        }
    }

    private static string? TryKind(string raw)
    {
        try
        {
            return JsonNode.Parse(raw) switch { JsonArray => "list", JsonValue v when v.TryGetValue<string>(out _) => "str", JsonValue v when v.TryGetValue<bool>(out _) => "bool", JsonValue => "float", _ => "NoneType" };
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static long Int(FlowNode node, FlowContext ctx, string key, long fallback)
    {
        var v = node.Data[key];
        if (v is null || v is JsonValue e && e.TryGetValue<string>(out var es) && es.Length == 0) return fallback;
        if (v is JsonValue s && s.TryGetValue<string>(out var text))
        {
            return FlowData.TryParseFloat(ctx.Interpolate(text), out var d) ? (long)d : fallback;
        }

        return (long)FlowData.ToDouble(v);
    }

    private FlowNodeResult? Nested(FlowNode node, FlowRun run, string name, int tail, int offsetBars, string sourceField, JsonObject parameters, JsonNode sourceRaw)
    {
        var ctx = run.Context;
        // One whole {{token}} resolves to the stored list itself; stringifying it would lose its shape.
        var list = sourceRaw is JsonValue rv && rv.TryGetValue<string>(out var rt) ? ctx.ResolveRaw(rt) : sourceRaw;
        if (list is JsonValue lv && lv.TryGetValue<string>(out var asText))
        {
            try { list = JsonNode.Parse(asText); }
            catch (JsonException)
            {
                run.Log($"sourceSeries did not resolve to an array: '{asText}'", "error");
                return FlowNodeResult.Fail("sourceSeries did not resolve to a JSON array");
            }
        }

        if (list is not JsonArray array)
        {
            run.Log("sourceSeries must resolve to an array", "error");
            return FlowNodeResult.Fail("sourceSeries must resolve to an array");
        }

        var numbers = new List<double>();
        foreach (var item in array)
        {
            JsonNode? value;
            if (item is JsonObject o)
            {
                // value or out0 come from another indicator node; close (or a named field) lets a raw history array be fed in.
                value = sourceField.Length > 0 ? o[sourceField] : new[] { "value", "out0", "close" }.Select(k => o[k]).FirstOrDefault(x => x is not null);
            }
            else value = item;

            if (value is null) continue;
            try
            {
                numbers.Add(FlowData.ToDouble(value));
            }
            catch (FormatException)
            {
                var message = $"sourceSeries contains a non-numeric value: '{FlowContext.PyStr(value)}'";
                run.Log(message, "error");
                return FlowNodeResult.Fail(message);
            }
        }

        run.Log($"Computing nested {name} over {numbers.Count} upstream values");
        try
        {
            return Finish(node, run, name, Compute([], name, parameters, 100, tail, numbers, offsetBars), nested: true);
        }
        catch (ArgumentException ex)
        {
            run.Log($"Indicator error: {ex.Message}", "error");
            return FlowNodeResult.Fail(ex.Message);
        }
    }

    private static FlowNodeResult Finish(FlowNode node, FlowRun run, string name, JsonObject output, bool nested = false)
    {
        if (output["status"]?.GetValue<string>() == "error")
        {
            run.Log($"Indicator error: {output["message"]}", "error");
            return new FlowNodeResult { Status = FlowNodeResult.Error, Message = output["message"]?.GetValue<string>(), Values = output };
        }

        run.Log($"{name}{(nested ? " (nested)" : "")} latest: {FlowContext.PyStr(output["latest"])}");
        ApiNodeSupport.Store(node, run, output);
        return FlowNodeResult.Ok(output);
    }

    /// <summary>One indicator over records (or a bare series). Throws ArgumentException for an indicator that cannot run, which the node reports.</summary>
    public static JsonObject Compute(IReadOnlyList<JsonObject> records, string indicatorName, JsonObject parameters, int lookbackBars, int tailBars, IReadOnlyList<double>? sourceSeries, int offsetBars)
    {
        var name = indicatorName.Trim().ToLowerInvariant();
        if (TwoSeries.Contains(name))
            throw new ArgumentException($"'{name}' needs two independent series (a second symbol or two already-computed signals) and cannot run from a single symbol's history. Use two Indicator nodes plus a Math Expression/Var Condition node instead.");
        if (Unsupported.Contains(name))
            throw new ArgumentException($"'{name}' is not available: the installed openalgo build does not return usable values for it. Pick a different indicator.");
        if (!IndicatorRegistry.All.TryGetValue(name, out var fn) || !IndicatorInputs.All.TryGetValue(name, out var inputs))
            throw new ArgumentException($"unknown indicator '{indicatorName}'");

        var resolved = new Dictionary<string, object>(StringComparer.Ordinal);
        foreach (var (k, v) in parameters) resolved[k] = Plain(v);

        string[] columns;
        var series = new Dictionary<string, double[]>(StringComparer.Ordinal);
        int count;
        if (sourceSeries is not null)
        {
            if (!(inputs.Length == 1 && inputs[0].Series == "data"))
                throw new ArgumentException($"'{name}' takes more than one independent input series (e.g. high/low/close), so it cannot be nested on top of another indicator's single output series. Nest only single-series indicators (SMA, EMA, RSI, WMA, stdev, highest/lowest, ...).");
            if (sourceSeries.Count < 2) throw new ArgumentException("nested indicator needs at least 2 upstream values");
            series["data"] = [.. sourceSeries];
            columns = ["data (nested)"];
            count = sourceSeries.Count;
        }
        else
        {
            if (records.Count == 0) throw new ArgumentException("no historical data available for this symbol/date range");
            var take = Math.Min(ClampBars(lookbackBars) + WarmupBars, MaxBars);
            var rows = records.Count > take ? records.Skip(records.Count - take).ToList() : records.ToList();
            columns = [.. inputs.Select(i => i.Column)];
            foreach (var (param, column) in inputs)
            {
                if (!rows.Any(r => r.ContainsKey(column))) throw new ArgumentException($"history has no '{column}' column, which {name} needs");
                series[param] = [.. rows.Select(r => r[column] is JsonValue v && FlowData.TryParseOrNumber(v, out var d) ? d : double.NaN)];
            }

            count = rows.Count;
        }

        var outputs = fn(new IndicatorArgs(series, resolved));
        var names = outputs.Length == 1 ? new[] { "value" } : [.. Enumerable.Range(0, outputs.Length).Select(i => $"out{i}")];

        // The n-th value from the end of one output, skipping the bars it has no value for; null when there are fewer.
        double? At(int column, int fromEnd)
        {
            var valid = outputs[column].Where(x => !double.IsNaN(x)).ToArray();
            var i = fromEnd < 0 ? valid.Length + fromEnd : fromEnd;
            return i >= 0 && i < valid.Length ? Math.Round(valid[i], 6, MidpointRounding.ToEven) : null;
        }

        JsonObject Row(int fromEnd)
        {
            var row = new JsonObject();
            for (var c = 0; c < outputs.Length; c++) row[names[c]] = At(c, fromEnd);
            return row;
        }

        var n = ClampBars(tailBars);
        var tailSeries = new JsonArray();
        for (var off = -n; off < 0; off++) tailSeries.Add(Row(off));
        var offset = ClampBars(offsetBars + 1L) - 1;
        var latest = Row(-1);
        var resolvedParams = new JsonObject();
        foreach (var (k, v) in resolved) resolvedParams[k] = JsonValue.Create(v);

        if (latest.Count > 0 && latest.All(kv => kv.Value is null))
        {
            return new JsonObject
            {
                ["status"] = "error", ["indicator"] = name,
                ["message"] = $"Indicator '{name}' produced no value over {count} bars - every output was null. Increase the lookback if the indicator needs a longer warm-up, or check that this indicator is usable with the selected inputs.",
                ["inputs"] = new JsonArray([.. columns.Select(c => (JsonNode?)c)]), ["params"] = resolvedParams, ["outputs"] = new JsonArray([.. names.Select(c => (JsonNode?)c)]), ["bars_used"] = count,
            };
        }

        return new JsonObject
        {
            ["status"] = "success", ["indicator"] = name, ["nested"] = sourceSeries is not null, ["offset_bars"] = offset, ["at_offset"] = Row(-1 - offset),
            ["inputs"] = new JsonArray([.. columns.Select(c => (JsonNode?)c)]), ["params"] = resolvedParams, ["outputs"] = new JsonArray([.. names.Select(c => (JsonNode?)c)]),
            ["latest"] = latest, ["previous"] = Row(-2), ["series"] = tailSeries, ["bars_used"] = count,
        };
    }

    private static object Plain(JsonNode? v) => v switch
    {
        JsonValue j when j.TryGetValue<bool>(out var b) => b,
        JsonValue j when j.TryGetValue<string>(out var s) => s,
        JsonValue j when FlowData.TryNumber(j, out var d) => d == Math.Floor(d) && Math.Abs(d) < 1e15 ? (long)d : d,
        _ => v?.ToJsonString() ?? "",
    };
}
