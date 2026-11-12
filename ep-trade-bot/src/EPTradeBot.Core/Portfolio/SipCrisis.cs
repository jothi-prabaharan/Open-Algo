using System.Globalization;
using System.Text.Json.Nodes;

namespace EPTradeBot.Core.Portfolio;

/// <summary>
/// Whether starting a SIP into a crisis beat waiting for it to end, each run from the same history to the same final date. OpenAlgo's sip.crisis: only crises the data fully covers, with at least
/// 120 days of runway after, are reported, newest first.
/// </summary>
public static class SipCrisis
{
    public const int MinSipDays = 120;

    private static JsonObject? Run(PriceSeries closes, DateOnly start, DateOnly end, double amount, SipOptions o)
    {
        SipResult r;
        try { r = SipEngine.Run(closes, "x", "x", start, end, amount, o); }
        catch (Exception e) when (e is not OutOfMemoryException) { return null; }
        return new JsonObject
        {
            ["start"] = start.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), ["invested"] = SipAnalytics.Finite(r.TotalInvested), ["final_value"] = SipAnalytics.Finite(r.FinalValue),
            ["installments"] = r.InstallmentCount, ["xirr"] = SipAnalytics.Finite(Xirr.OrNull(r.CashFlows)), ["multiple"] = r.TotalInvested != 0 ? SipAnalytics.Finite(r.FinalValue / r.TotalInvested) : null,
            ["absolute_return"] = r.TotalInvested != 0 ? SipAnalytics.Finite((r.FinalValue - r.TotalInvested) / r.TotalInvested) : null, ["average_cost"] = SipAnalytics.Finite(r.AverageCost),
        };
    }

    public static List<JsonObject> Analyse(PriceSeries closes, double amount, SipOptions o, IReadOnlyList<CrisisPeriod>? periods = null)
    {
        var rows = new List<JsonObject>();
        if (closes is null || closes.IsEmpty) return rows;
        var first = closes.Dates[0];
        var last = closes.Dates[^1];
        foreach (var period in periods ?? CrisisPeriod.India)
        {
            var cStart = DateOnly.ParseExact(period.Start, "yyyy-MM-dd", CultureInfo.InvariantCulture);
            var cEnd = DateOnly.ParseExact(period.End, "yyyy-MM-dd", CultureInfo.InvariantCulture);
            if (cStart < first || cEnd > last) continue;
            if (last.DayNumber - cEnd.DayNumber < MinSipDays) continue;
            var into = Run(closes, cStart, last, amount, o);
            var waited = Run(closes, cEnd, last, amount, o);
            if (into is null || waited is null) continue;

            var inside = closes.Dates.Zip(closes.Closes).Where(r => r.First >= cStart && r.First <= cEnd).Select(r => r.Second).ToList();
            double? move = null, trough = null;
            if (inside.Count >= 2)
            {
                move = inside[^1] / inside[0] - 1.0;
                trough = inside.Min() / inside[0] - 1.0;
            }

            var intoX = into["xirr"]?.GetValue<double>();
            var waitedX = waited["xirr"]?.GetValue<double>();
            rows.Add(new JsonObject
            {
                ["key"] = period.Key, ["label"] = period.Label, ["note"] = period.Note, ["scope"] = period.Scope, ["crisis_start"] = period.Start, ["crisis_end"] = period.End,
                ["market_move"] = SipAnalytics.Finite(move), ["market_trough"] = SipAnalytics.Finite(trough), ["started_into_it"] = into, ["waited_for_the_end"] = waited,
                ["xirr_advantage"] = intoX is { } a && waitedX is { } b ? SipAnalytics.Finite(a - b) : null, ["starting_early_won"] = intoX is { } c && waitedX is { } d && c > d,
            });
        }

        return rows.OrderByDescending(r => r["crisis_start"]!.GetValue<string>(), StringComparer.Ordinal).ToList();
    }

    public static JsonObject Summary(IReadOnlyList<JsonObject> rows)
    {
        var scored = rows.Where(r => r["xirr_advantage"] is not null).ToList();
        if (scored.Count == 0) return new JsonObject { ["crises"] = 0, ["early_wins"] = 0, ["share"] = null, ["median_advantage"] = null };
        var wins = scored.Count(r => r["starting_early_won"]!.GetValue<bool>());
        var advantages = scored.Select(r => r["xirr_advantage"]!.GetValue<double>()).OrderBy(v => v).ToList();
        var mid = advantages.Count / 2;
        var median = advantages.Count % 2 == 1 ? advantages[mid] : (advantages[mid - 1] + advantages[mid]) / 2;
        return new JsonObject { ["crises"] = scored.Count, ["early_wins"] = wins, ["share"] = SipAnalytics.Finite((double)wins / scored.Count), ["median_advantage"] = SipAnalytics.Finite(median) };
    }
}
