using System.Text.Json.Nodes;
using EPTradeBot.Core.Flow;
using EPTradeBot.Core.Time;

namespace EPTradeBot.Core.Tests;

public sealed class FlowIndicatorNodeTests
{
    private static readonly JsonObject Fixtures = JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "indicators.fixtures.json")))!.AsObject();

    private static readonly DateTime Noon = new DateTime(2026, 11, 11, 12, 0, 0, DateTimeKind.Utc) - Ist.Offset;

    private sealed class History(JsonObject answer) : IFlowApi
    {
        public List<(string Api, JsonObject Body)> Calls { get; } = [];

        public Task<JsonObject> CallAsync(string api, JsonObject body, CancellationToken ct)
        {
            Calls.Add((api, (JsonObject)body.DeepClone()));
            return Task.FromResult((JsonObject)answer.DeepClone());
        }
    }

    /// <summary>The recorded 400-bar data set as the history endpoint returns it.</summary>
    private static JsonObject Bars()
    {
        var d = Fixtures["data"]!.AsObject();
        var rows = new JsonArray();
        for (var i = 0; i < d["close"]!.AsArray().Count; i++)
        {
            var row = new JsonObject { ["timestamp"] = 1_700_000_000 + (i * 300) };
            foreach (var c in new[] { "open", "high", "low", "close", "volume" }) row[c] = d[c]![i]!.DeepClone();
            rows.Add(row);
        }

        return new JsonObject { ["status"] = "success", ["data"] = rows };
    }

    private static async Task<(JsonObject? Out, FlowOutcome Outcome, FlowRun Run, History Api)> RunAsync(JsonObject data, JsonObject? answer = null, JsonObject? vars = null)
    {
        var api = new History(answer ?? Bars());
        var run = new FlowRun(() => Noon, host: new FlowHost(api, "t"));
        foreach (var (k, v) in vars ?? []) run.Context.Set(k, v?.DeepClone());
        data["outputVariable"] ??= "ind";
        var nodes = new List<FlowNode> { new("s", "start", []), new("i", "indicator", data) };
        var outcome = await new FlowRunner(CoreFlowNodes.All().Concat(ApiFlowNodes.All())).RunAsync(new FlowGraph(nodes, [new("s", "i", "")]), run, default);
        return (run.Context.Get("ind") as JsonObject, outcome, run, api);
    }

    /// <summary>A recorded case's last values, the way the node reports them (skipping bars with no value, six decimals).</summary>
    private static double?[] Recorded(string name, string? parameters = null)
    {
        var c = Fixtures["cases"]!.AsArray().OfType<JsonObject>().First(x => x["name"]!.GetValue<string>() == name && (parameters is null || x["params"]!.ToJsonString() == parameters));
        var result = c["result"]!.AsArray();
        var outputs = result.Count > 0 && result[0] is JsonArray ? result.Select(x => x!.AsArray()).ToArray() : [result];
        return outputs.Select(o => o.Where(v => v is not null).Select(v => (double?)v!.GetValue<double>()).LastOrDefault()).ToArray();
    }

    private static double? Round6(double? v) => v is null ? null : Math.Round(v.Value, 6, MidpointRounding.ToEven);

    [Fact]
    public async Task A_simple_average_over_a_symbols_history_reports_latest_previous_tail_and_offset_value()
    {
        var (output, outcome, _, api) = await RunAsync(new() { ["indicatorName"] = "sma", ["symbol"] = "SBIN", ["exchange"] = "NSE", ["interval"] = "5m", ["params"] = "{\"period\": 14}", ["tailBars"] = 3, ["offsetBars"] = 2 });

        Assert.True(outcome.Succeeded, outcome.Message);
        var expected = Fixtures["cases"]!.AsArray().OfType<JsonObject>().First(x => x["name"]!.GetValue<string>() == "sma" && x["params"]!["period"]?.GetValue<int>() == 14)["result"]!.AsArray()
            .Where(v => v is not null).Select(v => v!.GetValue<double>()).ToArray();
        Assert.Equal(Round6(expected[^1]), output!["latest"]!["value"]!.GetValue<double>());
        Assert.Equal(Round6(expected[^2]), output["previous"]!["value"]!.GetValue<double>());
        Assert.Equal(Round6(expected[^3]), output["at_offset"]!["value"]!.GetValue<double>());
        Assert.Equal(2, output["offset_bars"]!.GetValue<int>());
        var tail = output["series"]!.AsArray();
        Assert.Equal(3, tail.Count);
        Assert.Equal(Round6(expected[^3]), tail[0]!["value"]!.GetValue<double>());
        Assert.Equal(Round6(expected[^1]), tail[2]!["value"]!.GetValue<double>());
        Assert.Equal(["close"], output["inputs"]!.AsArray().Select(x => x!.GetValue<string>()).ToArray());
        Assert.Equal(["value"], output["outputs"]!.AsArray().Select(x => x!.GetValue<string>()).ToArray());
        Assert.False(output["nested"]!.GetValue<bool>());
        Assert.Equal(150, output["bars_used"]!.GetValue<int>()); // 100 requested + 50 of warm-up
        Assert.Equal(14, output["params"]!["period"]!.GetValue<int>());
        Assert.Equal("history", api.Calls.Single().Api);
    }

    [Fact]
    public async Task With_no_offset_the_offset_value_is_the_latest()
    {
        var (output, _, _, _) = await RunAsync(new() { ["indicatorName"] = "sma", ["symbol"] = "SBIN", ["params"] = "{\"period\": 14}" });
        Assert.Equal(0, output!["offset_bars"]!.GetValue<int>());
        Assert.Equal(output["latest"]!.ToJsonString(), output["at_offset"]!.ToJsonString());
    }

    [Fact]
    public async Task An_indicator_with_several_outputs_names_them_in_the_order_openalgo_returns_them()
    {
        var (output, outcome, _, _) = await RunAsync(new() { ["indicatorName"] = "bbands", ["symbol"] = "SBIN" });
        Assert.True(outcome.Succeeded, outcome.Message);
        Assert.Equal(["out0", "out1", "out2"], output!["outputs"]!.AsArray().Select(x => x!.GetValue<string>()).ToArray());
        var expected = Recorded("bbands");
        for (var i = 0; i < 3; i++) Assert.Equal(Round6(expected[i]), output["latest"]![$"out{i}"]!.GetValue<double>());
    }

    [Fact]
    public async Task The_history_asked_for_covers_the_bars_with_a_trading_day_allowance_and_ends_today_in_india()
    {
        var (_, _, _, api) = await RunAsync(new() { ["indicatorName"] = "sma", ["symbol"] = "SBIN", ["interval"] = "5m", ["source"] = "db" });
        var body = api.Calls.Single().Body;
        // 100 five-minute bars at 75 a day, 1.6 for weekends and holidays, and 5 spare days.
        Assert.Equal("2026-11-11", body["end_date"]!.GetValue<string>());
        Assert.Equal("2026-11-04", body["start_date"]!.GetValue<string>());
        Assert.Equal(("5m", "db", "NSE"), (body["interval"]!.GetValue<string>(), body["source"]!.GetValue<string>(), body["exchange"]!.GetValue<string>()));

        (_, _, _, api) = await RunAsync(new() { ["indicatorName"] = "sma", ["symbol"] = "SBIN" });
        Assert.Equal("2026-05-30", api.Calls.Single().Body["start_date"]!.GetValue<string>()); // daily: 100 * 1.6 + 5 = 165 days
    }

    [Theory]
    [InlineData(1, "D", 6)]
    [InlineData(100, "D", 165)]
    [InlineData(100, "5m", 7)]
    [InlineData(100, "unheard-of", 7)]
    [InlineData(5000, "D", 4000)]
    public void The_calendar_span_has_a_trading_day_allowance_and_a_ceiling(int bars, string interval, int days) => Assert.Equal(days, IndicatorNode.CalendarDays(bars, interval));

    [Fact]
    public async Task The_params_may_be_written_as_text_or_as_an_object()
    {
        var (a, _, _, _) = await RunAsync(new() { ["indicatorName"] = "sma", ["symbol"] = "X", ["params"] = "{\"period\": 5}" });
        var (b, _, run, _) = await RunAsync(new() { ["indicatorName"] = "sma", ["symbol"] = "X", ["params"] = new JsonObject { ["period"] = 5 } });
        Assert.Equal(a!["latest"]!.ToJsonString(), b!["latest"]!.ToJsonString());
        Assert.Contains(run.Logs, l => l.Level == "warning" && l.Message.Contains("normalising"));
    }

    [Theory]
    [InlineData("nosuch", "unknown indicator 'nosuch'")]
    [InlineData("beta", "needs two independent series")]
    [InlineData("vi", "is not available")]
    public async Task An_indicator_that_cannot_run_says_why(string name, string reason)
    {
        var (_, outcome, _, _) = await RunAsync(new() { ["indicatorName"] = name, ["symbol"] = "X" });
        Assert.False(outcome.Succeeded);
        Assert.Contains(reason, outcome.Message);
    }

    [Fact]
    public async Task Bad_params_and_a_missing_symbol_and_no_history_are_reported_not_guessed()
    {
        var (_, o1, _, _) = await RunAsync(new() { ["indicatorName"] = "sma", ["symbol"] = "X", ["params"] = "{nope" });
        Assert.Contains("Invalid params JSON: {nope", o1.Message);
        var (_, o2, _, _) = await RunAsync(new() { ["indicatorName"] = "sma", ["symbol"] = "X", ["params"] = "[1,2]" });
        Assert.Contains("params must resolve to a JSON object, got list", o2.Message);
        var (_, o3, _, api3) = await RunAsync(new() { ["indicatorName"] = "sma", ["symbol"] = "" });
        Assert.Contains("Symbol is required", o3.Message);
        Assert.Empty(api3.Calls);
        var (_, o4, _, _) = await RunAsync(new() { ["indicatorName"] = "sma", ["symbol"] = "X" }, new JsonObject { ["status"] = "success", ["data"] = new JsonArray() });
        Assert.Contains("no historical data available", o4.Message);
    }

    [Fact]
    public async Task A_failed_history_fetch_names_the_symbol_and_interval_and_points_to_the_fix()
    {
        var (_, outcome, _, _) = await RunAsync(new() { ["indicatorName"] = "sma", ["symbol"] = "SBIN", ["interval"] = "2m" }, new JsonObject { ["status"] = "error", ["error"] = "interval not supported", ["code"] = 400 });
        Assert.Contains("History fetch failed for SBIN (2m): interval not supported.", outcome.Message);
        Assert.Contains("Historify DB", outcome.Message);
    }

    [Fact]
    public async Task An_indicator_with_no_value_over_the_bars_it_was_given_is_an_error_not_a_quiet_null()
    {
        var (_, outcome, _, _) = await RunAsync(new() { ["indicatorName"] = "sma", ["symbol"] = "X", ["params"] = "{\"period\": 190}" });
        Assert.False(outcome.Succeeded);
        Assert.Contains("produced no value", outcome.Message);
    }

    [Fact]
    public async Task The_tail_and_the_bars_are_bounded()
    {
        var (output, _, _, _) = await RunAsync(new() { ["indicatorName"] = "sma", ["symbol"] = "X", ["tailBars"] = 5000, ["lookbackBars"] = 5000 });
        // The ceiling is 200 bars whatever was asked for.
        Assert.Equal(200, output!["series"]!.AsArray().Count);
        Assert.Equal(200, output["bars_used"]!.GetValue<int>());
    }

    [Fact]
    public async Task One_indicator_can_be_computed_over_another_ones_series()
    {
        var (rsi, _, _, _) = await RunAsync(new() { ["indicatorName"] = "rsi", ["symbol"] = "X", ["tailBars"] = 20 });
        var (nested, outcome, _, api) = await RunAsync(new() { ["indicatorName"] = "sma", ["sourceSeries"] = "{{r.series}}", ["params"] = "{\"period\": 3}", ["tailBars"] = 2 }, vars: new() { ["r"] = rsi!.DeepClone() });

        Assert.True(outcome.Succeeded, outcome.Message);
        Assert.Empty(api.Calls);
        Assert.True(nested!["nested"]!.GetValue<bool>());
        Assert.Equal(["data (nested)"], nested["inputs"]!.AsArray().Select(x => x!.GetValue<string>()).ToArray());
        var last3 = rsi["series"]!.AsArray().TakeLast(3).Select(x => x!["value"]!.GetValue<double>()).ToArray();
        Assert.Equal(Math.Round(last3.Average(), 6), nested["latest"]!["value"]!.GetValue<double>(), 5);
        Assert.Equal(20, nested["bars_used"]!.GetValue<int>());
    }

    [Theory]
    [InlineData("[1, 2, \"x\"]", "sourceSeries contains a non-numeric value: 'x'")]
    [InlineData("[1]", "nested indicator needs at least 2 upstream values")]
    [InlineData("42", "sourceSeries must resolve to an array")]
    [InlineData("not json", "sourceSeries did not resolve to a JSON array")]
    public async Task A_bad_source_series_is_reported(string source, string reason)
    {
        var (_, outcome, _, _) = await RunAsync(new() { ["indicatorName"] = "sma", ["sourceSeries"] = source });
        Assert.False(outcome.Succeeded);
        Assert.Contains(reason, outcome.Message);
    }

    [Fact]
    public async Task An_indicator_that_reads_high_low_and_close_cannot_be_nested()
    {
        var (_, outcome, _, _) = await RunAsync(new() { ["indicatorName"] = "atr", ["sourceSeries"] = "[1,2,3,4,5]" });
        Assert.False(outcome.Succeeded);
        Assert.Contains("cannot be nested", outcome.Message);
    }

    [Fact]
    public async Task A_raw_history_array_can_be_fed_in_and_a_named_field_picks_the_column()
    {
        var rows = new JsonArray(Enumerable.Range(1, 10).Select(i => (JsonNode?)new JsonObject { ["close"] = i, ["open"] = 100 + i }).ToArray());
        var two = "{\"period\": 2}";
        var (byDefault, o1, _, _) = await RunAsync(new() { ["indicatorName"] = "sma", ["sourceSeries"] = "{{h}}", ["params"] = two }, vars: new() { ["h"] = rows.DeepClone() });
        Assert.True(o1.Succeeded, o1.Message);
        Assert.Equal(9.5, byDefault!["latest"]!["value"]!.GetValue<double>()); // close, when no field is named
        var (byField, o2, _, _) = await RunAsync(new() { ["indicatorName"] = "sma", ["sourceSeries"] = "{{h}}", ["sourceField"] = "open", ["params"] = two }, vars: new() { ["h"] = rows.DeepClone() });
        Assert.True(o2.Succeeded, o2.Message);
        Assert.Equal(109.5, byField!["latest"]!["value"]!.GetValue<double>());
    }
}
