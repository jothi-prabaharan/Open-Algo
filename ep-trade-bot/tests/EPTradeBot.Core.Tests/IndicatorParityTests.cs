using System.Text.Json.Nodes;
using EPTradeBot.Core.Indicators;

namespace EPTradeBot.Core.Tests;

/// <summary>
/// Every indicator against the values openalgo.ta produced on one fixed data set (tools/contract-fixtures/indicators.fixtures.json, recorded by
/// record_indicators.py). A value agrees when both are NaN, or the difference is within 1e-9 of the larger magnitude (or 1e-9 absolute).
/// </summary>
public sealed class IndicatorParityTests
{
    private static readonly JsonObject Fixtures = (JsonObject)JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "indicators.fixtures.json")))!;

    public static IEnumerable<object[]> Cases() => Fixtures["cases"]!.AsArray().Select(c => new object[] { c!["name"]!.GetValue<string>() });

    private static double[] Series(JsonNode? node) => node!.AsArray().Select(x => x is null ? double.NaN : x.GetValue<double>()).ToArray();

    private static double[] Values(JsonNode? node) => node!.AsArray().Select(x => x is null ? double.NaN : x is JsonValue v && v.TryGetValue<bool>(out var b) ? (b ? 1.0 : 0.0) : x.GetValue<double>()).ToArray();

    [Fact]
    public void The_whole_of_openalgo_ta_is_ported_and_each_indicator_has_a_recorded_case()
    {
        var recorded = Fixtures["cases"]!.AsArray().Select(c => c!["indicator"]!.GetValue<string>()).ToHashSet();
        Assert.Equal(127, IndicatorRegistry.All.Count);
        Assert.Equal(IndicatorRegistry.All.Keys.Order(), recorded.Order());
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void Matches_openalgo_ta(string name)
    {
        var c = Fixtures["cases"]!.AsArray().OfType<JsonObject>().Single(x => x["name"]!.GetValue<string>() == name);
        var indicator = c["indicator"]!.GetValue<string>();
        Assert.True(IndicatorRegistry.All.TryGetValue(indicator, out var compute), $"{indicator} is not ported yet.");

        var data = (JsonObject)Fixtures[c["dataset"]?.GetValue<string>() ?? "data"]!;
        var slice = c["slice"]?.GetValue<int>();
        var series = new Dictionary<string, double[]>();
        foreach (var (param, source) in c["series"]!.AsObject())
        {
            series[param] = source is JsonArray ref2
                ? Kernels.Sma(Series(data["close"]), ref2[1]!.GetValue<int>())
                : Series(data[source!.GetValue<string>()]) is var full && slice is { } take ? full[..take] : Series(data[source!.GetValue<string>()]);
        }

        // The Python names the open "open_prices" and the average of the data "source"; the .NET call names them for what they are.
        if (series.Remove("open_prices", out var open)) series["open"] = open;
        var parameters = c["params"]!.AsObject().ToDictionary(kv => kv.Key, kv => (object)kv.Value!.GetValue<double>());

        var actual = compute!(new IndicatorArgs(series, parameters));
        var expected = c["result"]!.AsArray();
        var tuple = expected.Count > 0 && expected[0] is JsonArray;
        var expectedOutputs = tuple ? expected.Select(Values).ToArray() : [Values(expected)];
        Assert.True(expectedOutputs.Length == actual.Length, $"{name}: expected {expectedOutputs.Length} outputs, got {actual.Length}");
        for (var k = 0; k < actual.Length; k++)
        {
            Assert.Equal(expectedOutputs[k].Length, actual[k].Length);
            for (var i = 0; i < actual[k].Length; i++)
            {
                var (e, a) = (expectedOutputs[k][i], actual[k][i]);
                if (double.IsNaN(e) && double.IsNaN(a)) continue;
                var tolerance = 1e-9 * Math.Max(1.0, Math.Max(Math.Abs(e), Math.Abs(a)));
                Assert.True(Math.Abs(e - a) <= tolerance && !double.IsNaN(e) && !double.IsNaN(a), $"{name} output {k} index {i}: expected {e:R}, got {a:R}");
            }
        }
    }
}
