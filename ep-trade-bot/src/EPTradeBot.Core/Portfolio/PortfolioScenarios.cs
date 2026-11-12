using System.Globalization;
using System.Text.Json.Nodes;
using EPTradeBot.Core.Options;

namespace EPTradeBot.Core.Portfolio;

/// <summary>Does the answer survive doubt: the same allocation under each rebalancing rule, over rolling windows, and over resampled histories.</summary>
public static class PortfolioScenarios
{
    private static string Iso(DateOnly d) => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    public static ReturnSeries ReturnsOf(PortfolioRunResult run) => ReturnSeries.FromCloses(run.Prices.Dates, run.Equity);

    // ---- rebalancing sweep -----------------------------------------------------------------------------------

    public static JsonObject RebalancingSweep(PriceMatrix prices, IReadOnlyDictionary<string, double> weights, ITradeCosts costs, double initialCapital, double rf)
    {
        var variants = new List<(string Label, RebalancePolicy Policy)>
        {
            ("Never", new RebalancePolicy("never")), ("Yearly", new RebalancePolicy("yearly")), ("Quarterly", new RebalancePolicy("quarterly")), ("Monthly", new RebalancePolicy("monthly")),
            ($"Drift {(int)(0.05 * 100)}%", new RebalancePolicy("never", 0.05)),
        };
        var rows = new List<(string Label, double Sharpe, double TotalReturn, JsonObject Row)>();
        var curves = new JsonObject();
        foreach (var (label, policy) in variants)
        {
            var run = PortfolioEngine.Run(prices, weights, policy, costs, initialCapital);
            var returns = ReturnsOf(run).Values;
            var sharpe = ReturnStats.Sharpe(returns, rf);
            var row = new JsonObject
            {
                ["label"] = label, ["rule"] = policy.Rule, ["drift_band"] = policy.DriftBand, ["total_return"] = run.TotalReturn, ["cagr"] = ReturnStats.Clean(ReturnStats.Cagr(returns)),
                ["volatility"] = ReturnStats.Clean(ReturnStats.Volatility(returns)), ["sharpe"] = ReturnStats.Clean(sharpe), ["sortino"] = ReturnStats.Clean(ReturnStats.Sortino(returns, rf)),
                ["max_drawdown"] = ReturnStats.Clean(ReturnStats.MaxDrawdownOfReturns(returns)), ["calmar"] = ReturnStats.Clean(ReturnStats.Calmar(returns)), ["cost_drag"] = run.CostDrag,
                ["turnover"] = run.Turnover.Sum(), ["rebalances"] = run.RebalanceDates.Count,
            };
            rows.Add((label, sharpe, run.TotalReturn, row));
            var step = Math.Max(1, run.Equity.Length / 300);
            var curve = new JsonArray();
            for (var i = 0; i < run.Equity.Length; i += step) curve.Add(new JsonObject { ["date"] = Iso(prices.Dates[i]), ["value"] = PyNumber.Round(run.Equity[i], 2) });
            curves[label] = curve;
        }

        // Ranked on Sharpe (NaN last); the first of equals wins, as a stable descending sort does.
        var bySharpe = rows.Select((r, i) => (r, i)).OrderByDescending(x => double.IsNaN(x.r.Sharpe) ? -9e9 : x.r.Sharpe).ThenBy(x => x.i).First().r.Label;
        var byReturn = rows.Select((r, i) => (r, i)).OrderByDescending(x => x.r.TotalReturn).ThenBy(x => x.i).First().r.Label;
        return new JsonObject
        {
            ["variants"] = new JsonArray(rows.Select(r => (JsonNode)r.Row).ToArray()), ["curves"] = curves, ["best_by_sharpe"] = bySharpe, ["best_by_return"] = byReturn,
            ["sessions"] = prices.Sessions, ["start"] = Iso(prices.Dates[0]), ["end"] = Iso(prices.Dates[^1]),
        };
    }

    // ---- walk forward ----------------------------------------------------------------------------------------

    public static JsonObject WalkForward(PriceMatrix prices, IReadOnlyDictionary<string, double> weights, RebalancePolicy policy, ITradeCosts costs, double initialCapital, double windowYears, double stepYears)
    {
        var window = (int)(windowYears * ReturnStats.TradingDays);
        var step = Math.Max(1, (int)(stepYears * ReturnStats.TradingDays));
        if (prices.Sessions < window + 1)
            return new JsonObject { ["windows"] = new JsonArray(), ["summary"] = null, ["note"] = $"{prices.Sessions} sessions is shorter than the {window}-session window" };

        var windows = new JsonArray();
        var totals = new List<double>();
        for (var begin = 0; begin <= prices.Sessions - window; begin += step)
        {
            var sub = new PriceMatrix(prices.Dates[begin..(begin + window)], prices.Symbols, prices.Closes[begin..(begin + window)], prices.Source);
            var run = PortfolioEngine.Run(sub, weights, policy, costs, initialCapital);
            var equity = run.Equity;
            var years = window / (double)ReturnStats.TradingDays;
            var total = equity[^1] / equity[0] - 1.0;
            var peak = double.MinValue;
            var worst = 0.0;
            foreach (var e in equity) { peak = Math.Max(peak, e); worst = Math.Min(worst, e / peak - 1.0); }
            totals.Add(total);
            windows.Add(new JsonObject
            {
                ["start"] = Iso(sub.Dates[0]), ["end"] = Iso(sub.Dates[^1]), ["total_return"] = ReturnStats.Clean(total),
                ["cagr"] = ReturnStats.Clean(years > 0 ? Math.Pow(1.0 + total, 1.0 / years) - 1.0 : double.NaN), ["max_drawdown"] = ReturnStats.Clean(worst), ["sessions"] = window,
            });
        }

        return new JsonObject
        {
            ["windows"] = windows,
            ["summary"] = new JsonObject
            {
                ["count"] = totals.Count, ["window_years"] = windowYears, ["step_years"] = stepYears, ["best"] = ReturnStats.Clean(totals.Max()), ["worst"] = ReturnStats.Clean(totals.Min()),
                ["median"] = ReturnStats.Clean(ReturnStats.Median(totals)), ["positive_share"] = ReturnStats.Clean(totals.Count(t => t > 0) / (double)totals.Count), ["spread"] = ReturnStats.Clean(totals.Max() - totals.Min()),
            },
            ["note"] = null,
        };
    }

    // ---- monte carlo -----------------------------------------------------------------------------------------

    public static JsonObject MonteCarlo(ReturnSeries returns, int simulations = 1000, int block = 20, uint seed = 12345)
    {
        var values = returns.Values.Where(v => !double.IsNaN(v)).ToArray();
        var n = values.Length;
        if (n < block * 2) return new JsonObject { ["note"] = $"{n} sessions is too few for {block}-session blocks", ["paths"] = 0 };

        var rng = new NumpyRandom(seed);
        var finals = new double[simulations];
        var drawdowns = new double[simulations];
        var blocks = Math.Max(1, (int)Math.Ceiling(n / (double)block));
        for (var i = 0; i < simulations; i++)
        {
            var path = new List<double>(blocks * block);
            var starts = new long[blocks];
            for (var b = 0; b < blocks; b++) starts[b] = rng.Integer(0, n - block + 1);
            foreach (var s in starts) for (var k = 0; k < block; k++) path.Add(values[s + k]);
            // The running peak starts at the first compounded value, not at 1: a path that opens with a loss has no earlier high to fall from.
            double level = 1, peak = double.MinValue, worst = double.MaxValue;
            for (var k = 0; k < n; k++)
            {
                level *= 1.0 + path[k];
                peak = Math.Max(peak, level);
                worst = Math.Min(worst, level / peak - 1.0);
            }

            finals[i] = level - 1.0;
            drawdowns[i] = worst;
        }

        var years = n / (double)ReturnStats.TradingDays;
        var cagrs = finals.Select(f => Math.Sign(1.0 + f) * Math.Pow(Math.Abs(1.0 + f), 1.0 / years) - 1.0).ToArray();
        JsonObject Band(double[] a) => new()
        {
            ["p05"] = ReturnStats.Clean(ReturnStats.Quantile(a, 0.05)), ["p25"] = ReturnStats.Clean(ReturnStats.Quantile(a, 0.25)), ["median"] = ReturnStats.Clean(ReturnStats.Median(a)),
            ["p75"] = ReturnStats.Clean(ReturnStats.Quantile(a, 0.75)), ["p95"] = ReturnStats.Clean(ReturnStats.Quantile(a, 0.95)),
        };

        return new JsonObject
        {
            ["paths"] = simulations, ["block"] = block, ["seed"] = (int)seed, ["total_return"] = Band(finals), ["cagr"] = Band(cagrs), ["max_drawdown"] = Band(drawdowns),
            ["probability_of_loss"] = ReturnStats.Clean(finals.Count(f => f < 0) / (double)simulations), ["worst_drawdown_seen"] = ReturnStats.Clean(drawdowns.Min()), ["note"] = null,
        };
    }
}
