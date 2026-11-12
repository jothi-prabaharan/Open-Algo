using System.Globalization;
using System.Text.Json.Nodes;
using EPTradeBot.Core.Portfolio;

namespace EPTradeBot.Core.Tests;

/// <summary>The portfolio engine against OpenAlgo's own (portfolio_engine.fixtures.json, from record_portfolio_engine.py): drift and reset, rebalance sessions, turnover, costs and the per-holding lines.</summary>
public sealed class PortfolioEngineParityTests
{
    private static readonly JsonObject Fixtures = JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "portfolio_engine.fixtures.json")))!.AsObject();

    public static IEnumerable<object[]> Names() => Fixtures["scenarios"]!.AsArray().Select(s => new object[] { s!["name"]!.GetValue<string>() });

    private static PriceMatrix Prices() => new(
        Fixtures["dates"]!.AsArray().Select(d => DateOnly.ParseExact(d!.GetValue<string>(), "yyyy-MM-dd", CultureInfo.InvariantCulture)).ToArray(),
        Fixtures["symbols"]!.AsArray().Select(s => s!.GetValue<string>()).ToArray(),
        Fixtures["closes"]!.AsArray().Select(r => r!.AsArray().Select(v => v!.GetValue<double>()).ToArray()).ToArray(), "db");

    private static ITradeCosts Costs(JsonObject spec)
    {
        if (spec["kind"]!.GetValue<string>() == "flat") return new FlatCosts(spec["bps"]!.GetValue<double>(), spec["slippage"]!.GetValue<double>()).Validate();
        var overrides = ((JsonObject)spec["overrides"]!).ToDictionary(kv => kv.Key, kv => (IReadOnlyDictionary<string, double?>)((JsonObject)kv.Value!).ToDictionary(p => p.Key, p => (double?)p.Value!.GetValue<double>()));
        var schedule = CostSchedule.For(spec["name"]!.GetValue<string>(), overrides);
        return (schedule with { Slippage = spec["slippage"]!.GetValue<double>() }).Validate();
    }

    private static JsonArray Sample(double[] values, int step = 25) => Sample(values.Select(v => (JsonNode)JsonValue.Create(v)).ToArray(), step);

    private static JsonArray Sample(JsonNode[] values, int step)
    {
        var rows = new JsonArray();
        var indexes = Enumerable.Range(0, values.Length).Where(i => i % step == 0).ToList();
        if (indexes[^1] != values.Length - 1) indexes.Add(values.Length - 1);
        foreach (var i in indexes) rows.Add(new JsonArray(JsonValue.Create(i), values[i].DeepClone()));
        return rows;
    }

    [Theory]
    [MemberData(nameof(Names))]
    public void The_engine_matches_OpenAlgo(string name)
    {
        var fixture = Fixtures["scenarios"]!.AsArray().Single(s => s!["name"]!.GetValue<string>() == name)!.AsObject();
        var weights = ((JsonObject)fixture["weights"]!).ToDictionary(kv => kv.Key, kv => kv.Value!.GetValue<double>());
        PortfolioRunResult? run = null;
        string? error = null;
        try
        {
            run = PortfolioEngine.Run(Prices(), weights, new RebalancePolicy(fixture["rule"]!.GetValue<string>(), fixture["drift_band"]!.GetValue<double>()), Costs((JsonObject)fixture["costs"]!), fixture["capital"]!.GetValue<double>());
        }
        catch (ArgumentException e)
        {
            error = e.Message;
        }

        if (!fixture["ok"]!.GetValue<bool>())
        {
            Assert.Equal(fixture["error"]!.GetValue<string>(), error);
            return;
        }

        Assert.Null(error);
        var actual = new JsonObject
        {
            ["equity"] = Sample(run!.Equity),
            ["weights_path"] = Sample(run.Weights.Select(r => (JsonNode)new JsonArray(r.Select(v => (JsonNode)JsonValue.Create(v)).ToArray())).ToArray(), 25),
            ["rebalance_dates"] = new JsonArray(run.RebalanceDates.Select(d => (JsonNode)d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)).ToArray()),
            ["turnover"] = new JsonArray(run.Turnover.Select(v => (JsonNode)JsonValue.Create(v)).ToArray()),
            ["cost_drag"] = run.CostDrag,
            ["cost_breakdown"] = new JsonObject(run.CostBreakdown.Select(kv => new KeyValuePair<string, JsonNode?>(kv.Key, kv.Value))),
            ["orders"] = run.Orders,
            ["items"] = new JsonArray(run.Items.Select(i => (JsonNode)new JsonObject
            {
                ["symbol"] = i.Symbol, ["weight_target"] = i.WeightTarget, ["weight_final"] = i.WeightFinal, ["invested"] = i.Invested, ["price_pnl"] = i.PricePnl, ["costs"] = i.Costs,
                ["net_pnl"] = i.NetPnl, ["contribution_pct"] = i.ContributionPct, ["symbol_return"] = i.SymbolReturn,
            }).ToArray()),
        };
        var expected = new JsonObject();
        foreach (var key in actual.Select(k => k.Key)) expected[key] = fixture[key]!.DeepClone();
        ToolsParityTests.AssertSame(expected, actual, 1e-9);
    }
}
