using System.Globalization;
using System.Text.Json.Nodes;
using EPTradeBot.Core.Options;

namespace EPTradeBot.Core.Portfolio;

public static partial class ReturnStats
{
    private static readonly string[] MonthNames = ["JAN", "FEB", "MAR", "APR", "MAY", "JUN", "JUL", "AUG", "SEP", "OCT", "NOV", "DEC"];

    private static JsonArray Curve(IReadOnlyList<DateOnly> dates, IReadOnlyList<double> values, int skipNaN = 0)
    {
        _ = skipNaN;
        var rows = new JsonArray();
        for (var i = 0; i < values.Count; i++)
        {
            if (double.IsNaN(values[i])) continue;
            rows.Add(new JsonObject { ["date"] = Iso(dates[i]), ["value"] = PyNumber.Round(values[i], 4) });
        }

        return rows;
    }

    /// <summary>The last Monday-ending week label for a session: the Monday on or after it (openstatz resamples weeks on W-MON).</summary>
    private static DateOnly WeekLabel(DateOnly d) => d.AddDays((8 - (int)d.DayOfWeek) % 7);

    private static DateOnly MonthEnd(DateOnly d) => new(d.Year, d.Month, DateTime.DaysInMonth(d.Year, d.Month));

    private static DateOnly QuarterEnd(DateOnly d) { var m = (d.Month - 1) / 3 * 3 + 3; return new DateOnly(d.Year, m, DateTime.DaysInMonth(d.Year, m)); }

    private static DateOnly YearEnd(DateOnly d) => new(d.Year, 12, 31);

    /// <summary>Compounded returns per bin, keeping the empty bins between the first and last (a resample compounds nothing to zero).</summary>
    private static List<(DateOnly Label, double Value)> Resample(ReturnSeries r, Func<DateOnly, DateOnly> label, Func<DateOnly, DateOnly> next)
    {
        var groups = new SortedDictionary<DateOnly, double>();
        for (var i = 0; i < r.Count; i++)
        {
            var l = label(r.Dates[i]);
            groups[l] = (groups.TryGetValue(l, out var p) ? p : 1.0) * (1 + r.Values[i]);
        }

        var result = new List<(DateOnly, double)>();
        if (groups.Count == 0) return result;
        for (var l = groups.Keys.First(); l <= groups.Keys.Last(); l = next(l))
            result.Add((l, groups.TryGetValue(l, out var p) ? p - 1 : 0.0));
        return result;
    }

    private static List<(DateOnly Label, double Value)> Weekly(ReturnSeries r) => Resample(r, WeekLabel, l => l.AddDays(7));

    private static List<(DateOnly Label, double Value)> Monthly(ReturnSeries r) => Resample(r, MonthEnd, l => MonthEnd(l.AddDays(1)));

    private static List<(DateOnly Label, double Value)> Quarterly(ReturnSeries r) => Resample(r, QuarterEnd, l => QuarterEnd(l.AddDays(1)));

    private static List<(DateOnly Label, double Value)> Yearly(ReturnSeries r) => Resample(r, YearEnd, l => YearEnd(l.AddDays(1)));

    /// <summary>Compounded returns per group of sessions that exist (no empty groups), in key order.</summary>
    private static List<double> Grouped<TKey>(ReturnSeries r, Func<DateOnly, TKey> key) where TKey : notnull, IComparable<TKey>
    {
        var groups = new SortedDictionary<TKey, double>();
        for (var i = 0; i < r.Count; i++)
        {
            var k = key(r.Dates[i]);
            groups[k] = (groups.TryGetValue(k, out var p) ? p : 1.0) * (1 + r.Values[i]);
        }

        return [.. groups.Values.Select(p => p - 1)];
    }

    private static (int, int) YearMonth(DateOnly d) => (d.Year, d.Month);

    private static (int, int) YearQuarter(DateOnly d) => (d.Year, (d.Month - 1) / 3 + 1);

    private static double[] Rolling(double[] v, int window, Func<double[], double> fn)
    {
        var o = new double[v.Length];
        for (var i = 0; i < v.Length; i++) o[i] = i + 1 < window ? double.NaN : fn(v[(i + 1 - window)..(i + 1)]);
        return o;
    }

    private static JsonArray DrawdownEpisodes(DateOnly[] dates, double[] dd)
    {
        var n = dd.Length;
        bool No(int i) => dd[i] == 0;
        var starts = new List<int>();
        var ends = new List<int>();
        for (var i = 1; i < n; i++) if (!No(i) && No(i - 1)) starts.Add(i);
        for (var i = 0; i < n - 1; i++) if (!No(i) && No(i + 1)) ends.Add(i);
        var rows = new JsonArray();
        if (starts.Count == 0) return rows;
        if (ends.Count > 0 && starts[0] > ends[0]) starts.Insert(0, 0);
        if (ends.Count == 0 || starts[^1] > ends[^1]) ends.Add(n - 1);
        var episodes = new List<(int Start, int Valley, int End, double Depth)>();
        for (var i = 0; i < Math.Min(starts.Count, ends.Count); i++)
        {
            var valley = starts[i];
            for (var j = starts[i]; j <= ends[i]; j++) if (dd[j] < dd[valley]) valley = j;
            episodes.Add((starts[i], valley, ends[i], dd[valley] * 100));
        }

        foreach (var e in episodes.OrderBy(e => e.Depth).Take(5))
            rows.Add(new JsonObject
            {
                ["start"] = Iso(dates[e.Start]), ["valley"] = Iso(dates[e.Valley]), ["end"] = Iso(dates[e.End]),
                ["days"] = dates[e.End].DayNumber - dates[e.Start].DayNumber + 1, ["depth"] = Clean(e.Depth / 100.0),
            });
        return rows;
    }

    private static JsonObject Quantiles(string period, IReadOnlyList<double> data)
    {
        var q1 = Quantile(data, 0.25);
        var q3 = Quantile(data, 0.75);
        var iqr = q3 - q1;
        var values = data.Where(x => x >= q1 - 1.5 * iqr && x <= q3 + 1.5 * iqr).ToArray();
        var outliers = data.Where(x => !(x >= q1 - 1.5 * iqr && x <= q3 + 1.5 * iqr)).ToArray();
        return new JsonObject
        {
            ["period"] = period, ["count"] = values.Length, ["min"] = Clean(values.Min()), ["q1"] = Clean(Quantile(values, 0.25)), ["median"] = Clean(Quantile(values, 0.5)),
            ["q3"] = Clean(Quantile(values, 0.75)), ["max"] = Clean(values.Max()), ["mean"] = Clean(Mean(values)), ["negative_share"] = Clean(values.Count(x => x < 0) / (double)values.Length),
            ["outliers"] = CleanList(outliers.Take(40)),
        };
    }

    /// <summary>Everything the series tab draws: underwater curve and episodes, rolling risk, month and week grids, seasonality, quantiles and year on year against the benchmark.</summary>
    public static JsonObject SeriesAnalytics(ReturnSeries returns, double rf, ReturnSeries? benchmark)
    {
        var o = new JsonObject();
        var r = returns.Values;
        var dd = DrawdownSeries(r);
        o["drawdown"] = Curve(returns.Dates, dd.Select(x => x * 100.0).ToArray());
        var episodes = DrawdownEpisodes(returns.Dates, dd);
        if (episodes.Count > 0) o["drawdown_episodes"] = episodes;

        var window = r.Length > 200 ? 126 : Math.Max(20, r.Length / 4);
        if (r.Length > window)
        {
            var excess = Excess(r, rf, window);
            var sharpe = Rolling(excess, window, w => Mean(w) / Std(w) * Math.Sqrt(TradingDays));
            var vol = Rolling(r, window, w => Std(w) * Math.Sqrt(TradingDays));
            o["rolling"] = new JsonObject { ["window"] = window, ["sharpe"] = Curve(returns.Dates, sharpe), ["volatility"] = Curve(returns.Dates, vol) };
        }

        // Month by month, years down and months across; a month with no sessions is zero.
        var months = new SortedDictionary<int, double[]>();
        var monthProduct = new Dictionary<(int, int), double>();
        for (var i = 0; i < returns.Count; i++)
        {
            var key = (returns.Dates[i].Year, returns.Dates[i].Month);
            monthProduct[key] = (monthProduct.TryGetValue(key, out var p) ? p : 1.0) * (1 + r[i]);
        }

        foreach (var ((year, month), p) in monthProduct)
        {
            if (!months.TryGetValue(year, out var row)) months[year] = row = new double[12];
            row[month - 1] = p - 1;
        }

        o["monthly_returns"] = new JsonObject
        {
            ["years"] = new JsonArray(months.Keys.Select(y => (JsonNode)y.ToString(CultureInfo.InvariantCulture)).ToArray()),
            ["columns"] = new JsonArray(MonthNames.Select(m => (JsonNode)m).ToArray()),
            ["values"] = new JsonArray(months.Values.Select(row => (JsonNode)CleanList(row)).ToArray()),
        };

        var eom = Grouped(returns, YearMonth);
        var eoq = Grouped(returns, YearQuarter);
        o["seasonality"] = new JsonObject
        {
            ["best_month"] = Clean(eom.Max()), ["worst_month"] = Clean(eom.Min()),
            ["avg_up_month"] = Clean(eom.Where(x => x > 0).DefaultIfEmpty(double.NaN).Average()),
            ["avg_down_month"] = Clean(eom.Where(x => x < 0).DefaultIfEmpty(double.NaN).Average()),
            ["win_months"] = Clean(WinRate(eom)), ["win_quarters"] = Clean(WinRate(eoq)),
        };

        var weekly = Weekly(returns);
        var quantiles = new JsonArray
        {
            Quantiles("Daily", r),
            Quantiles("Weekly", weekly.Select(w => w.Value).ToArray()),
            Quantiles("Monthly", Monthly(returns).Select(w => w.Value).ToArray()),
            Quantiles("Quarterly", Quarterly(returns).Select(w => w.Value).ToArray()),
            Quantiles("Yearly", Yearly(returns).Select(w => w.Value).ToArray()),
        };
        o["return_quantiles"] = quantiles;

        if (weekly.Count > 0)
        {
            var cells = new SortedDictionary<int, SortedDictionary<int, double>>();
            foreach (var (label, value) in weekly)
            {
                var year = ISOWeek.GetYear(label.ToDateTime(TimeOnly.MinValue));
                var week = ISOWeek.GetWeekOfYear(label.ToDateTime(TimeOnly.MinValue));
                if (!cells.TryGetValue(year, out var row)) cells[year] = row = [];
                row[week] = value;
            }

            var weeks = cells.Values.SelectMany(x => x.Keys).Distinct().Order().ToArray();
            o["weekly_grid"] = new JsonObject
            {
                ["years"] = new JsonArray(cells.Keys.Select(y => (JsonNode)y.ToString(CultureInfo.InvariantCulture)).ToArray()),
                ["weeks"] = new JsonArray(weeks.Select(w => (JsonNode)w).ToArray()),
                ["values"] = new JsonArray(cells.Values.Select(row => (JsonNode)new JsonArray(weeks.Select(w => row.TryGetValue(w, out var v) ? Clean(v) : null).ToArray())).ToArray()),
            };
        }

        o["weekly_returns"] = Curve(weekly.Select(w => w.Label).ToArray(), weekly.Select(w => w.Value).ToArray());

        var rows = new JsonArray();
        if (benchmark is { Count: > 0 })
        {
            var (dates, p, b) = Join(returns, benchmark);
            if (p.Length > 0)
            {
                var yearly = new SortedDictionary<int, (double Port, double Bench)>();
                for (var i = 0; i < dates.Length; i++)
                {
                    var cur = yearly.TryGetValue(dates[i].Year, out var t) ? t : (1.0, 1.0);
                    yearly[dates[i].Year] = (cur.Item1 * (1 + p[i]), cur.Item2 * (1 + b[i]));
                }

                foreach (var (year, (port, bench)) in yearly)
                {
                    var benchPct = (bench - 1) * 100;
                    var portPct = (port - 1) * 100;
                    var multiplier = benchPct == 0 ? double.NaN : portPct / benchPct;
                    rows.Add(new JsonObject
                    {
                        ["year"] = year.ToString(CultureInfo.InvariantCulture), ["benchmark"] = Clean(benchPct / 100.0), ["portfolio"] = Clean(portPct / 100.0),
                        ["difference"] = Clean((portPct - benchPct) / 100.0), ["won"] = portPct >= benchPct, ["multiplier"] = benchPct > 0 ? Clean(multiplier) : null,
                    });
                }
            }
        }
        else
        {
            var yearly = new SortedDictionary<int, double>();
            for (var i = 0; i < returns.Count; i++) yearly[returns.Dates[i].Year] = (yearly.TryGetValue(returns.Dates[i].Year, out var p) ? p : 1.0) * (1 + r[i]);
            foreach (var (year, p) in yearly) rows.Add(new JsonObject { ["year"] = year.ToString(CultureInfo.InvariantCulture), ["portfolio"] = Clean(p - 1) });
        }

        o["yearly_returns"] = rows;
        return o;
    }
}
