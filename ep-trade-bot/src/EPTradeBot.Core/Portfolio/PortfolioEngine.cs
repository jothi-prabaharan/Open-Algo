namespace EPTradeBot.Core.Portfolio;

/// <summary>Calendar-aligned closes for several symbols: one row per session, one column per symbol, every close positive and finite.</summary>
public sealed record PriceMatrix(DateOnly[] Dates, string[] Symbols, double[][] Closes, string Source)
{
    public int Sessions => Dates.Length;

    /// <summary>Inner join of single-symbol series on their common sessions, as OpenAlgo aligns them.</summary>
    public static PriceMatrix Join(IReadOnlyList<(string Symbol, PriceSeries Series)> columns, string source)
    {
        if (columns.Count == 0) throw new PriceDataException("no symbols requested");
        var common = columns[0].Series.Dates.ToHashSet();
        foreach (var (_, s) in columns.Skip(1)) common.IntersectWith(s.Dates);
        var dates = common.Order().ToArray();
        var rows = new double[dates.Length][];
        var lookup = columns.Select(c => c.Series.Dates.Select((d, i) => (d, i)).ToDictionary(x => x.d, x => c.Series.Closes[x.i])).ToArray();
        for (var r = 0; r < dates.Length; r++) rows[r] = [.. lookup.Select(l => l[dates[r]])];
        return new PriceMatrix(dates, [.. columns.Select(c => c.Symbol)], rows, source);
    }
}

/// <summary>When a portfolio resets to target: never, or on the last session of each month, quarter or year, plus an optional drift band (a fraction: 0.05 is five points).</summary>
public sealed record RebalancePolicy(string Rule = "never", double DriftBand = 0)
{
    public static readonly string[] Rules = ["never", "monthly", "quarterly", "yearly"];

    public RebalancePolicy Validate()
    {
        if (!Rules.Contains(Rule)) throw new ArgumentException($"unknown rebalance rule '{Rule}'; expected one of [{string.Join(", ", Rules.Order(StringComparer.Ordinal).Select(r => $"'{r}'"))}]");
        if (!(DriftBand >= 0.0 && DriftBand < 1.0)) throw new ArgumentException($"drift_band is a fraction of 1.0 (0.05 = 5 percentage points), got {DriftBand}");
        return this;
    }

    /// <summary>The last session of each period in <paramref name="dates"/>, never the first session (that is the purchase).</summary>
    public static HashSet<DateOnly> CalendarDates(IReadOnlyList<DateOnly> dates, string rule)
    {
        var result = new HashSet<DateOnly>();
        if (rule == "never" || dates.Count == 0) return result;
        object Key(DateOnly d) => rule switch { "monthly" => (d.Year, d.Month), "quarterly" => (d.Year, (d.Month - 1) / 3), _ => (d.Year, 0) };
        for (var i = 0; i < dates.Count; i++)
        {
            if (i == dates.Count - 1 || !Key(dates[i + 1]).Equals(Key(dates[i]))) result.Add(dates[i]);
        }

        result.Remove(dates[0]);
        return result;
    }
}

/// <summary>One holding's line in a run: what it made, what it cost and what it contributed.</summary>
public sealed record PortfolioItem(string Symbol, double WeightTarget, double WeightFinal, double Invested, double PricePnl, double Costs, double NetPnl, double ContributionPct, double SymbolReturn);

public sealed class PortfolioRunResult
{
    public required double[] Equity { get; init; }

    public required double[][] Weights { get; init; }

    public required PriceMatrix Prices { get; init; }

    public required List<DateOnly> RebalanceDates { get; init; }

    public required List<double> Turnover { get; init; }

    public required double CostDrag { get; init; }

    public required List<KeyValuePair<string, double>> CostBreakdown { get; init; }

    public required List<PortfolioItem> Items { get; init; }

    public required int Orders { get; init; }

    public required double InitialCapital { get; init; }

    public required double[] TargetWeights { get; init; }

    public double TotalReturn => Equity[^1] / Equity[0] - 1.0;
}

/// <summary>
/// The portfolio back test: target weights drift with prices and snap back on the rebalance sessions, with costs charged on the traded value and
/// reported. OpenAlgo's portfolio.engine.run_backtest.
/// </summary>
public static class PortfolioEngine
{
    public static double[] NormaliseWeights(IReadOnlyDictionary<string, double> weights, IReadOnlyList<string> symbols)
    {
        var missing = symbols.Where(s => !weights.ContainsKey(s)).ToList();
        if (missing.Count > 0) throw new ArgumentException($"no weight given for {string.Join(", ", missing)}");
        var extra = weights.Keys.Where(k => !symbols.Contains(k)).ToList();
        if (extra.Count > 0) throw new ArgumentException($"weight given for unheld symbol(s): {string.Join(", ", extra)}");
        var vector = symbols.Select(s => weights[s]).ToArray();
        if (vector.Any(v => !double.IsFinite(v))) throw new ArgumentException("weights must be finite");
        if (vector.Any(v => v < 0)) throw new ArgumentException("negative weights are not supported; this is a long-only engine");
        var total = vector.Sum();
        if (total <= 0) throw new ArgumentException("weights sum to zero");
        return [.. vector.Select(v => v / total)];
    }

    public static PortfolioRunResult Run(PriceMatrix prices, IReadOnlyDictionary<string, double> weights, RebalancePolicy policy, ITradeCosts costs, double initialCapital = 100_000.0)
    {
        policy.Validate();
        if (!double.IsFinite(initialCapital) || initialCapital <= 0) throw new ArgumentException("initial capital must be positive and finite");
        var symbols = prices.Symbols;
        var n = symbols.Length;
        var target = NormaliseWeights(weights, symbols);
        if (prices.Closes.Any(row => row.Any(v => !double.IsFinite(v) || v <= 0))) throw new ArgumentException("price matrix must contain only positive finite closes");

        var scheduled = RebalancePolicy.CalendarDates(prices.Dates, policy.Rule);
        var equity = new double[prices.Sessions];
        var weightPath = new double[prices.Sessions][];
        var rebalanceDates = new List<DateOnly>();
        var turnover = new List<double>();
        var orderCount = 0;
        var breakdown = new List<KeyValuePair<string, double>>();
        void Add(string key, double amount)
        {
            var at = breakdown.FindIndex(k => k.Key == key);
            if (at < 0) breakdown.Add(new(key, amount));
            else breakdown[at] = new(key, breakdown[at].Value + amount);
        }

        if (costs is CostSchedule zero) foreach (var line in zero.Breakdown(0.0, 0.0, 0)) Add(line.Key, 0.0);
        else { Add("total", 0.0); Add("orders", 0.0); }

        var sleeve = target.Select(w => w * initialCapital).ToArray();
        var pricePnl = new double[n];
        var costPaid = new double[n];
        var grossSleeve = (double[])sleeve.Clone();
        double value = 0;

        for (var i = 0; i < prices.Sessions; i++)
        {
            if (i > 0)
            {
                var grown = new double[n];
                for (var j = 0; j < n; j++)
                {
                    var growth = prices.Closes[i][j] / prices.Closes[i - 1][j];
                    grown[j] = sleeve[j] * growth;
                    pricePnl[j] += grown[j] - sleeve[j];
                    grossSleeve[j] *= growth;
                }

                sleeve = grown;
                value = sleeve.Sum();
                var held = sleeve.Select(s => s / value).ToArray();
                var drifted = policy.DriftBand > 0.0 && held.Select((h, j) => Math.Abs(h - target[j])).Max() > policy.DriftBand;
                if (scheduled.Contains(prices.Dates[i]) || drifted)
                {
                    var desired = target.Select(w => w * value).ToArray();
                    var moved = desired.Select((d, j) => Math.Abs(d - sleeve[j])).ToArray();
                    var traded = moved.Sum() / 2.0 / value;
                    if (traded > 0)
                    {
                        var tradedValue = traded * value;
                        var orders = moved.Count(m => m > 1e-9);
                        orderCount += orders;
                        double charge;
                        if (costs is CostSchedule schedule)
                        {
                            var lines = schedule.Breakdown(tradedValue, tradedValue, orders);
                            foreach (var l in lines) Add(l.Key, l.Value);
                            charge = lines.First(l => l.Key == "total").Value;
                        }
                        else
                        {
                            var flat = (FlatCosts)costs;
                            charge = tradedValue * flat.Total;
                            Add("total", charge);
                            Add("orders", orders);
                        }

                        var movedSum = moved.Sum();
                        for (var j = 0; j < n; j++) costPaid[j] += charge * (movedSum > 0 ? moved[j] / movedSum : moved[j]);
                        value -= charge;
                        rebalanceDates.Add(prices.Dates[i]);
                        turnover.Add(traded);
                    }

                    sleeve = target.Select(w => w * value).ToArray();
                    var grossTotal = grossSleeve.Sum();
                    grossSleeve = target.Select(w => w * grossTotal).ToArray();
                }
            }
            else
            {
                value = sleeve.Sum();
            }

            equity[i] = value;
            weightPath[i] = [.. sleeve.Select(s => s / value)];
        }

        var grossReturn = grossSleeve.Sum() / initialCapital - 1.0;
        var netReturn = equity[^1] / initialCapital - 1.0;
        var last = prices.Sessions - 1;
        var items = new List<PortfolioItem>();
        for (var j = 0; j < n; j++)
        {
            var net = pricePnl[j] - costPaid[j];
            items.Add(new PortfolioItem(symbols[j], target[j], weightPath[last][j], target[j] * initialCapital, pricePnl[j], costPaid[j], net, net / initialCapital, prices.Closes[last][j] / prices.Closes[0][j] - 1.0));
        }

        return new PortfolioRunResult
        {
            Equity = equity, Weights = weightPath, Prices = prices, RebalanceDates = rebalanceDates, Turnover = turnover, CostDrag = grossReturn - netReturn, CostBreakdown = breakdown,
            Items = items, Orders = orderCount, InitialCapital = initialCapital, TargetWeights = target,
        };
    }
}
