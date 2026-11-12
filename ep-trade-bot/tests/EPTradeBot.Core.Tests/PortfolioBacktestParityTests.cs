using System.Globalization;
using System.Text.Json.Nodes;
using EPTradeBot.Core.Portfolio;

namespace EPTradeBot.Core.Tests;

/// <summary>The whole portfolio back test against OpenAlgo's (portfolio_backtest.fixtures.json, from record_portfolio_backtest.py): every section of the answer, and every refusal.</summary>
public sealed class PortfolioBacktestParityTests
{
    private static readonly JsonObject Fixtures = JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "portfolio_backtest.fixtures.json")))!.AsObject();

    public static IEnumerable<object[]> Names() => Fixtures["cases"]!.AsArray().Select(c => new object[] { c!["name"]!.GetValue<string>() });

    private static DateOnly Date(string s) => DateOnly.ParseExact(s, "yyyy-MM-dd", CultureInfo.InvariantCulture);

    private static PriceMatrix Load(IReadOnlyList<string> symbols, string start, string end, string source)
    {
        var universe = (JsonObject)Fixtures["universe"]!;
        var from = Date(start);
        var to = Date(end);
        var columns = new List<(string, PriceSeries)>();
        foreach (var symbol in symbols)
        {
            if (!universe.ContainsKey(symbol))
                throw new PriceDataException($"{symbol}: no D history in Historify for {start}..{end}. Ingest it, or run with the broker API as the source.");
            var dates = universe[symbol]!["dates"]!.AsArray().Select(d => Date(d!.GetValue<string>())).ToArray();
            var closes = universe[symbol]!["closes"]!.AsArray().Select(c => c!.GetValue<double>()).ToArray();
            var keep = Enumerable.Range(0, dates.Length).Where(i => dates[i] >= from && dates[i] <= to).ToArray();
            if (keep.Length == 0) throw new PriceDataException($"Historify holds no D bars for {symbol} between {start} and {end}");
            columns.Add((symbol, new PriceSeries([.. keep.Select(i => dates[i])], [.. keep.Select(i => closes[i])])));
        }

        return PriceMatrix.Join(columns, source, start, end);
    }

    private static PortfolioBacktestInput Input(JsonObject request)
    {
        T? Get<T>(string key) => request[key] is JsonNode n ? n.GetValue<T>() : default;
        var holdings = request["holdings"]!.AsArray().Select(h => new PortfolioHolding(h!["symbol"]!.GetValue<string>(), h["exchange"]!.GetValue<string>(), h["weight"]!.GetValue<double>())).ToList();
        var overrides = request["charge_overrides"] is JsonObject o
            ? o.ToDictionary(kv => kv.Key, kv => (IReadOnlyDictionary<string, double?>)((JsonObject)kv.Value!).ToDictionary(p => p.Key, p => (double?)p.Value!.GetValue<double>()))
            : null;
        return new PortfolioBacktestInput(holdings, request["start_date"]!.GetValue<string>(), request["end_date"]!.GetValue<string>(), Get<string>("benchmark"), "NSE_INDEX", Get<string>("rebalance") ?? "never",
            request["drift_band"]?.GetValue<double>() ?? 0, Get<string>("cost_model") ?? "indian_equity", request["brokerage_pct"]?.GetValue<double>() ?? 0, "NSE", overrides, request["gst_rate"]?.GetValue<double>(),
            1.0, 0.5, request["mc_simulations"]?.GetValue<int>() ?? 1000, request["cost_bps"]?.GetValue<double>() ?? 0, request["slippage"]?.GetValue<double>() ?? 0,
            request["initial_capital"]?.GetValue<double>() ?? 100_000.0, request["risk_free_rate"]?.GetValue<double>() ?? 0);
    }

    private static readonly string[] Sampled = ["equity", "benchmark_equity", "series.drawdown", "series.weekly_returns", "series.rolling.sharpe", "series.rolling.volatility", "allocation.dates"];

    private static JsonArray Sample(JsonArray rows, int step = 25)
    {
        if (rows.Count <= step * 2) return rows;
        var keep = new JsonArray();
        for (var i = 0; i < rows.Count; i += step) keep.Add(rows[i]!.DeepClone());
        if ((rows.Count - 1) % step != 0) keep.Add(rows[^1]!.DeepClone());
        return keep;
    }

    private static void Thin(JsonObject payload)
    {
        foreach (var path in Sampled)
        {
            JsonNode? node = payload;
            var parts = path.Split('.');
            foreach (var part in parts[..^1]) node = (node as JsonObject)?[part];
            if (node is JsonObject holder && holder[parts[^1]] is JsonArray rows) holder[parts[^1]] = Sample(rows);
        }

        if (payload["allocation"]?["series"] is JsonObject series)
            foreach (var key in series.Select(k => k.Key).ToList()) series[key] = Sample((JsonArray)series[key]!);
        if (payload["rebalancing_sweep"]?["curves"] is JsonObject curves)
            foreach (var key in curves.Select(k => k.Key).ToList()) curves[key] = Sample((JsonArray)curves[key]!);
    }

    [Theory]
    [MemberData(nameof(Names))]
    public void The_backtest_matches_OpenAlgo(string name)
    {
        var fixture = Fixtures["cases"]!.AsArray().Single(c => c!["name"]!.GetValue<string>() == name)!.AsObject();
        var input = Input((JsonObject)fixture["request"]!);
        var names = ((JsonObject)Fixtures["names"]!).ToDictionary(kv => kv.Key, kv => kv.Value!.GetValue<string>());
        var (ok, response, code) = PortfolioBacktestService.Run(input, (symbols, _, start, end, source, _) => Load(symbols, start, end, source), names);

        Assert.Equal(fixture["status"]!.GetValue<int>(), code);
        Assert.Equal(fixture["ok"]!.GetValue<bool>(), ok);
        if (ok) Thin(response);
        ToolsParityTests.AssertSame(fixture["payload"], response, 1e-9);
    }
}
