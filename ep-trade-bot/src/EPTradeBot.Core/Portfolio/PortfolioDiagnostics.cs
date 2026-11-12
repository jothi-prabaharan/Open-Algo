using System.Globalization;
using System.Text.Json.Nodes;
using EPTradeBot.Core.Options;

namespace EPTradeBot.Core.Portfolio;

/// <summary>Holding returns as columns: one row per session after the first, one column per symbol.</summary>
public sealed record HoldingReturns(DateOnly[] Dates, string[] Symbols, double[][] Rows)
{
    public static HoldingReturns From(PriceMatrix prices)
    {
        var n = prices.Sessions;
        var dates = prices.Dates.Skip(1).ToArray();
        var rows = new double[Math.Max(n - 1, 0)][];
        for (var i = 1; i < n; i++) rows[i - 1] = [.. prices.Closes[i].Select((c, j) => c / prices.Closes[i - 1][j] - 1.0)];
        return new HoldingReturns(dates, prices.Symbols, rows);
    }

    public double[] Column(int j) => [.. Rows.Select(r => r[j])];
}

/// <summary>Correlation, concentration, clustering, health and attribution: the diagnostics OpenAlgo's portfolio package adds around the engine.</summary>
public static class PortfolioDiagnostics
{
    private static JsonNode? C(double x) => ReturnStats.Clean(x);

    // ---- correlation and concentration -----------------------------------------------------------------------

    public static double Correlation(double[] a, double[] b, int minPeriods = 1)
    {
        if (a.Length < Math.Max(minPeriods, 2)) return double.NaN;
        var ma = ReturnStats.Mean(a);
        var mb = ReturnStats.Mean(b);
        double sab = 0, saa = 0, sbb = 0;
        for (var i = 0; i < a.Length; i++) { var x = a[i] - ma; var y = b[i] - mb; sab += x * y; saa += x * x; sbb += y * y; }
        var d = Math.Sqrt(saa * sbb);
        return d == 0 ? double.NaN : sab / d;
    }

    public static double[][] CorrelationMatrix(HoldingReturns r, int minPeriods = 1)
    {
        var n = r.Symbols.Length;
        var columns = Enumerable.Range(0, n).Select(r.Column).ToArray();
        var m = new double[n][];
        for (var i = 0; i < n; i++)
        {
            m[i] = new double[n];
            for (var j = 0; j < n; j++) m[i][j] = i == j ? (columns[i].Length >= minPeriods && Correlation(columns[i], columns[i]) is var d && !double.IsNaN(d) ? 1.0 : double.NaN) : Correlation(columns[i], columns[j], minPeriods);
        }

        return m;
    }

    public static double AveragePairwise(HoldingReturns r)
    {
        if (r.Symbols.Length < 2) return double.NaN;
        var m = CorrelationMatrix(r);
        var values = new List<double>();
        for (var i = 0; i < m.Length; i++) for (var j = i + 1; j < m.Length; j++) if (!double.IsNaN(m[i][j])) values.Add(m[i][j]);
        return values.Count == 0 ? double.NaN : values.Sum() / values.Count;
    }

    public static JsonObject Concentration(double[] weights)
    {
        var total = weights.Sum();
        if (total <= 0) throw new ArgumentException("weights sum to zero");
        var w = weights.Select(x => x / total).ToArray();
        var hhi = w.Sum(x => x * x);
        return new JsonObject { ["hhi"] = hhi, ["effective_holdings"] = 1.0 / hhi, ["largest_weight"] = w.Max(), ["holdings"] = w.Length };
    }

    public static double DiversificationRatio(double[] weights, HoldingReturns r)
    {
        if (r.Symbols.Length < 2) return double.NaN;
        var w = weights.Select(x => x / weights.Sum()).ToArray();
        var n = w.Length;
        var cols = Enumerable.Range(0, n).Select(r.Column).ToArray();
        var cov = new double[n, n];
        for (var i = 0; i < n; i++)
            for (var j = 0; j < n; j++)
            {
                var mi = ReturnStats.Mean(cols[i]);
                var mj = ReturnStats.Mean(cols[j]);
                double s = 0;
                for (var k = 0; k < cols[i].Length; k++) s += (cols[i][k] - mi) * (cols[j][k] - mj);
                cov[i, j] = s / (cols[i].Length - 1) * ReturnStats.TradingDays;
            }

        double quad = 0;
        for (var i = 0; i < n; i++) for (var j = 0; j < n; j++) quad += w[i] * cov[i, j] * w[j];
        var vol = Math.Sqrt(quad);
        if (vol <= 0) return double.NaN;
        double weighted = 0;
        for (var i = 0; i < n; i++) weighted += w[i] * Math.Sqrt(cov[i, i]);
        return weighted / vol;
    }

    // ---- structure -------------------------------------------------------------------------------------------

    private static readonly string[] FundMarkers = ["ETF", "BEES", "FUND", "INDEX", "NIFTY", "SENSEX", "GOLD", "SILVER"];

    public static string ClassifyInstrument(string symbol, string? name)
    {
        var haystack = $"{symbol} {name ?? string.Empty}".ToUpperInvariant();
        return FundMarkers.Any(m => haystack.Contains(m, StringComparison.Ordinal)) ? "fund" : "stock";
    }

    public static List<List<string>> CorrelationClusters(HoldingReturns r, double threshold = 0.6)
    {
        var symbols = r.Symbols;
        if (symbols.Length < 2) return [.. symbols.Select(s => new List<string> { s })];
        var corr = CorrelationMatrix(r);
        var parent = Enumerable.Range(0, symbols.Length).ToArray();
        int Find(int i) { while (parent[i] != i) { parent[i] = parent[parent[i]]; i = parent[i]; } return i; }
        for (var i = 0; i < symbols.Length; i++)
            for (var j = i + 1; j < symbols.Length; j++)
                if (double.IsFinite(corr[i][j]) && corr[i][j] >= threshold)
                {
                    int a = Find(i), b = Find(j);
                    if (a != b) parent[b] = a;
                }

        var groups = new List<(int Root, List<string> Members)>();
        for (var i = 0; i < symbols.Length; i++)
        {
            var root = Find(i);
            var at = groups.FindIndex(g => g.Root == root);
            if (at < 0) groups.Add((root, [symbols[i]])); else groups[at].Members.Add(symbols[i]);
        }

        return [.. groups.Select(g => g.Members).OrderByDescending(g => g.Count)];
    }

    public static JsonObject Structure(string[] symbols, double[] weights, HoldingReturns r, IReadOnlyDictionary<string, string> names, double threshold = 0.6)
    {
        var total = weights.Sum();
        if (total == 0) total = 1.0;
        var w = new Dictionary<string, double>();
        for (var i = 0; i < symbols.Length; i++) w[symbols[i]] = weights[i] / total;
        var classes = new List<KeyValuePair<string, double>>();
        foreach (var s in symbols)
        {
            var kind = ClassifyInstrument(s, names.GetValueOrDefault(s));
            var at = classes.FindIndex(c => c.Key == kind);
            if (at < 0) classes.Add(new(kind, w[s])); else classes[at] = new(kind, classes[at].Value + w[s]);
        }

        var rows = new List<(double Weight, JsonObject Row)>();
        var id = 0;
        foreach (var members in CorrelationClusters(r, threshold))
        {
            id++;
            var held = members.Where(w.ContainsKey).ToList();
            var weight = PyNumber.Round(held.Sum(m => w[m]), 5);
            rows.Add((weight, new JsonObject { ["id"] = id, ["members"] = new JsonArray(held.Select(m => (JsonNode)m).ToArray()), ["weight"] = weight, ["independent"] = held.Count == 1 }));
        }

        var sorted = rows.OrderByDescending(x => x.Weight).Select(x => (JsonNode)x.Row).ToArray();
        return new JsonObject
        {
            ["instrument_classes"] = new JsonObject(classes.Select(c => new KeyValuePair<string, JsonNode?>(c.Key, PyNumber.Round(c.Value, 5)))),
            ["instrument_class_basis"] = "inferred from the listing name",
            ["clusters"] = new JsonArray(sorted),
            ["threshold"] = threshold,
            ["effective_bets"] = rows.Count,
            ["largest_cluster_weight"] = PyNumber.Round(rows.Count == 0 ? 0.0 : rows.Max(x => x.Weight), 5),
            ["sector_note"] = "Sector and market-cap breakdowns are not shown: OpenAlgo's symbol master carries no sector or market-cap field, and inventing one would be worse than omitting it. Co-movement clustering answers the same question from data that actually exists.",
        };
    }

    // ---- health ----------------------------------------------------------------------------------------------

    private sealed record Pillar(string Key, string Label, double Score, double Weight, string Formula, JsonObject Inputs, string Comment);

    private static double Scale(double value, double low, double high)
    {
        if (!double.IsFinite(value)) return double.NaN;
        if (high == low) return 50.0;
        return Math.Clamp((value - low) / (high - low), 0.0, 1.0) * 100.0;
    }

    private static JsonNode? RoundOrNull(double x, int digits) => double.IsFinite(x) ? PyNumber.Round(x, digits) : null;

    public static JsonObject Health(string[] symbols, double[] weights, HoldingReturns returns, PriceMatrix prices, double sharpe, double sortino, double maxDrawdown, double costDrag, double turnover)
    {
        var pillars = new List<Pillar>();

        var c = Concentration(weights);
        var eff = c["effective_holdings"]!.GetValue<double>();
        pillars.Add(new Pillar("concentration", "Concentration", Scale(eff, 1.0, 12.0), 0.20, "effective holdings = 1 / HHI, scaled linearly from 1 (all in one name) to 12",
            new JsonObject
            {
                ["hhi"] = PyNumber.Round(c["hhi"]!.GetValue<double>(), 4), ["effective_holdings"] = PyNumber.Round(eff, 4),
                ["largest_weight"] = PyNumber.Round(c["largest_weight"]!.GetValue<double>(), 4), ["holdings"] = c["holdings"]!.GetValue<int>(),
            },
            eff < 4 ? $"{c["holdings"]!.GetValue<int>()} holdings, behaving like {PyFormat.Fixed(eff, 1)} independent bets" : $"spread across roughly {PyFormat.Fixed(eff, 1)} independent bets"));

        var avg = AveragePairwise(returns);
        pillars.Add(new Pillar("diversification", "True diversification", Scale(-avg, -0.9, -0.0), 0.20,
            "mean pairwise correlation, scaled from 0.9 (moves as one) to 0.0 (unrelated); lower is better",
            new JsonObject { ["average_pairwise_correlation"] = RoundOrNull(avg, 4) },
            double.IsFinite(avg) && avg > 0.7 ? "holdings move together, so the spread is nominal" : "holdings move independently enough to diversify"));

        pillars.Add(new Pillar("efficiency", "Risk-adjusted efficiency", Scale(sharpe, 0.0, 2.0), 0.20, "Sharpe ratio scaled from 0.0 to 2.0",
            new JsonObject { ["sharpe"] = RoundOrNull(sharpe, 4), ["sortino"] = RoundOrNull(sortino, 4) },
            double.IsFinite(sharpe) && sharpe < 0.5 ? "taking risk without being paid for it" : double.IsFinite(sharpe) && sharpe < 1.2 ? "modest reward for the risk taken" : "returns are compensating well for the risk taken"));

        pillars.Add(new Pillar("drawdown", "Drawdown resilience", Scale(maxDrawdown, -0.5, -0.1), 0.20, "worst peak-to-trough fall, scaled from -50% to -10%",
            new JsonObject { ["max_drawdown"] = RoundOrNull(maxDrawdown, 4) },
            double.IsFinite(maxDrawdown) && maxDrawdown < -0.30 ? "a fall this deep needs a large gain to recover" : "falls have stayed within normal bounds"));

        pillars.Add(TrendPillar(symbols, weights, prices));

        pillars.Add(new Pillar("cost", "Cost efficiency", Scale(-costDrag, -0.05, 0.0), 0.10, "return given up to trading costs, scaled from 5% to 0%; lower is better",
            new JsonObject { ["cost_drag"] = PyNumber.Round(costDrag, 6), ["turnover_total"] = PyNumber.Round(turnover, 4) },
            costDrag > 0.02 ? "rebalancing is eating a meaningful share of the return" : "trading costs are not material"));

        var scored = pillars.Where(p => double.IsFinite(p.Score)).ToList();
        var totalWeight = scored.Sum(p => p.Weight);
        var overall = totalWeight > 0 ? scored.Sum(p => p.Score * p.Weight) / totalWeight : double.NaN;
        var grade = !double.IsFinite(overall) ? null : overall >= 80 ? "A" : overall >= 65 ? "B" : overall >= 50 ? "C" : overall >= 35 ? "D" : "F";
        return new JsonObject
        {
            ["score"] = RoundOrNull(overall, 1),
            ["grade"] = grade,
            ["pillars"] = new JsonArray(pillars.Select(p => (JsonNode)new JsonObject
            {
                ["key"] = p.Key, ["label"] = p.Label, ["score"] = RoundOrNull(p.Score, 1), ["weight"] = p.Weight, ["formula"] = p.Formula, ["inputs"] = p.Inputs.DeepClone(), ["comment"] = p.Comment,
                ["effective_weight"] = double.IsFinite(p.Score) && totalWeight > 0 ? PyNumber.Round(p.Weight / totalWeight, 4) : 0.0,
            }).ToArray()),
            ["unmeasured"] = new JsonArray(pillars.Where(p => !double.IsFinite(p.Score)).Select(p => (JsonNode)p.Key).ToArray()),
        };
    }

    private static Pillar TrendPillar(string[] symbols, double[] weights, PriceMatrix prices)
    {
        const int Lookback = 200;
        var above = new List<(string Symbol, bool Above)>();
        for (var j = 0; j < symbols.Length; j++)
        {
            var closes = prices.Closes.Select(r => r[j]).ToArray();
            if (closes.Length < Lookback) continue;
            var sma = closes[^Lookback..].Sum() / Lookback;
            if (double.IsFinite(sma)) above.Add((symbols[j], closes[^1] > sma));
        }

        if (above.Count == 0)
            return new Pillar("trend", "Trend health", double.NaN, 0.10, $"weight above the {Lookback}-session moving average", new JsonObject { ["measured"] = 0 }, $"less than {Lookback} sessions of history");

        var total = weights.Sum();
        double W(string s) => weights[Array.IndexOf(symbols, s)] / total;
        var covered = above.Sum(a => W(a.Symbol));
        var share = covered != 0 ? above.Where(a => a.Above).Sum(a => W(a.Symbol)) / covered : 0.0;
        return new Pillar("trend", "Trend health", share * 100.0, 0.10,
            $"portfolio weight trading above its {Lookback}-session simple moving average, as a share of the weight that could be measured",
            new JsonObject
            {
                ["above"] = new JsonArray(above.Where(a => a.Above).Select(a => a.Symbol).Order(StringComparer.Ordinal).Select(s => (JsonNode)s).ToArray()),
                ["below"] = new JsonArray(above.Where(a => !a.Above).Select(a => a.Symbol).Order(StringComparer.Ordinal).Select(s => (JsonNode)s).ToArray()),
                ["measured_weight"] = PyNumber.Round(covered, 4),
            },
            share < 0.5 ? "most of the book is below its long-term average" : "most of the book is trending up");
    }

    // ---- attribution -----------------------------------------------------------------------------------------

    private static double CompoundOf(IEnumerable<double> v) { double p = 1; foreach (var x in v) p *= 1 + x; return p - 1; }

    /// <summary>Net excess return split into selection, allocation and costs, with per-holding contributions when the engine's rows reconcile.</summary>
    public static JsonObject Attribution(HoldingReturns holdings, double[][] realizedWeights, DateOnly[] weightDates, string[] weightSymbols, double[] targetWeights, string[] targetSymbols,
        ReturnSeries portfolio, ReturnSeries? benchmark, IReadOnlyDictionary<string, double> contributions)
    {
        JsonObject Unavailable(string reason) => new() { ["available"] = false, ["reason"] = reason };
        if (holdings.Rows.Length == 0 || benchmark is null || benchmark.Count == 0) return Unavailable("a benchmark is required to attribute against");
        var symbols = holdings.Symbols.Where(s => weightSymbols.Contains(s) && targetSymbols.Contains(s)).ToArray();
        if (symbols.Length == 0) return Unavailable("no priced holdings to attribute");
        var target = symbols.Select(s => targetWeights[Array.IndexOf(targetSymbols, s)]).ToArray();
        if (target.Sum() <= 0) return Unavailable("target weights sum to zero");
        var sumTarget = target.Sum();
        target = [.. target.Select(t => t / sumTarget)];

        var portLookup = portfolio.Dates.Select((d, i) => (d, i)).ToDictionary(x => x.d, x => portfolio.Values[x.i]);
        var benchLookup = benchmark.Dates.Select((d, i) => (d, i)).ToDictionary(x => x.d, x => benchmark.Values[x.i]);
        var weightRow = weightDates.Select((d, i) => (d, i)).ToDictionary(x => x.d, x => x.i);
        var kept = new List<int>();
        var gross = new List<double>();
        var equal = new List<double>();
        var net = new List<double>();
        var bench = new List<double>();
        for (var i = 0; i < holdings.Dates.Length; i++)
        {
            var d = holdings.Dates[i];
            if (!portLookup.TryGetValue(d, out var p) || !benchLookup.TryGetValue(d, out var b)) continue;
            // The weight held at the previous close earned this session's return.
            if (!weightRow.TryGetValue(d, out var wi) || wi == 0) continue;
            var prev = realizedWeights[wi - 1];
            double g = 0, e = 0;
            foreach (var (s, k) in symbols.Select((s, k) => (s, k)))
            {
                var h = holdings.Rows[i][Array.IndexOf(holdings.Symbols, s)];
                g += h * prev[Array.IndexOf(weightSymbols, s)];
                e += h;
            }

            if (double.IsNaN(p) || double.IsNaN(b)) continue;
            kept.Add(i);
            gross.Add(g);
            equal.Add(e / symbols.Length);
            net.Add(p);
            bench.Add(b);
        }

        if (kept.Count < 2) return Unavailable("no overlapping sessions with the benchmark");
        var grossReturn = CompoundOf(gross);
        var netReturn = CompoundOf(net);
        var equalReturn = CompoundOf(equal);
        var benchmarkReturn = CompoundOf(bench);
        var exact = holdings.Dates.SequenceEqual(portfolio.Dates) && holdings.Dates.SequenceEqual(benchmark.Dates) && kept.Count == holdings.Dates.Length;
        var rows = new List<(double Contribution, JsonObject Row)>();
        string? reason = null;
        if (exact)
        {
            var ok = symbols.All(contributions.ContainsKey) && symbols.All(s => double.IsFinite(contributions[s]));
            var sum = ok ? symbols.Sum(s => contributions[s]) : double.NaN;
            if (ok && Math.Abs(sum - netReturn) <= Math.Max(1e-9 * Math.Max(Math.Abs(sum), Math.Abs(netReturn)), 1e-9))
            {
                for (var k = 0; k < symbols.Length; k++)
                {
                    var own = CompoundOf(kept.Select(i => holdings.Rows[i][Array.IndexOf(holdings.Symbols, symbols[k])]));
                    var contribution = contributions[symbols[k]] - target[k] * benchmarkReturn;
                    rows.Add((contribution, new JsonObject { ["symbol"] = symbols[k], ["weight"] = target[k], ["return"] = own, ["vs_benchmark"] = own - benchmarkReturn, ["contribution"] = contribution }));
                }
            }
            else
            {
                reason = "engine holding contributions do not reconcile to this period";
            }
        }
        else
        {
            reason = "per-holding contributions require the benchmark and engine result to cover identical sessions";
        }

        return new JsonObject
        {
            ["available"] = true, ["portfolio_return"] = netReturn, ["gross_portfolio_return"] = grossReturn, ["equal_weight_return"] = equalReturn, ["benchmark_return"] = benchmarkReturn,
            ["excess_return"] = netReturn - benchmarkReturn, ["selection_effect"] = equalReturn - benchmarkReturn, ["allocation_effect"] = grossReturn - equalReturn, ["cost_effect"] = netReturn - grossReturn,
            ["holdings"] = new JsonArray(rows.OrderByDescending(r => r.Contribution).Select(r => (JsonNode)r.Row).ToArray()), ["holdings_reason"] = reason,
            ["method"] = "Selection compares an equal-weighted basket of the same holdings with the benchmark; allocation compares the realized gross portfolio with that basket; costs compare the engine's net and gross paths. Brinson-Fachler is not used because benchmark constituent weights and segment returns are unavailable.",
        };
    }

    // ---- crisis ----------------------------------------------------------------------------------------------

    private static double? WindowReturn(ReturnSeries s, DateOnly start, DateOnly end)
    {
        var chunk = new List<double>();
        for (var i = 0; i < s.Count; i++) if (s.Dates[i] >= start && s.Dates[i] <= end) chunk.Add(s.Values[i]);
        return chunk.Count < 2 ? null : CompoundOf(chunk);
    }

    public static JsonObject Crisis(ReturnSeries returns, ReturnSeries? benchmark, double minCoverage = 0.6)
    {
        if (returns.Count == 0) return new JsonObject { ["periods"] = new JsonArray(), ["summary"] = null };
        var first = returns.Dates[0];
        var last = returns.Dates[^1];
        var rows = new List<(CrisisPeriod Period, double Port, double? Bench, int Days, int Sessions, double Coverage, DateOnly Start, DateOnly End)>();
        foreach (var period in CrisisPeriod.India)
        {
            var start = DateOnly.ParseExact(period.Start, "yyyy-MM-dd", CultureInfo.InvariantCulture);
            var end = DateOnly.ParseExact(period.End, "yyyy-MM-dd", CultureInfo.InvariantCulture);
            if (end < first || start > last) continue;
            var cs = start > first ? start : first;
            var ce = end < last ? end : last;
            var span = end.DayNumber - start.DayNumber;
            if (span == 0) span = 1;
            var coverage = (ce.DayNumber - cs.DayNumber) / (double)span;
            if (WindowReturn(returns, cs, ce) is not { } port) continue;
            var sessions = returns.Dates.Count(d => d >= cs && d <= ce);
            var bench = benchmark is null ? null : WindowReturn(benchmark, cs, ce);
            rows.Add((period, port, bench, ce.DayNumber - cs.DayNumber, sessions, coverage, cs, ce));
        }

        var json = new JsonArray();
        foreach (var r in rows)
            json.Add(new JsonObject
            {
                ["key"] = r.Period.Key, ["label"] = r.Period.Label, ["note"] = r.Period.Note, ["start"] = r.Start.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), ["end"] = r.End.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                ["portfolio"] = r.Port, ["benchmark"] = r.Bench, ["excess"] = r.Bench is null ? null : r.Port - r.Bench, ["days"] = r.Days, ["sessions"] = r.Sessions,
                ["coverage"] = PyNumber.Round(Math.Min(r.Coverage, 1.0), 3), ["partial"] = r.Coverage < minCoverage, ["scope"] = r.Period.Scope,
            });

        var complete = rows.Where(r => r.Coverage >= minCoverage).ToList();
        JsonNode? summary = null;
        if (complete.Count > 0)
        {
            double? Excess((CrisisPeriod Period, double Port, double? Bench, int Days, int Sessions, double Coverage, DateOnly Start, DateOnly End) r) => r.Bench is null ? null : r.Port - r.Bench;
            var anyExcess = complete.Any(r => Excess(r) is not null);
            var byScope = new JsonObject();
            foreach (var scope in new[] { "global", "india" })
            {
                var group = complete.Where(r => r.Period.Scope == scope).ToList();
                if (group.Count == 0) continue;
                byScope[scope] = new JsonObject
                {
                    ["count"] = group.Count, ["average_return"] = group.Average(r => r.Port),
                    ["hit_rate"] = group.Any(r => Excess(r) is not null) ? group.Count(r => (Excess(r) ?? 0) > 0) / (double)group.Count : null,
                };
            }

            summary = new JsonObject
            {
                ["count"] = complete.Count, ["average_return"] = complete.Average(r => r.Port), ["hit_rate"] = anyExcess ? complete.Count(r => Excess(r) is { } e && e > 0) / (double)complete.Count : null,
                ["worst"] = complete.Min(r => r.Port), ["best"] = complete.Max(r => r.Port), ["by_scope"] = byScope,
            };
        }

        return new JsonObject { ["periods"] = json, ["summary"] = summary };
    }
}
