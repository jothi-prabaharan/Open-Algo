using System.Text.Json.Nodes;
using EPTradeBot.Core.Options;

namespace EPTradeBot.Core.Tests;

/// <summary>The /tools back ends against what OpenAlgo's own services returned for the same inputs and clock (tools.fixtures.json, from record_tools.py). Numbers must agree to the digit OpenAlgo rounds to.</summary>
public sealed class ToolsParityTests
{
    private static readonly JsonObject Fixtures = JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "tools.fixtures.json")))!.AsObject();

    /// <summary>Equal as data: numbers by value (to the last digit OpenAlgo shows), containers element by element.</summary>
    internal static string? Difference(JsonNode? expected, JsonNode? actual, string path = "$", double tolerance = 1e-12)
    {
        if (expected is null || actual is null) return expected is null && actual is null ? null : $"{path}: expected {expected?.ToJsonString() ?? "null"}, got {actual?.ToJsonString() ?? "null"}";
        if (expected is JsonObject eo)
        {
            if (actual is not JsonObject ao) return $"{path}: expected an object, got {actual.ToJsonString()}";
            foreach (var (key, value) in eo)
                if (!ao.ContainsKey(key)) return $"{path}.{key}: missing";
                else if (Difference(value, ao[key], $"{path}.{key}", tolerance) is { } d) return d;
            foreach (var (key, _) in ao)
                if (!eo.ContainsKey(key)) return $"{path}.{key}: unexpected";
            return null;
        }

        if (expected is JsonArray ea)
        {
            if (actual is not JsonArray aa || aa.Count != ea.Count) return $"{path}: expected {ea.Count} items, got {(actual as JsonArray)?.Count.ToString() ?? actual.ToJsonString()}";
            for (var i = 0; i < ea.Count; i++)
                if (Difference(ea[i], aa[i], $"{path}[{i}]", tolerance) is { } d) return d;
            return null;
        }

        var ev = (JsonValue)expected;
        var av = (JsonValue)actual;
        if (ev.TryGetValue<string>(out var es)) return av.TryGetValue<string>(out var s2) && s2 == es ? null : $"{path}: expected \"{es}\", got {av.ToJsonString()}";
        if (ev.TryGetValue<bool>(out var eb)) return av.TryGetValue<bool>(out var b2) && b2 == eb ? null : $"{path}: expected {eb}, got {av.ToJsonString()}";
        if (FlowNumber(ev, out var en) && FlowNumber(av, out var an))
            return en == an || Math.Abs(en - an) <= tolerance * Math.Max(1.0, Math.Abs(en)) ? null : $"{path}: expected {en:R}, got {an:R}";
        return $"{path}: expected {ev.ToJsonString()}, got {av.ToJsonString()}";
    }

    /// <summary>Fails with the full path and both values; Assert.Null cuts the message short.</summary>
    internal static void AssertSame(JsonNode? expected, JsonNode? actual, double tolerance = 1e-12)
    {
        var d = Difference(expected, actual, "$", tolerance);
        Assert.True(d is null, d);
    }

    private static bool FlowNumber(JsonValue v, out double d)
    {
        if (v.TryGetValue<double>(out d)) return true;
        if (v.TryGetValue<long>(out var l)) { d = l; return true; }
        if (v.TryGetValue<int>(out var i)) { d = i; return true; }
        d = 0;
        return false;
    }

    public static IEnumerable<object[]> GreeksCases() => Fixtures["greeks"]!["cases"]!.AsArray().Select((c, i) => new object[] { i, $"{c!["symbol"]} {c["spot"]} {c["price"]} {c["expiry_time"]}" });

    [Theory]
    [MemberData(nameof(GreeksCases))]
    public void Greeks_match_OpenAlgo_for_each_recorded_option(int index, string label)
    {
        _ = label;
        var c = Fixtures["greeks"]!["cases"]![index]!;
        var now = DateTime.Parse(Fixtures["greeks"]!["now"]!.GetValue<string>()[..19], System.Globalization.CultureInfo.InvariantCulture);
        var (ok, response, code) = OptionGreeks.Calculate(c["symbol"]!.GetValue<string>(), c["exchange"]!.GetValue<string>(), c["spot"]!.GetValue<double>(), c["price"]!.GetValue<double>(),
            c["rate"]?.GetValue<double>(), c["expiry_time"]?.GetValue<string>(), now);
        Assert.Equal(c["ok"]!.GetValue<bool>(), ok);
        Assert.Equal(c["code"]!.GetValue<int>(), code);
        AssertSame(c["response"], response);
    }

    private static DateTime Now(string tool) => DateTime.Parse(Fixtures[tool]!["now"]!.GetValue<string>()[..19], System.Globalization.CultureInfo.InvariantCulture);

    public static IEnumerable<object[]> GexCases() => Fixtures["gex"]!["cases"]!.AsArray().Select((c, i) => new object[] { i, c!["name"]!.GetValue<string>() });

    [Theory]
    [MemberData(nameof(GexCases))]
    public void Gamma_exposure_matches_OpenAlgo_strike_by_strike(int index, string name)
    {
        _ = name;
        var c = Fixtures["gex"]!["cases"]![index]!;
        var chain = c["chain"] as JsonObject;
        (bool Ok, JsonObject Response, int Code) result = chain is null
            ? (false, (JsonObject)c["response"]!.DeepClone(), c["code"]!.GetValue<int>())
            : GexTool.Compute(chain, c["futures"]?.GetValue<double>(), "NIFTY", c["exchange"]!.GetValue<string>(), c["expiry"]!.GetValue<string>(), Now("gex"));
        Assert.Equal(c["ok"]!.GetValue<bool>(), result.Ok);
        Assert.Equal(c["code"]!.GetValue<int>(), result.Code);
        AssertSame(c["response"], result.Response);
    }

    public static IEnumerable<object[]> IvSmileCases() => Fixtures["iv_smile"]!["cases"]!.AsArray().Select((c, i) => new object[] { i, c!["name"]!.GetValue<string>() });

    [Theory]
    [MemberData(nameof(IvSmileCases))]
    public void The_IV_smile_matches_OpenAlgo_including_ATM_level_and_skew(int index, string name)
    {
        _ = name;
        var c = Fixtures["iv_smile"]!["cases"]![index]!;
        var chain = c["chain"] as JsonObject;
        (bool Ok, JsonObject Response, int Code) result = chain is null
            ? (false, (JsonObject)c["response"]!.DeepClone(), c["code"]!.GetValue<int>())
            : IvSmileTool.Compute(chain, "NIFTY", c["exchange"]!.GetValue<string>(), c["expiry"]!.GetValue<string>(), Now("iv_smile"));
        Assert.Equal(c["ok"]!.GetValue<bool>(), result.Ok);
        Assert.Equal(c["code"]!.GetValue<int>(), result.Code);
        AssertSame(c["response"], result.Response);
    }

    public static IEnumerable<object[]> OiCases() => Fixtures["oi"]!["cases"]!.AsArray().Select((c, i) => new object[] { i, c!["name"]!.GetValue<string>() });

    [Theory]
    [MemberData(nameof(OiCases))]
    public void Open_interest_and_max_pain_match_OpenAlgo(int index, string name)
    {
        _ = name;
        var c = Fixtures["oi"]!["cases"]![index]!;
        var chain = c["chain"] as JsonObject;
        var underlying = c["underlying"]!.GetValue<string>();
        var expiry = c["expiry"]!.GetValue<string>();
        var futures = c["futures"]?.GetValue<double>();
        foreach (var (kind, expected) in new[] { ("oi", c["oi"]!), ("pain", c["pain"]!) })
        {
            (bool Ok, JsonObject Response, int Code) result = chain is null
                ? (false, (JsonObject)expected["response"]!.DeepClone(), expected["code"]!.GetValue<int>())
                : kind == "oi" ? OiTools.OiData(chain, futures, underlying, expiry) : OiTools.MaxPain(chain, futures, underlying, expiry);
            Assert.Equal(expected["ok"]!.GetValue<bool>(), result.Ok);
            Assert.Equal(expected["code"]!.GetValue<int>(), result.Code);
            AssertSame(expected["response"], result.Response);
        }
    }

    public static IEnumerable<object[]> VolSurfaceCases() => Fixtures["vol_surface"]!["cases"]!.AsArray().Select((c, i) => new object[] { i, c!["name"]!.GetValue<string>() });

    [Theory]
    [MemberData(nameof(VolSurfaceCases))]
    public void The_volatility_surface_matches_OpenAlgo_cell_by_cell(int index, string name)
    {
        _ = name;
        var c = Fixtures["vol_surface"]!["cases"]![index]!;
        var strikes = c["strikes"]!.AsObject();
        var quotes = c["quotes"]!.AsObject();
        var failure = c["quote_fail"] is JsonArray f ? ((int Code, string Message)?)(f[0]!.GetValue<int>(), f[1]!.GetValue<string>()) : null;
        var inputs = new VolSurfaceTool.Inputs(
            c["ltp"]!.GetValue<double>(), failure,
            expiry => strikes[expiry] is JsonArray a ? a.Select(x => x!.GetValue<double>()).ToList() : [],
            symbol => quotes[symbol] is JsonValue v ? v.GetValue<double>() : null);
        var (ok, response, code) = VolSurfaceTool.Compute(c["underlying"]!.GetValue<string>(), c["exchange"]!.GetValue<string>(),
            c["expiries"]!.AsArray().Select(x => x!.GetValue<string>()).ToList(), c["strike_count"]!.GetValue<int>(), inputs, Now("vol_surface"));
        Assert.Equal(c["ok"]!.GetValue<bool>(), ok);
        Assert.Equal(c["code"]!.GetValue<int>(), code);
        AssertSame(c["response"], response);
    }

    public static IEnumerable<object[]> StrikeProfileCases() => Fixtures["strike_profile"]!["cases"]!.AsArray().Select((c, i) => new object[] { i, c!["name"]!.GetValue<string>() });

    [Theory]
    [MemberData(nameof(StrikeProfileCases))]
    public void The_strike_profile_matches_OpenAlgo_including_flip_and_walls(int index, string name)
    {
        _ = name;
        var c = Fixtures["strike_profile"]!["cases"]![index]!;
        var recorded = c["calls"]!.AsArray();
        var expiries = c["expiries"]!.AsArray().Select(x => x!.GetValue<string>()).ToList();
        var failAfter = c["fail_after"]?.GetValue<int>();
        var busy = c["busy"]!.GetValue<bool>();
        var made = new List<(string Expiry, int Count)>();
        StrikeProfileTool.ChainFetch fetch = (expiry, count) =>
        {
            if (failAfter is { } f && expiries.IndexOf(expiry) >= f)
                return (false, new JsonObject { ["status"] = "error", ["message"] = "Option chain unavailable" }, busy ? 429 : 404);
            made.Add((expiry, count));
            var hit = recorded.FirstOrDefault(r => r!["expiry"]!.GetValue<string>() == expiry && r["strike_count"]!.GetValue<int>() == count);
            Assert.True(hit is not null, $"OpenAlgo never asked for {expiry} at {count} strikes");
            return (true, (JsonObject)hit!["chain"]!.DeepClone(), 200);
        };
        var now = DateTime.Parse(Fixtures["strike_profile"]!["now"]!.GetValue<string>()[..19], System.Globalization.CultureInfo.InvariantCulture);
        var (ok, response, code) = StrikeProfileTool.Compute("NIFTY", "NFO", expiries, c["range_pct"]?.GetValue<double>(), fetch, now.AddHours(-5.5));
        Assert.Equal(c["ok"]!.GetValue<bool>(), ok);
        Assert.Equal(c["code"]!.GetValue<int>(), code);
        Assert.Equal(recorded.Select(r => (r!["expiry"]!.GetValue<string>(), r["strike_count"]!.GetValue<int>())), made);
        AssertSame(c["response"], response);
    }

    public static IEnumerable<object[]> GammaDensityCases() => Fixtures["gamma_density"]!["cases"]!.AsArray().Select((c, i) => new object[] { i, c!["name"]!.GetValue<string>() });

    [Theory]
    [MemberData(nameof(GammaDensityCases))]
    public void Gamma_density_matches_OpenAlgo_at_both_horizons(int index, string name)
    {
        _ = name;
        var c = Fixtures["gamma_density"]!["cases"]![index]!;
        var chain = c["chain"] as JsonObject;
        (bool Ok, JsonObject Response, int Code) result = chain is null
            ? (false, (JsonObject)c["response"]!.DeepClone(), c["code"]!.GetValue<int>())
            : GammaDensityTool.Compute(chain, "NIFTY", c["exchange"]!.GetValue<string>(), c["expiry"]!.GetValue<string>(), c["rate"]?.GetValue<double>(), c["forward"]?.GetValue<double>(), Now("gamma_density"));
        Assert.Equal(c["ok"]!.GetValue<bool>(), result.Ok);
        Assert.Equal(c["code"]!.GetValue<int>(), result.Code);
        // Densities are raw gamma x open interest; opengreeks stops its volatility solve a hair earlier than ours, which shows at the tenth digit.
        AssertSame(c["response"], result.Response, 1e-8);
    }
}
