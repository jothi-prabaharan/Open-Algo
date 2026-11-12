using System.Globalization;
using System.Text.Json.Nodes;
using EPTradeBot.Core.Options;

namespace EPTradeBot.Core.Portfolio;

/// <summary>Daily returns with their session dates.</summary>
public sealed record ReturnSeries(DateOnly[] Dates, double[] Values)
{
    public int Count => Values.Length;

    /// <summary>Returns between consecutive closes (the first session has none).</summary>
    public static ReturnSeries FromCloses(DateOnly[] dates, double[] closes)
    {
        var d = new DateOnly[Math.Max(closes.Length - 1, 0)];
        var v = new double[d.Length];
        for (var i = 1; i < closes.Length; i++) { d[i - 1] = dates[i]; v[i - 1] = closes[i] / closes[i - 1] - 1.0; }
        return new ReturnSeries(d, v);
    }
}

/// <summary>
/// The portfolio statistics OpenAlgo takes from openstatz (and the few it adds): headline metrics, the underwater curve and its episodes, rolling risk, the month and week grids,
/// seasonality, return quantiles and year-on-year against a benchmark. Formulas, conventions and edge behaviour follow openstatz, not the textbook where they differ.
/// </summary>
public static partial class ReturnStats
{
    public const int TradingDays = 252;

    // ---- small numerics ---------------------------------------------------------------------------------------

    public static double Mean(IReadOnlyList<double> v) { double s = 0; foreach (var x in v) s += x; return v.Count == 0 ? double.NaN : s / v.Count; }

    /// <summary>Sample standard deviation (ddof 1), two-pass as pandas computes it.</summary>
    public static double Std(IReadOnlyList<double> v)
    {
        if (v.Count < 2) return double.NaN;
        var m = Mean(v);
        double s = 0;
        foreach (var x in v) s += (x - m) * (x - m);
        return Math.Sqrt(s / (v.Count - 1));
    }

    /// <summary>Linear-interpolated quantile, as pandas.</summary>
    public static double Quantile(IReadOnlyList<double> v, double q)
    {
        if (v.Count == 0) return double.NaN;
        var s = v.Order().ToArray();
        var pos = (s.Length - 1) * q;
        var lo = (int)Math.Floor(pos);
        var hi = Math.Min(lo + 1, s.Length - 1);
        return s[lo] + (s[hi] - s[lo]) * (pos - lo);
    }

    public static double Comp(IEnumerable<double> v) { double p = 1; foreach (var x in v) p *= 1 + x; return p - 1; }

    /// <summary>Inverse of the normal distribution (Wichura's AS 241, double precision).</summary>
    public static double NormalPpf(double p)
    {
        if (p <= 0) return double.NegativeInfinity;
        if (p >= 1) return double.PositiveInfinity;
        var q = p - 0.5;
        if (Math.Abs(q) <= 0.425)
        {
            var r = 0.180625 - q * q;
            return q * (((((((r * 2509.0809287301226727 + 33430.575583588128105) * r + 67265.770927008700853) * r + 45921.953931549871457) * r + 13731.693765509461125) * r + 1971.5909503065514427) * r + 133.14166789178437745) * r + 3.387132872796366608)
                / (((((((r * 5226.495278852545925 + 28729.085735721942674) * r + 39307.89580009271061) * r + 21213.794301586595867) * r + 5394.1960214247511077) * r + 687.1870074920579083) * r + 42.313330701600911252) * r + 1.0);
        }

        var rr = q < 0 ? p : 1 - p;
        rr = Math.Sqrt(-Math.Log(rr));
        double val;
        if (rr <= 5)
        {
            rr -= 1.6;
            val = (((((((rr * 7.7454501427834140764e-4 + 0.0227238449892691845833) * rr + 0.24178072517745061177) * rr + 1.27045825245236838258) * rr + 3.64784832476320460504) * rr + 5.7694972214606914055) * rr + 4.6303378461565452959) * rr + 1.42343711074968357734)
                / (((((((rr * 1.05075007164441684324e-9 + 5.475938084995344946e-4) * rr + 0.0151986665636164571966) * rr + 0.14810397642748007459) * rr + 0.68976733498510000455) * rr + 1.6763848301838038494) * rr + 2.05319162663775882187) * rr + 1.0);
        }
        else
        {
            rr -= 5;
            val = (((((((rr * 2.01033439929228813265e-7 + 2.71155556874348757815e-5) * rr + 0.0012426609473880784386) * rr + 0.026532189526576123093) * rr + 0.29656057182850489123) * rr + 1.7848265399172913358) * rr + 5.4637849111641143699) * rr + 6.6579046435011037772)
                / (((((((rr * 2.04426310338993978564e-15 + 1.4215117583164458887e-7) * rr + 1.8463183175100546818e-5) * rr + 7.868691311456132591e-4) * rr + 0.0148753612908506148525) * rr + 0.13692988092273580531) * rr + 0.59983220655588793769) * rr + 1.0);
        }

        return q < 0 ? -val : val;
    }

    internal static JsonNode? Clean(double x) => double.IsFinite(x) ? JsonValue.Create(PyNumber.Round(x, 6)) : null;

    internal static JsonNode? Clean(double? x) => x is { } v ? Clean(v) : null;

    private static JsonArray CleanList(IEnumerable<double> values) => new(values.Select(Clean).ToArray());

    private static string Iso(DateOnly d) => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    // ---- openstatz primitives ---------------------------------------------------------------------------------

    /// <summary>openstatz reads a series with a minimum below 0 or a maximum below 1 as returns and turns it into prices from 1; anything else is already prices.</summary>
    public static double[] PreparePrices(double[] values)
    {
        if (values.Length == 0) return values;
        if (values.Min() >= 0 && values.Max() >= 1) return values;
        var prices = new double[values.Length];
        double p = 1;
        for (var i = 0; i < values.Length; i++) { p *= 1 + values[i]; prices[i] = p; }
        return prices;
    }

    private static double Baseline(double first) => first > 1000 ? 1e5 : first > 10 ? 100.0 : 1.0;

    /// <summary>Drawdown from the running peak, with a phantom starting point so a loss on the first session counts.</summary>
    public static double[] DrawdownSeries(double[] returns)
    {
        var prices = PreparePrices(returns);
        var dd = new double[prices.Length];
        var peak = Baseline(prices[0]);
        for (var i = 0; i < prices.Length; i++)
        {
            peak = Math.Max(peak, prices[i]);
            var v = prices[i] / peak - 1.0;
            dd[i] = v == 0 ? 0.0 : v;
        }

        return dd;
    }

    public static double MaxDrawdown(double[] pricesOrReturns)
    {
        if (pricesOrReturns.Length == 0) return 0.0;
        var prices = PreparePrices(pricesOrReturns);
        var peak = Baseline(prices[0]);
        var worst = 1.0;
        // The phantom point itself is at its own peak (ratio 1).
        foreach (var p in prices)
        {
            peak = Math.Max(peak, p);
            worst = Math.Min(worst, p / peak);
        }

        return worst - 1;
    }

    public static double Cagr(double[] r)
    {
        var years = r.Length / (double)TradingDays;
        return Math.Pow(Math.Abs(Comp(r) + 1.0), 1.0 / years) - 1;
    }

    private static double[] Excess(double[] r, double rf, int? periods)
    {
        if (!(rf > 0)) return r;
        var d = periods is { } n ? Math.Pow(1 + rf, 1.0 / n) - 1.0 : rf;
        return [.. r.Select(x => x - d)];
    }

    public static double Skew(double[] v)
    {
        var n = v.Length;
        if (n < 3) return double.NaN;
        var m = Mean(v);
        double m2 = 0, m3 = 0;
        foreach (var x in v) { var d = x - m; m2 += d * d; m3 += d * d * d; }
        return n * Math.Sqrt(n - 1.0) / (n - 2.0) * (m3 / Math.Pow(m2, 1.5));
    }

    public static double Kurtosis(double[] v)
    {
        var n = v.Length;
        if (n < 4) return double.NaN;
        var m = Mean(v);
        double m2 = 0, m4 = 0;
        foreach (var x in v) { var d = x - m; m2 += d * d; m4 += d * d * d * d; }
        var numerator = (double)n * (n + 1) * (n - 1) * m4;
        var denominator = (double)(n - 2) * (n - 3) * m2 * m2;
        return numerator / denominator - 3.0 * (n - 1.0) * (n - 1.0) / ((n - 2.0) * (n - 3.0));
    }

    private static double WinRate(IReadOnlyList<double> v)
    {
        var nonZero = v.Count(x => x != 0);
        return nonZero == 0 ? 0.0 : v.Count(x => x > 0) / (double)nonZero;
    }

    /// <summary>Two series on the sessions they share, in date order.</summary>
    public static (DateOnly[] Dates, double[] A, double[] B) Join(ReturnSeries a, ReturnSeries b)
    {
        var lookup = new Dictionary<DateOnly, double>();
        for (var i = 0; i < b.Count; i++) lookup[b.Dates[i]] = b.Values[i];
        var dates = new List<DateOnly>();
        var av = new List<double>();
        var bv = new List<double>();
        for (var i = 0; i < a.Count; i++)
        {
            if (lookup.TryGetValue(a.Dates[i], out var other) && !double.IsNaN(a.Values[i]) && !double.IsNaN(other)) { dates.Add(a.Dates[i]); av.Add(a.Values[i]); bv.Add(other); }
        }

        return ([.. dates], [.. av], [.. bv]);
    }

    // ---- headline metrics -------------------------------------------------------------------------------------

    public static JsonObject Headline(ReturnSeries returns, ReturnSeries? benchmark, double rf)
    {
        var r = returns.Values;
        var sharpeSeries = Excess(r, rf, TradingDays);
        var mean = Mean(sharpeSeries);
        var sharpe = mean / Std(sharpeSeries) * Math.Sqrt(TradingDays);
        var downside = Math.Sqrt(sharpeSeries.Where(x => x < 0).Sum(x => x * x) / sharpeSeries.Length);
        var sortino = (downside == 0 ? double.NaN : mean / downside) * Math.Sqrt(TradingDays);
        var maxDrawdown = MaxDrawdown(PriceCurve(r));
        var cagr = Cagr(r);
        var mu = Mean(r);
        var sigma = Std(r);
        var var95 = mu + sigma * NormalPpf(0.05);
        var below = r.Where(x => x < var95).ToArray();
        var cvarRaw = below.Length > 0 ? Mean(below) : double.NaN;
        var dd = DrawdownSeries(r);
        var calmarDd = MaxDrawdown(r);
        var recoveryDd = MaxDrawdown(r);
        var q95 = Quantile(r, 0.95);
        var q05 = Quantile(r, 0.05);
        var tail = double.IsNaN(q95) || double.IsNaN(q05) || q05 == 0 ? double.NaN : Math.Abs(q95 / q05);
        var o = new JsonObject
        {
            ["cagr"] = Clean(cagr),
            ["volatility"] = Clean(Std(r) * Math.Sqrt(TradingDays)),
            ["sharpe"] = Clean(sharpe),
            ["sortino"] = Clean(sortino),
            ["calmar"] = Clean(cagr / Math.Abs(calmarDd)),
            ["max_drawdown"] = Clean(maxDrawdown),
            ["win_rate"] = Clean(WinRate(r)),
            ["best_day"] = Clean(r.Max()),
            ["worst_day"] = Clean(r.Min()),
            ["value_at_risk"] = Clean(var95),
            ["cvar"] = Clean(double.IsNaN(cvarRaw) ? var95 : cvarRaw),
            ["ulcer_index"] = Clean(Math.Sqrt(dd.Sum(x => x * x) / (r.Length - 1))),
            ["recovery_factor"] = Clean(recoveryDd == 0 ? double.NaN : Math.Abs(r.Sum() - 0.0) / Math.Abs(recoveryDd)),
            ["tail_ratio"] = Clean(tail),
            ["skew"] = Clean(Skew(r)),
            ["kurtosis"] = Clean(Kurtosis(r)),
        };

        if (benchmark is { Count: > 0 })
        {
            var (_, p, b) = Join(returns, benchmark);
            if (p.Length > 0)
            {
                var varB = Variance(b);
                var cov = Covariance(p, b);
                var beta = varB == 0 ? double.NaN : cov / varB;
                var alpha = (Mean(p) - beta * Mean(b)) * TradingDays;
                var diff = p.Select((x, i) => x - b[i]).ToArray();
                var diffStd = Std(diff);
                var benchmarkCagr = Cagr(b);
                o["alpha"] = Clean(double.IsNaN(alpha) ? 0 : alpha);
                o["beta"] = Clean(double.IsNaN(beta) ? 0 : beta);
                o["information_ratio"] = Clean(diffStd != 0 ? Mean(diff) / diffStd : 0);
                o["benchmark_cagr"] = Clean(benchmarkCagr);
                o["excess_cagr"] = Clean(cagr - benchmarkCagr);
                o["up_capture"] = Clean(Capture(p, b, x => x > 0));
                o["down_capture"] = Clean(Capture(p, b, x => x < 0));
            }
        }

        return o;
    }

    /// <summary>Running value of the returns from 1: the series OpenAlgo hands to max_drawdown.</summary>
    private static double[] PriceCurve(double[] r)
    {
        var p = new double[r.Length];
        double v = 1;
        for (var i = 0; i < r.Length; i++) { v *= 1 + r[i]; p[i] = v; }
        return p;
    }

    private static double Variance(double[] v) { var s = Std(v); return s * s; }

    private static double Covariance(double[] a, double[] b)
    {
        var ma = Mean(a);
        var mb = Mean(b);
        double s = 0;
        for (var i = 0; i < a.Length; i++) s += (a[i] - ma) * (b[i] - mb);
        return s / (a.Length - 1);
    }

    private static double Capture(double[] port, double[] bench, Func<double, bool> pick)
    {
        var idx = Enumerable.Range(0, bench.Length).Where(i => pick(bench[i])).ToArray();
        if (idx.Length == 0) return double.NaN;
        var b = Mean(idx.Select(i => bench[i]).ToArray());
        return b == 0 ? double.NaN : Mean(idx.Select(i => port[i]).ToArray()) / b;
    }
}
