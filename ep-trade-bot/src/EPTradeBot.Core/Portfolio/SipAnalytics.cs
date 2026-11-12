using System.Globalization;
using System.Text.Json.Nodes;
using EPTradeBot.Core.Options;

namespace EPTradeBot.Core.Portfolio;

/// <summary>
/// Measures of a completed SIP, from its dated cash flows and daily curves, never from price statistics that the deposit schedule would distort. OpenAlgo's sip.analytics: XIRR and its variants,
/// time spent below cost, drawdown, rolling and start-date grids, frequency and lumpsum comparisons.
/// </summary>
public static class SipAnalytics
{
    public static readonly int[] RollingYears = [1, 3, 5, 7, 10];
    public const int MaxGridCells = 900;

    /// <summary>A finite number, or null: JSON has no NaN and a chart reads null as a gap.</summary>
    public static double? Finite(double? v) => v is { } d && double.IsFinite(d) ? d : null;

    private static string Iso(DateOnly d) => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    public static JsonObject Headline(SipResult r)
    {
        var rate = Xirr.OrNull(r.CashFlows);
        var gain = r.FinalValue - r.TotalInvested;
        return new JsonObject
        {
            ["invested"] = Finite(r.TotalInvested), ["final_value"] = Finite(r.FinalValue), ["gain"] = Finite(gain),
            ["multiple"] = r.TotalInvested != 0 ? Finite(r.FinalValue / r.TotalInvested) : null, ["xirr"] = Finite(rate),
            ["absolute_return"] = Finite(Xirr.AbsoluteReturn(r.TotalInvested, r.FinalValue)), ["installments"] = r.InstallmentCount, ["units"] = Finite(r.TotalUnits),
            ["average_cost"] = Finite(r.AverageCost), ["average_price"] = Finite(r.AveragePrice),
            ["cost_advantage"] = r.AveragePrice != 0 ? Finite((r.AverageCost - r.AveragePrice) / r.AveragePrice) : null,
            ["charges"] = Finite(r.Charges), ["cash"] = Finite(r.Cash), ["start"] = Iso(r.Dates[0]), ["end"] = Iso(r.Dates[^1]),
            ["years"] = Finite((r.Dates[^1].DayNumber - r.Dates[0].DayNumber) / 365.0),
        };
    }

    public static JsonObject Underwater(SipResult r)
    {
        var total = r.Value.Length;
        int below = 0, longest = 0, current = 0, longestEnd = -1;
        for (var i = 0; i < total; i++)
        {
            if (r.Value[i] < r.Invested[i])
            {
                below++;
                current++;
                if (current > longest) { longest = current; longestEnd = i; }
            }
            else current = 0;
        }

        double? worst = null;
        for (var i = 0; i < total; i++)
        {
            if (r.Invested[i] == 0) continue;
            var gap = (r.Value[i] - r.Invested[i]) / r.Invested[i];
            if (!double.IsNaN(gap) && (worst is null || gap < worst)) worst = gap;
        }

        return new JsonObject
        {
            ["sessions_below"] = below, ["sessions_total"] = total, ["share_below"] = total != 0 ? Finite((double)below / total) : null, ["longest_streak_sessions"] = longest,
            ["longest_streak_ended"] = longestEnd >= 0 ? Iso(r.Dates[longestEnd]) : null, ["worst_shortfall"] = Finite(worst), ["ever_underwater"] = below > 0,
        };
    }

    public static JsonObject Drawdown(SipResult r)
    {
        var n = r.Value.Length;
        var peak = new double[n];
        var dd = new double[n];
        var running = double.NegativeInfinity;
        for (var i = 0; i < n; i++)
        {
            running = Math.Max(running, r.Value[i]);
            peak[i] = running;
            dd[i] = peak[i] != 0 ? (r.Value[i] - peak[i]) / peak[i] : double.NaN;
        }

        var trough = -1;
        for (var i = 0; i < n; i++)
            if (!double.IsNaN(dd[i]) && (trough < 0 || dd[i] < dd[trough])) trough = i;

        int? recovery = null;
        if (trough >= 0)
            for (var i = trough; i < n; i++)
                if (r.Value[i] >= peak[trough]) { recovery = i - trough; break; }

        var curve = new JsonArray();
        for (var i = 0; i < n; i++) curve.Add(new JsonObject { ["date"] = Iso(r.Dates[i]), ["value"] = Finite(dd[i]) });
        return new JsonObject
        {
            ["max_drawdown"] = trough >= 0 ? Finite(dd[trough]) : null, ["trough_date"] = trough >= 0 ? Iso(r.Dates[trough]) : null, ["recovery_sessions"] = recovery,
            ["recovered"] = recovery is not null, ["curve"] = curve,
        };
    }

    private static double? SipXirr(PriceSeries closes, DateOnly start, DateOnly end, double amount, SipOptions o)
    {
        try { return Xirr.OrNull(SipEngine.Run(closes, "x", "x", start, end, amount, o).CashFlows); }
        catch (Exception e) when (e is not OutOfMemoryException) { return null; }
    }

    private static double Median(List<double> values)
    {
        var s = values.OrderBy(v => v).ToList();
        var mid = s.Count / 2;
        return s.Count % 2 == 1 ? s[mid] : (s[mid - 1] + s[mid]) / 2.0;
    }

    public static JsonArray RollingXirr(PriceSeries closes, double amount, SipOptions o, int[]? years = null, int stepMonths = 3)
    {
        var result = new JsonArray();
        if (closes.IsEmpty) return result;
        var first = closes.Dates[0];
        var last = closes.Dates[^1];
        foreach (var span in years ?? RollingYears)
        {
            var points = new JsonArray();
            var rates = new List<double>();
            var cursor = first;
            while (true)
            {
                DateOnly end;
                try { end = new DateOnly(cursor.Year + span, cursor.Month, Math.Min(cursor.Day, 28)); }
                catch (ArgumentOutOfRangeException) { break; }
                if (end > last) break;
                var rate = SipXirr(closes, cursor, end, amount, o);
                if (rate is not null)
                {
                    points.Add(new JsonObject { ["start"] = Iso(cursor), ["end"] = Iso(end), ["xirr"] = Finite(rate) });
                    if (Finite(rate) is { } f) rates.Add(f);
                }

                var month = cursor.Month - 1 + stepMonths;
                try { cursor = new DateOnly(cursor.Year + month / 12, month % 12 + 1, Math.Min(cursor.Day, 28)); }
                catch (ArgumentOutOfRangeException) { break; }
            }

            if (points.Count == 0) continue;
            result.Add(new JsonObject
            {
                ["years"] = span, ["windows"] = points.Count, ["points"] = points, ["best"] = rates.Count > 0 ? Finite(rates.Max()) : null, ["worst"] = rates.Count > 0 ? Finite(rates.Min()) : null,
                ["median"] = rates.Count > 0 ? Finite(Median(rates)) : null, ["positive_share"] = rates.Count > 0 ? Finite((double)rates.Count(r => r > 0) / rates.Count) : null,
            });
        }

        return result;
    }

    public static JsonObject StartDateHeatmap(PriceSeries closes, double amount, SipOptions o, int[]? durations = null)
    {
        if (closes.IsEmpty) return new JsonObject { ["rows"] = new JsonArray(), ["columns"] = new JsonArray(), ["values"] = new JsonArray() };
        var first = closes.Dates[0];
        var last = closes.Dates[^1];
        var starts = new List<DateOnly>();
        for (var cursor = new DateOnly(first.Year, first.Month, 1); cursor <= last; cursor = cursor.AddMonths(1)) starts.Add(cursor);
        var usable = (durations ?? RollingYears).Where(d => d != 0).ToArray();
        while (starts.Count > 0 && starts.Count * usable.Length > MaxGridCells) starts = starts.Where((_, i) => i % 2 == 0).ToList();

        var values = new JsonArray();
        foreach (var s in starts)
        {
            var row = new JsonArray();
            foreach (var span in usable)
            {
                DateOnly end;
                try { end = new DateOnly(s.Year + span, s.Month, 1); }
                catch (ArgumentOutOfRangeException) { row.Add(null); continue; }
                row.Add(end <= last ? Finite(SipXirr(closes, s, end < last ? end : last, amount, o)) : null);
            }

            values.Add(row);
        }

        return new JsonObject
        {
            ["rows"] = new JsonArray(starts.Select(s => (JsonNode)s.ToString("MMM yyyy", CultureInfo.InvariantCulture)).ToArray()),
            ["columns"] = new JsonArray(usable.Select(d => (JsonNode)$"{d}Y").ToArray()), ["values"] = values,
        };
    }

    public static JsonObject SipDateHeatmap(PriceSeries closes, DateOnly start, DateOnly end, double amount, SipOptions o)
    {
        var days = new JsonArray();
        var rates = new List<double?>();
        for (var day = 1; day <= 28; day++)
        {
            days.Add(day);
            rates.Add(Finite(SipXirr(closes, start, end, amount, o with { DayOfMonth = day })));
        }

        var clean = rates.Where(r => r is not null).Select(r => r!.Value).ToList();
        return new JsonObject
        {
            ["days"] = days, ["values"] = new JsonArray(rates.Select(r => (JsonNode?)r).ToArray()),
            ["best_day"] = clean.Count > 0 ? rates.IndexOf(clean.Max()) + 1 : null, ["worst_day"] = clean.Count > 0 ? rates.IndexOf(clean.Min()) + 1 : null,
            ["spread"] = clean.Count > 0 ? Finite(clean.Max() - clean.Min()) : null,
        };
    }

    public static JsonArray FrequencyComparison(PriceSeries closes, DateOnly start, DateOnly end, double monthlyAmount, SipOptions o)
    {
        var result = new JsonArray();
        var annual = monthlyAmount * 12;
        foreach (var (freq, count) in new[] { ("weekly", 52), ("fortnightly", 26), ("monthly", 12), ("quarterly", 4) })
        {
            var amount = annual / count;
            SipResult r;
            try { r = SipEngine.Run(closes, "x", "x", start, end, amount, o with { Frequency = freq }); }
            catch (Exception e) when (e is not OutOfMemoryException) { continue; }
            result.Add(new JsonObject
            {
                ["frequency"] = freq, ["amount_per_installment"] = Finite(amount), ["installments"] = r.InstallmentCount, ["invested"] = Finite(r.TotalInvested), ["final_value"] = Finite(r.FinalValue),
                ["xirr"] = Finite(Xirr.OrNull(r.CashFlows)), ["average_cost"] = Finite(r.AverageCost), ["charges"] = Finite(r.Charges),
            });
        }

        return result;
    }

    public static JsonArray YearlyBreakdown(SipResult r)
    {
        var result = new JsonArray();
        var i = 0;
        while (i < r.Dates.Length)
        {
            var year = r.Dates[i].Year;
            var j = i;
            while (j + 1 < r.Dates.Length && r.Dates[j + 1].Year == year) j++;
            double openValue = r.Value[i], openInvested = r.Invested[i], closeValue = r.Value[j], closeInvested = r.Invested[j];
            var added = closeInvested - openInvested;
            result.Add(new JsonObject
            {
                ["year"] = year, ["invested_during_year"] = Finite(added), ["invested_to_date"] = Finite(closeInvested), ["value"] = Finite(closeValue), ["gain"] = Finite(closeValue - closeInvested),
                ["change"] = Finite(closeValue - openValue - added),
            });
            i = j + 1;
        }

        return result;
    }

    public static JsonObject MonthlyValueHeatmap(SipResult r)
    {
        var first = new DateOnly(r.Dates[0].Year, r.Dates[0].Month, 1);
        var last = new DateOnly(r.Dates[^1].Year, r.Dates[^1].Month, 1);
        var months = new List<(DateOnly Month, double Value, double Invested)>();
        for (var m = first; m <= last; m = m.AddMonths(1))
        {
            var idx = -1;
            for (var i = 0; i < r.Dates.Length; i++)
                if (r.Dates[i].Year == m.Year && r.Dates[i].Month == m.Month) idx = i;
            months.Add(idx >= 0 ? (m, r.Value[idx], r.Invested[idx]) : (m, double.NaN, double.NaN));
        }

        var grid = new SortedDictionary<int, double?[]>();
        for (var i = 0; i < months.Count; i++)
        {
            double? pct = null;
            if (i > 0)
            {
                var priorClose = months[i - 1].Value;
                var added = months[i].Invested - months[i - 1].Invested;
                var market = months[i].Value - priorClose - added;
                var baseValue = priorClose != 0 ? priorClose : double.NaN;
                pct = market / baseValue;
            }

            if (!grid.TryGetValue(months[i].Month.Year, out var row)) grid[months[i].Month.Year] = row = new double?[12];
            row[months[i].Month.Month - 1] = Finite(pct);
        }

        return new JsonObject
        {
            ["years"] = new JsonArray(grid.Keys.Select(y => (JsonNode)y.ToString(CultureInfo.InvariantCulture)).ToArray()),
            ["columns"] = new JsonArray(new[] { "Jan", "Feb", "Mar", "Apr", "May", "Jun", "Jul", "Aug", "Sep", "Oct", "Nov", "Dec" }.Select(m => (JsonNode)m).ToArray()),
            ["values"] = new JsonArray(grid.Values.Select(row => (JsonNode)new JsonArray(row.Select(v => (JsonNode?)v).ToArray())).ToArray()),
            ["basis"] = "close-to-close, contribution removed",
        };
    }

    public static JsonObject? LumpsumComparison(PriceSeries closes, SipResult r, DateOnly start, DateOnly end, SipOptions o)
    {
        SipEngine.Lumpsum lump;
        try { lump = SipEngine.RunLumpsum(closes, start, end, r.TotalInvested, o); }
        catch (Exception e) when (e is not OutOfMemoryException) { return null; }
        return new JsonObject
        {
            ["invested"] = Finite(lump.Invested), ["final_value"] = Finite(lump.FinalValue), ["xirr"] = Finite(Xirr.OrNull(lump.CashFlows)), ["entry_price"] = Finite(lump.EntryPrice),
            ["entry_date"] = Iso(lump.EntryDate), ["sip_final_value"] = Finite(r.FinalValue), ["difference"] = Finite(lump.FinalValue - r.FinalValue), ["sip_won"] = r.FinalValue > lump.FinalValue,
        };
    }
}
