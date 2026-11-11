using System.Text.Json.Nodes;
using EPTradeBot.Core.Flow;
using EPTradeBot.Core.Time;

namespace EPTradeBot.Core.Tests;

/// <summary>The Flow engine against what OpenAlgo's own executor did on the same inputs (flow.fixtures.json, from record_flow.py).</summary>
public sealed class FlowParityTests
{
    private static readonly JsonObject Fixtures = JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "flow.fixtures.json")))!.AsObject();

    public static IEnumerable<object[]> Interpolations() => Fixtures["interpolate"]!.AsArray().Select((c, i) => new object[] { i, c!["text"]!.GetValue<string>() });

    public static IEnumerable<object[]> Maths() => Fixtures["math"]!.AsArray().Select((c, i) => new object[] { i, c!["expr"]!.GetValue<string>() });

    public static IEnumerable<object[]> Times() => Fixtures["times"]!.AsArray().Select((c, i) => new object[] { i, c!["text"]!.GetValue<string>() });

    public static IEnumerable<object[]> Graphs() => Fixtures["graphs"]!.AsArray().Select((c, i) => new object[] { i, c!["name"]!.GetValue<string>() });

    private static FlowContext ContextWith(JsonObject vars)
    {
        var ctx = new FlowContext(() => DateTime.UtcNow);
        foreach (var (k, v) in vars) ctx.Set(k, v?.DeepClone());
        return ctx;
    }

    /// <summary>Equal as data: numbers by value, containers element by element.</summary>
    private static bool SameData(JsonNode? a, JsonNode? b)
    {
        if (a is null || b is null) return a is null && b is null;
        if (a is JsonObject oa && b is JsonObject ob)
            return oa.Count == ob.Count && oa.All(kv => ob.ContainsKey(kv.Key) && SameData(kv.Value, ob[kv.Key]));
        if (a is JsonArray aa && b is JsonArray ab) return aa.Count == ab.Count && aa.Zip(ab).All(p => SameData(p.First, p.Second));
        if (a is JsonValue va && b is JsonValue vb)
        {
            if (va.TryGetValue<string>(out var sa) && vb.TryGetValue<string>(out var sb)) return sa == sb;
            if (va.TryGetValue<bool>(out var ba) && vb.TryGetValue<bool>(out var bb)) return ba == bb;
            if (va.TryGetValue<double>(out var da) && vb.TryGetValue<double>(out var db)) return da == db || Math.Abs(da - db) <= 1e-12 * Math.Max(Math.Abs(da), Math.Abs(db));
        }

        return false;
    }

    [Theory]
    [MemberData(nameof(Interpolations))]
    public void Substitution_gives_the_text_and_value_OpenAlgo_gave(int index, string text)
    {
        var c = Fixtures["interpolate"]![index]!;
        var ctx = ContextWith(c["vars"]!.AsObject());
        Assert.Equal(c["interpolated"]!.GetValue<string>(), ctx.Interpolate(text));
        Assert.True(SameData(c["raw"], ctx.ResolveRaw(text)), $"raw value of {text}: expected {c["raw"]?.ToJsonString()}, got {ctx.ResolveRaw(text)?.ToJsonString()}");
    }

    [Theory]
    [MemberData(nameof(Maths))]
    public void Arithmetic_gives_the_value_or_the_refusal_OpenAlgo_gave(int index, string expression)
    {
        var c = Fixtures["math"]![index]!;
        if (c["error"] is not null)
        {
            Assert.ThrowsAny<Exception>(() => MathEvaluator.Evaluate(expression));
            return;
        }

        var value = MathEvaluator.Evaluate(expression);
        if (c["value"] is JsonValue v && v.TryGetValue<string>(out var text))
        {
            Assert.Equal(text, FlowContext.PyFloat(value));
            return;
        }

        var expected = c["value"]!.GetValue<double>();
        Assert.True(value == expected || Math.Abs(value - expected) <= 1e-12 * Math.Abs(expected), $"{expression}: expected {expected}, got {value}");
    }

    [Theory]
    [MemberData(nameof(Times))]
    public void Times_are_read_and_clamped_as_OpenAlgo_reads_them(int index, string text)
    {
        var parsed = Fixtures["times"]![index]!["parsed"]!.AsArray().Select(x => x!.GetValue<int>()).ToArray();
        var (h, m, s) = FlowData.ParseTime(text, 9, 15);
        Assert.Equal(parsed, new[] { h, m, s });
    }

    [Theory]
    [MemberData(nameof(Graphs))]
    public async Task A_graph_does_what_OpenAlgo_did(int index, string name)
    {
        var g = Fixtures["graphs"]![index]!;
        var graph = FlowGraph.Parse(g["nodes"]!.AsArray(), g["edges"]!.AsArray());
        var istNow = DateTime.Parse(g["at"]!.GetValue<string>(), System.Globalization.CultureInfo.InvariantCulture);
        var utc = DateTime.SpecifyKind(istNow - Ist.Offset, DateTimeKind.Utc);
        var sleeps = new List<double>();
        var run = new FlowRun(() => utc, (d, _) => { sleeps.Add(d.TotalSeconds); return Task.CompletedTask; });
        foreach (var (k, v) in g["vars"]!.AsObject()) run.Context.Set(k, v?.DeepClone());

        var outcome = await new FlowRunner(CoreFlowNodes.All()).RunAsync(graph, run, default);

        // What the graph did to its variables, in order, and what it said.
        var expectedVars = g["variables"]!.AsObject();
        var actualVars = new JsonObject(run.Context.Variables.Select(kv => new KeyValuePair<string, JsonNode?>(kv.Key, kv.Value?.DeepClone())));
        Assert.True(SameData(expectedVars, actualVars), $"{name}: variables expected {expectedVars.ToJsonString()}, got {actualVars.ToJsonString()}");
        var logs = run.Logs.Where(l => l.Message.StartsWith("[LOG]")).Select(l => l.Message).ToArray();
        Assert.Equal(g["logs"]!.AsArray().Select(x => x!.GetValue<string>()).ToArray(), logs);
        Assert.Equal(g["sleeps"]!.AsArray().Select(x => x!.GetValue<double>()).ToArray(), sleeps.ToArray());

        // Which nodes failed. The wording is compared wherever this engine writes it; a Python exception's own text is not.
        var expectedErrors = g["errors"]!.AsArray().Select(x => x!.AsObject()).ToList();
        Assert.Equal(expectedErrors.Count, run.Errors.Count);
        for (var i = 0; i < expectedErrors.Count; i++)
        {
            Assert.Equal(expectedErrors[i]["node"]!.GetValue<string>(), run.Errors[i].Node);
            Assert.Equal(expectedErrors[i]["type"]!.GetValue<string>(), run.Errors[i].Type);
            if (run.Errors[i].Type is not ("variable" or "mathExpression"))
                Assert.Equal(expectedErrors[i]["message"]!.GetValue<string>(), run.Errors[i].Message);
        }

        var raised = g["raised"]?.GetValue<string>();
        if (raised is null) Assert.Equal(expectedErrors.Count == 0, outcome.Succeeded);
        else
        {
            Assert.False(outcome.Succeeded);
            Assert.Equal(raised, outcome.Message);
        }
    }

    [Fact]
    public void The_session_date_rolls_over_at_three_in_the_morning()
    {
        string At(int h, int m) => new FlowContext(() => new DateTime(2026, 11, 11, h, m, 0, DateTimeKind.Utc) - Ist.Offset).Interpolate("{{session_date}}");
        Assert.Equal("2026-11-10", At(2, 59));
        Assert.Equal("2026-11-11", At(3, 0));
        Assert.Equal("2026-11-11", At(23, 59));
    }

    [Fact]
    public void The_built_in_variables_read_the_india_clock()
    {
        var ctx = new FlowContext(() => new DateTime(2026, 11, 8, 12, 30, 5, DateTimeKind.Utc) - Ist.Offset); // a Sunday, 12:30:05 IST
        Assert.Equal("2026-11-08 12:30:05", ctx.Interpolate("{{timestamp}}"));
        Assert.Equal("Sunday", ctx.Interpolate("{{weekday}}"));
        Assert.Equal("7", ctx.Interpolate("{{weekday_num}}"));
        Assert.Equal("4", ctx.Interpolate("{{quarter}}"));
        Assert.Equal("12:30:05", ctx.Interpolate("{{time}}"));
        Assert.Equal("312", ctx.Interpolate("{{day_of_year}}"));
        Assert.Equal("45", ctx.Interpolate("{{week_of_year}}"));
    }
}
