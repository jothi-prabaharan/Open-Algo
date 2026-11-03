using static EPTradeBot.Core.Risk.RiskValues;

namespace EPTradeBot.Core.Risk;

/// <summary>
/// Port of OpenAlgo services/risk/position.py: stop loss, target and trailing stop
/// for one position and one tick. The reconciliation notes in that file apply
/// unchanged: no implicit stop at entry, zero is not a price, an unusable tick leaves
/// the state alone, the trail arms off the favourable peak, a trail never puts the
/// stop beyond the best traded price, and the stop wins when both hit on one tick.
/// </summary>
public static class PositionEvaluator
{
    public static PositionDecision Evaluate(PositionRisk risk, double? lastPrice)
    {
        if (!IsPrice(lastPrice))
        {
            return new PositionDecision
            {
                Identifier = risk.Identifier,
                Evaluated = false,
                StopPrice = risk.EffectiveStop,
                TargetPrice = AsPrice(risk.TargetPrice),
                HighestPrice = risk.HighestPrice,
                LowestPrice = risk.LowestPrice,
                Detail = "tick ignored: last price is missing, zero, negative or not finite",
            };
        }

        var ltp = lastPrice!.Value;
        var isLong = risk.IsLong;
        double? entry = IsPrice(risk.EntryPrice) ? risk.EntryPrice : null;
        var seed = entry ?? ltp;
        var highest = risk.HighestPrice;
        var lowest = risk.LowestPrice;
        if (isLong) highest = Math.Max(highest ?? seed, ltp);
        else lowest = Math.Min(lowest ?? seed, ltp);

        var favourable = 0.0;
        if (entry is { } e)
        {
            var best = isLong ? highest : lowest;
            if (best is { } b) favourable = Math.Max(0.0, isLong ? b - e : e - b);
        }

        var stop = risk.EffectiveStop;
        var originalStop = stop;
        var trailArmed = false;
        var trigger = Math.Max(0.0, risk.TrailTrigger);
        var canTrail = risk.TrailingEnabled && risk.TrailStep > 0.0 && double.IsFinite(risk.TrailStep) && entry is not null;
        if (canTrail && favourable > 0.0 && favourable >= trigger)
        {
            trailArmed = true;
            var candidate = TrailCandidate(risk, favourable, highest, lowest, trigger);
            if (candidate is { } c)
            {
                c = ClampToPeak(c, isLong, highest, lowest);
                if (stop is null || (isLong ? c > stop : c < stop)) stop = c;
            }
        }

        var stopMoved = stop != originalStop;
        var target = AsPrice(risk.TargetPrice);
        var stopHit = stop is { } s && (isLong ? ltp <= s : ltp >= s);
        var targetHit = target is { } t && (isLong ? ltp >= t : ltp <= t);

        BreachReason? reason = null;
        var detail = "";
        var dir = isLong ? "long" : "short";
        if (stopHit)
        {
            reason = BreachReason.Stop;
            detail = $"stop loss hit: last price {FormatPrice(ltp)} is at or {(isLong ? "below" : "above")} the stop {FormatPrice(stop!.Value)} on a {dir} position";
        }
        else if (targetHit)
        {
            reason = BreachReason.Target;
            detail = $"target hit: last price {FormatPrice(ltp)} is at or {(isLong ? "above" : "below")} the target {FormatPrice(target!.Value)} on a {dir} position";
        }
        else if (stopMoved && stop is { } moved)
        {
            var previous = originalStop is { } o ? FormatPrice(o) : "none";
            detail = $"trailing stop moved from {previous} to {FormatPrice(moved)} after {FormatPrice(favourable)} of favourable movement";
        }

        var pnl = 0.0;
        if (entry is { } en && risk.Quantity != 0) pnl = isLong ? (ltp - en) * risk.Quantity : (en - ltp) * risk.Quantity;

        return new PositionDecision
        {
            Identifier = risk.Identifier,
            Evaluated = true,
            StopPrice = stop,
            TargetPrice = target,
            HighestPrice = highest,
            LowestPrice = lowest,
            Pnl = pnl,
            Breached = reason is not null,
            Reason = reason,
            Detail = detail,
            StopMoved = stopMoved,
            TrailArmed = trailArmed,
        };
    }

    /// <summary>Plain-text reasons a configuration is unusable; empty when it is fine.</summary>
    public static IReadOnlyList<string> Validate(PositionRisk risk, double? lastPrice = null)
    {
        var problems = new List<string>();
        var isLong = risk.IsLong;
        double? entry = IsPrice(risk.EntryPrice) ? risk.EntryPrice : null;
        var reference = IsPrice(lastPrice) ? lastPrice : entry;
        if (entry is null) problems.Add("entry price is missing or not a positive number");

        if (risk.EffectiveStop is { } stop && reference is { } r)
        {
            if (isLong && stop >= r) problems.Add($"stop {FormatPrice(stop)} is at or above {FormatPrice(r)} on a long position, which exits immediately");
            if (!isLong && stop <= r) problems.Add($"stop {FormatPrice(stop)} is at or below {FormatPrice(r)} on a short position, which exits immediately");
        }

        if (AsPrice(risk.TargetPrice) is { } target && reference is { } rt)
        {
            if (isLong && target <= rt) problems.Add($"target {FormatPrice(target)} is at or below {FormatPrice(rt)} on a long position, which exits immediately");
            if (!isLong && target >= rt) problems.Add($"target {FormatPrice(target)} is at or above {FormatPrice(rt)} on a short position, which exits immediately");
        }

        if (risk.TrailingEnabled && !(risk.TrailStep > 0.0)) problems.Add("trailing is enabled but the trail step is not a positive number");
        if (risk.TrailingEnabled && risk.TrailMode == TrailMode.Stepped && !(risk.TrailTrigger > 0.0)) problems.Add("a stepped trail needs a positive trail trigger");
        if (risk.TrailingEnabled && risk.TrailMode == TrailMode.Stepped && risk.TrailStep > risk.TrailTrigger && risk.TrailTrigger > 0.0)
            problems.Add("a stepped trail with a step larger than its trigger gives back more than it locks in; reduce the step or raise the trigger");
        return problems;
    }

    private static double? TrailCandidate(PositionRisk risk, double favourable, double? highest, double? lowest, double trigger)
    {
        if (risk.TrailMode == TrailMode.Stepped)
        {
            if (trigger <= 0.0) return null;
            var anchor = AsPrice(risk.InitialStopPrice) ?? risk.EffectiveStop;
            if (anchor is null) return null;
            var steps = Math.Floor(favourable / trigger);
            if (steps <= 0) return null;
            var advance = steps * risk.TrailStep;
            return risk.IsLong ? anchor + advance : anchor - advance;
        }

        return risk.IsLong ? highest - risk.TrailStep : lowest + risk.TrailStep;
    }

    private static double ClampToPeak(double candidate, bool isLong, double? highest, double? lowest)
    {
        if (isLong && highest is { } h) return Math.Min(candidate, h);
        if (!isLong && lowest is { } l) return Math.Max(candidate, l);
        return candidate;
    }
}
