using System.Globalization;
using System.Text.Json.Nodes;
using EPTradeBot.Core.Portfolio;

namespace EPTradeBot.Core.Tests;

/// <summary>The SIP back tester against OpenAlgo's own (sip.fixtures.json, from record_sip.py): schedule, whole-share simulation, cost model, every metric and every validation message.</summary>
public sealed class SipParityTests
{
    private static readonly JsonObject Fixtures = JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "sip.fixtures.json")))!.AsObject();
    private const int Sample = 50;

    public static IEnumerable<object[]> Cases() => Fixtures["cases"]!.AsArray().Select((c, i) => new object[] { i, c!["name"]!.GetValue<string>() });

    private static PriceSeries Series(JsonNode? dates, JsonNode? closes) => new(
        dates!.AsArray().Select(d => DateOnly.ParseExact(d!.GetValue<string>(), "yyyy-MM-dd", CultureInfo.InvariantCulture)).ToArray(), closes!.AsArray().Select(c => c!.GetValue<double>()).ToArray());

    private static JsonArray Thin(JsonArray rows)
    {
        if (rows.Count <= Sample * 2) return rows;
        var keep = new JsonArray();
        for (var i = 0; i < rows.Count; i++)
            if (i % Sample == 0 || i == rows.Count - 1 && (rows.Count - 1) % Sample != 0) keep.Add(rows[i]!.DeepClone());
        return keep;
    }

    /// <summary>The same thinning the recorder applied, so a long response is compared by its shape and sampled points.</summary>
    private static void SampleLike(JsonObject r)
    {
        if (r["curves"] is JsonObject curves)
            foreach (var key in curves.Select(k => k.Key).ToList()) curves[key] = Thin((JsonArray)curves[key]!);
        if (r["drawdown"] is JsonObject dd && dd["curve"] is JsonArray curve) dd["curve"] = Thin(curve);
        if (r["benchmark"] is JsonObject b && b["curve"] is JsonArray bc) b["curve"] = Thin(bc);
        if (r["rolling_xirr"] is JsonArray rolling)
            foreach (var row in rolling.OfType<JsonObject>()) row["points"] = Thin((JsonArray)row["points"]!);
    }

    private static IReadOnlyDictionary<string, IReadOnlyDictionary<string, double?>>? Overrides(JsonNode? node) =>
        node is JsonObject o ? o.ToDictionary(kv => kv.Key, kv => (IReadOnlyDictionary<string, double?>)((JsonObject)kv.Value!).ToDictionary(p => p.Key, p => p.Value is JsonValue v ? (double?)v.GetValue<double>() : null)) : null;

    [Theory]
    [MemberData(nameof(Cases))]
    public void The_SIP_back_test_matches_OpenAlgo(int index, string name)
    {
        _ = name;
        var c = Fixtures["cases"]![index]!;
        var args = c["args"]!.AsObject();
        double D(string key, double fallback) => args[key] is JsonValue v ? v.GetValue<double>() : fallback;
        string S(string key, string fallback) => args[key]?.GetValue<string>() ?? fallback;
        var request = new SipRequest(
            c["symbol"]!.GetValue<string>(), c["exchange"]!.GetValue<string>(), c["start_date"]?.DeepClone(), c["end_date"]?.DeepClone(), c["amount"]?.DeepClone(), S("frequency", "monthly"), args["day_of_month"]?.DeepClone(),
            D("step_up_percent", 0), D("brokerage_percent", 0), D("brokerage_flat", 0), S("cost_model", "indian_equity"), S("cost_exchange", "NSE"), Overrides(args["charge_overrides"]),
            args["gst_rate"] is JsonValue g ? g.GetValue<double>() : null, D("cost_bps", 0), D("slippage", 0), args["benchmark"]?.GetValue<string>(), S("benchmark_exchange", "NSE_INDEX"), S("source", "db"),
            args["include_grids"]?.GetValue<bool>() ?? true);

        var loads = new List<JsonObject>();
        SipService.PriceLoader load = (symbol, exchange, start, end, source, benchmark) =>
        {
            loads.Add(new JsonObject { ["symbol"] = symbol, ["exchange"] = exchange, ["start"] = start, ["end"] = end, ["source"] = source, ["benchmark"] = benchmark });
            var warnings = c["warnings"]!.AsArray().Select(w => w!.GetValue<string>()).ToList();
            if (benchmark)
            {
                if (c["bench_error"]!.GetValue<bool>()) throw new PriceDataException("no history for benchmark");
                return new LoadedPrices(Series(c["bench"]![0], c["bench"]![1]), "historify", []);
            }

            if (c["load_error"] is JsonArray err)
                throw err[0]!.GetValue<string>() == "DataError" ? new PriceDataException(err[1]!.GetValue<string>()) : new InvalidOperationException(err[1]!.GetValue<string>());
            return new LoadedPrices(Series(c["dates"], c["closes"]), "historify", warnings);
        };

        var (ok, response, code) = SipService.Run(request, load);
        Assert.Equal(c["ok"]!.GetValue<bool>(), ok);
        Assert.Equal(c["code"]!.GetValue<int>(), code);
        SampleLike(response);
        ToolsParityTests.AssertSame(c["response"], response, 1e-9);
        Assert.Equal(c["loads"]!.AsArray().Count, loads.Count);
        for (var i = 0; i < loads.Count; i++) ToolsParityTests.AssertSame(c["loads"]![i], loads[i]);
    }
}
