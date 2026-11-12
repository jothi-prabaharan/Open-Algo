using System.Globalization;
using System.Text.Json.Nodes;
using EPTradeBot.Core.Options;

namespace EPTradeBot.Core.Portfolio;

public sealed record PortfolioHolding(string Symbol, string Exchange, double Weight);

public sealed record PortfolioBacktestInput(
    IReadOnlyList<PortfolioHolding> Holdings, string StartDate, string EndDate, string? Benchmark = null, string BenchmarkExchange = "NSE_INDEX", string Rebalance = "never", double DriftBand = 0,
    string CostModel = "indian_equity", double BrokeragePct = 0, string CostExchange = "NSE", IReadOnlyDictionary<string, IReadOnlyDictionary<string, double?>>? ChargeOverrides = null, double? GstRate = null,
    double WalkWindowYears = 1.0, double WalkStepYears = 0.5, int McSimulations = 1000, double CostBps = 0, double Slippage = 0, double InitialCapital = 100_000.0, double RiskFreeRate = 0, string Source = "db");

/// <summary>
/// The portfolio back test as the tab consumes it: the engine, the statistics and every diagnostic assembled into one answer.
/// OpenAlgo's services.portfolio_service.run_portfolio_backtest, with price loading passed in.
/// </summary>
public static class PortfolioBacktestService
{
    public const int MaxSymbols = 50;

    /// <summary>Loads aligned daily closes for symbols on exchanges; throws <see cref="PriceDataException"/> when the data cannot answer.</summary>
    public delegate PriceMatrix MatrixLoader(IReadOnlyList<string> symbols, IReadOnlyList<string> exchanges, string start, string end, string source, bool benchmark);

    private static (bool, JsonObject, int) Error(string message, int status) => (false, new JsonObject { ["status"] = "error", ["message"] = message }, status);

    private static string Iso(DateOnly d) => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    /// <summary>OpenAlgo's _clean: NaN and infinity become null, floats are rounded to six places, whole numbers stay as they are.</summary>
    public static JsonNode? CleanTree(JsonNode? node)
    {
        switch (node)
        {
            case JsonObject o: return new JsonObject(o.Select(kv => new KeyValuePair<string, JsonNode?>(kv.Key, CleanTree(kv.Value))));
            case JsonArray a: return new JsonArray(a.Select(CleanTree).ToArray());
            case JsonValue v when v.TryGetValue<int>(out _) || v.TryGetValue<long>(out _): return v.DeepClone();
            case JsonValue v when v.TryGetValue<double>(out var d): return double.IsFinite(d) ? JsonValue.Create(PyNumber.Round(d, 6)) : null;
            default: return node?.DeepClone();
        }
    }

    private static JsonArray Curve(IReadOnlyList<DateOnly> dates, IReadOnlyList<double> values) =>
        new(values.Select((v, i) => (JsonNode)new JsonObject { ["date"] = Iso(dates[i]), ["value"] = PyNumber.Round(v, 4) }).ToArray());

    public static ITradeCosts BuildCosts(string costModel, double brokeragePct, string costExchange, IReadOnlyDictionary<string, IReadOnlyDictionary<string, double?>>? overrides, double? gstRate, double costBps, double slippage)
    {
        if (costModel == "flat_bps") return new FlatCosts(costBps, slippage).Validate();
        var edits = (overrides ?? new Dictionary<string, IReadOnlyDictionary<string, double?>>()).ToDictionary(kv => kv.Key, kv => (IReadOnlyDictionary<string, double?>)new Dictionary<string, double?>(kv.Value));
        if (brokeragePct != 0 && !edits.ContainsKey("brokerage")) edits["brokerage"] = new Dictionary<string, double?> { ["flat"] = 0.0, ["rate"] = brokeragePct, ["cap"] = 20.0 };
        var schedule = CostSchedule.For($"india_delivery_{costExchange.ToLowerInvariant()}", edits);
        return (schedule with { Slippage = slippage, TaxRate = gstRate ?? schedule.TaxRate }).Validate();
    }

    public static (bool Ok, JsonObject Response, int Code) Run(PortfolioBacktestInput r, MatrixLoader load, IReadOnlyDictionary<string, string> names)
    {
        if (r.Holdings.Count == 0) return Error("no holdings supplied", 400);
        if (r.Holdings.Count > MaxSymbols) return Error($"{r.Holdings.Count} holdings exceeds the {MaxSymbols} limit", 400);
        var symbols = r.Holdings.Select(h => h.Symbol.Trim().ToUpperInvariant()).ToList();
        var exchanges = r.Holdings.Select(h => h.Exchange.Trim().ToUpperInvariant()).ToList();
        if (symbols.Distinct().Count() != symbols.Count) return Error("duplicate symbol in holdings", 400);
        var weights = new Dictionary<string, double>();
        for (var i = 0; i < symbols.Count; i++) weights[symbols[i]] = r.Holdings[i].Weight;

        PriceMatrix prices;
        PortfolioRunResult result;
        RebalancePolicy policy;
        ITradeCosts charged;
        try
        {
            prices = load(symbols, exchanges, r.StartDate, r.EndDate, r.Source, false);
            policy = new RebalancePolicy(r.Rebalance, r.DriftBand).Validate();
            charged = BuildCosts(r.CostModel, r.BrokeragePct, r.CostExchange, r.ChargeOverrides, r.GstRate, r.CostBps, r.Slippage);
            result = PortfolioEngine.Run(prices, weights, policy, charged, r.InitialCapital);
        }
        catch (PriceDataException e)
        {
            return Error(e.Message, 422);
        }
        catch (ArgumentException e)
        {
            return Error(e.Message, 400);
        }

        ReturnSeries? benchReturns = null;
        JsonArray benchCurve = [];
        if (!string.IsNullOrEmpty(r.Benchmark))
        {
            try
            {
                var bench = load([r.Benchmark.Trim().ToUpperInvariant()], [r.BenchmarkExchange], r.StartDate, r.EndDate, r.Source, true);
                // Aligned to the portfolio's sessions, carrying the last close over a missing day; sessions before the benchmark began are dropped.
                var lookup = bench.Dates.Select((d, i) => (d, i)).ToDictionary(x => x.d, x => bench.Closes[x.i][0]);
                var dates = new List<DateOnly>();
                var closes = new List<double>();
                double? carried = null;
                foreach (var d in prices.Dates)
                {
                    if (lookup.TryGetValue(d, out var c)) carried = c;
                    if (carried is { } v) { dates.Add(d); closes.Add(v); }
                }

                if (closes.Count > 1)
                {
                    benchReturns = ReturnSeries.FromCloses([.. dates], [.. closes]);
                    benchCurve = Curve(dates, closes.Select(c => c / closes[0] * r.InitialCapital).ToArray());
                }
            }
            catch (PriceDataException)
            {
                // A missing benchmark degrades the report; it does not invalidate the portfolio.
            }
        }

        var returns = PortfolioScenarios.ReturnsOf(result);
        var holdingReturns = HoldingReturns.From(prices);
        var targetWeights = result.TargetWeights;
        var symbolsArray = prices.Symbols;
        var corr = PortfolioDiagnostics.CorrelationMatrix(holdingReturns, 20);
        var metrics = ReturnStats.Headline(returns, benchReturns, r.RiskFreeRate);
        double MetricOrNaN(string key) => metrics[key] is JsonValue v && v.TryGetValue<double>(out var d) && d != 0 ? d : double.NaN;

        var turnoverTotal = result.Turnover.Sum();
        var contributions = result.Items.ToDictionary(i => i.Symbol, i => i.ContributionPct);
        var sweep = PortfolioScenarios.RebalancingSweep(prices, weights, charged, r.InitialCapital, r.RiskFreeRate);
        var concentration = PortfolioDiagnostics.Concentration(targetWeights);
        concentration["diversification_ratio"] = PortfolioDiagnostics.DiversificationRatio(targetWeights, holdingReturns);

        var meta = new JsonObject
        {
            ["symbols"] = new JsonArray(symbolsArray.Select(s => (JsonNode)s).ToArray()),
            ["target_weights"] = new JsonObject(symbolsArray.Select((s, i) => new KeyValuePair<string, JsonNode?>(s, targetWeights[i]))),
            ["rule"] = policy.Rule, ["drift_band"] = policy.DriftBand,
            ["cost_model"] = charged is CostSchedule cs ? new JsonObject { ["kind"] = "schedule", ["name"] = cs.Name } : new JsonObject { ["kind"] = "flat_bps", ["bps"] = ((FlatCosts)charged).Bps },
            ["orders"] = result.Orders, ["slippage"] = charged is CostSchedule c2 ? c2.Slippage : ((FlatCosts)charged).Slippage, ["initial_capital"] = r.InitialCapital, ["sessions"] = prices.Sessions,
            ["source"] = prices.Source, ["start"] = Iso(prices.Dates[0]), ["end"] = Iso(prices.Dates[^1]), ["benchmark"] = r.Benchmark, ["risk_free_rate"] = r.RiskFreeRate, ["total_return_basis"] = "price",
            ["data_warnings"] = new JsonObject(prices.Warnings.Select(w => new KeyValuePair<string, JsonNode?>(w.Key, new JsonArray(w.Value.Select(h => (JsonNode)new JsonArray(Iso(h.Date), h.Move)).ToArray())))),
        };

        var payload = new JsonObject
        {
            ["status"] = "success",
            ["meta"] = meta,
            ["equity"] = Curve(prices.Dates, result.Equity),
            ["benchmark_equity"] = benchCurve,
            ["metrics"] = metrics,
            ["items"] = CleanTree(new JsonArray(result.Items.Select(i => (JsonNode)new JsonObject
            {
                ["symbol"] = i.Symbol, ["weight_target"] = i.WeightTarget, ["weight_final"] = i.WeightFinal, ["invested"] = i.Invested, ["price_pnl"] = i.PricePnl, ["costs"] = i.Costs,
                ["net_pnl"] = i.NetPnl, ["contribution_pct"] = i.ContributionPct, ["symbol_return"] = i.SymbolReturn,
            }).ToArray())),
            ["correlation"] = CleanTree(new JsonObject
            {
                ["symbols"] = new JsonArray(symbolsArray.Select(s => (JsonNode)s).ToArray()),
                ["matrix"] = new JsonArray(corr.Select(row => (JsonNode)new JsonArray(row.Select(v => (JsonNode)JsonValue.Create(v)).ToArray())).ToArray()),
                ["average_pairwise"] = PortfolioDiagnostics.AveragePairwise(holdingReturns),
            }),
            ["diversification"] = CleanTree(concentration),
            ["series"] = ReturnStats.SeriesAnalytics(returns, r.RiskFreeRate, benchReturns),
            ["walk_forward"] = CleanTree(PortfolioScenarios.WalkForward(prices, weights, policy, charged, r.InitialCapital, r.WalkWindowYears, r.WalkStepYears)),
            ["monte_carlo"] = CleanTree(PortfolioScenarios.MonteCarlo(returns, r.McSimulations)),
            ["rebalancing_sweep"] = CleanTree(sweep),
            ["attribution"] = CleanTree(PortfolioDiagnostics.Attribution(holdingReturns, result.Weights, prices.Dates, symbolsArray, targetWeights, symbolsArray, returns, benchReturns, contributions)),
            ["asset_returns"] = CleanTree(AssetReturns(prices)),
            ["structure"] = CleanTree(PortfolioDiagnostics.Structure(symbolsArray, targetWeights, holdingReturns, names)),
            ["allocation"] = CleanTree(AllocationPath(result.Weights, prices.Dates, symbolsArray)),
            ["crisis"] = CleanTree(PortfolioDiagnostics.Crisis(returns, benchReturns)),
            ["health"] = PortfolioDiagnostics.Health(symbolsArray, targetWeights, holdingReturns, prices, MetricOrNaN("sharpe"), MetricOrNaN("sortino"), MetricOrNaN("max_drawdown"), result.CostDrag, turnoverTotal),
        };

        var costs = new JsonObject { ["model"] = r.CostModel };
        foreach (var (key, value) in result.CostBreakdown) costs[key == "tax" ? "gst" : key] = value;
        costs["schedule"] = charged is CostSchedule named ? named.Name : "flat bps";
        costs["drag"] = result.CostDrag;
        costs["turnover"] = turnoverTotal;
        payload["costs"] = CleanTree(costs);
        payload["rebalancing"] = new JsonObject
        {
            ["rule"] = r.Rebalance, ["drift_band"] = r.DriftBand, ["count"] = result.RebalanceDates.Count, ["cost_drag"] = ReturnStats.Clean(result.CostDrag), ["turnover_total"] = ReturnStats.Clean(turnoverTotal),
            ["dates"] = new JsonArray(result.RebalanceDates.Select(d => (JsonNode)Iso(d)).ToArray()),
        };
        payload["insights"] = CleanTree(PortfolioInsights.Build(payload));
        return (true, payload, 200);
    }

    /// <summary>Trailing close-to-close return per holding; a window longer than the history is null rather than the since-start figure.</summary>
    public static JsonArray AssetReturns(PriceMatrix prices)
    {
        var windows = new (string Label, int Sessions)[] { ("1W", 5), ("1M", 21), ("3M", 63), ("1Y", 252), ("3Y", 756), ("5Y", 1260) };
        var rows = new List<(double? OneYear, JsonObject Row)>();
        for (var j = 0; j < prices.Symbols.Length; j++)
        {
            var closes = prices.Closes.Select(r => r[j]).ToArray();
            var row = new JsonObject { ["symbol"] = prices.Symbols[j], ["last"] = PyNumber.Round(closes[^1], 2) };
            double? oneYear = null;
            foreach (var (label, sessions) in windows)
            {
                if (closes.Length > sessions)
                {
                    var start = closes[^(sessions + 1)];
                    var value = start != 0 ? PyNumber.Round(closes[^1] / start - 1.0, 6) : (double?)null;
                    row[label] = value;
                    if (label == "1Y") oneYear = value;
                }
                else
                {
                    row[label] = null;
                }
            }

            rows.Add((oneYear, row));
        }

        // Best one-year return first; holdings without one go last.
        return new JsonArray(rows.Select((x, i) => (x, i)).OrderBy(t => t.x.OneYear is null).ThenBy(t => -(t.x.OneYear ?? 0)).ThenBy(t => t.i).Select(t => (JsonNode)t.x.Row).ToArray());
    }

    /// <summary>The weight path thinned to about 400 points, always ending on the last session.</summary>
    public static JsonObject AllocationPath(double[][] weights, DateOnly[] dates, string[] symbols)
    {
        if (weights.Length == 0) return new JsonObject { ["dates"] = new JsonArray(), ["symbols"] = new JsonArray(), ["series"] = new JsonObject(), ["average"] = new JsonObject() };
        var step = Math.Max(1, weights.Length / 400);
        var picked = Enumerable.Range(0, weights.Length).Where(i => i % step == 0).ToList();
        if (picked[^1] != weights.Length - 1) picked.Add(weights.Length - 1);
        return new JsonObject
        {
            ["dates"] = new JsonArray(picked.Select(i => (JsonNode)Iso(dates[i])).ToArray()),
            ["symbols"] = new JsonArray(symbols.Select(s => (JsonNode)s).ToArray()),
            ["series"] = new JsonObject(symbols.Select((s, j) => new KeyValuePair<string, JsonNode?>(s, new JsonArray(picked.Select(i => (JsonNode)PyNumber.Round(weights[i][j], 5)).ToArray())))),
            ["average"] = new JsonObject(symbols.Select((s, j) => new KeyValuePair<string, JsonNode?>(s, PyNumber.Round(weights.Average(w => w[j]), 5)))),
        };
    }
}
