using static EPTradeBot.Core.Risk.RiskValues;

namespace EPTradeBot.Core.Risk;

/// <summary>Port of OpenAlgo services/risk/aggregate.py. Pure.</summary>
public static class AggregateEvaluator
{
    /// <summary>Mark to market for one open position; zero when it cannot be marked.</summary>
    public static double PositionPnl(Side side, double entryPrice, double quantity, double? lastPrice)
    {
        if (!IsPrice(entryPrice) || !IsPrice(lastPrice) || quantity == 0) return 0.0;
        var q = Math.Abs(quantity);
        return side == Side.BUY ? (lastPrice!.Value - entryPrice) * q : (entryPrice - lastPrice!.Value) * q;
    }

    /// <summary>Marks open positions from their own entry and last price; never from a stale stored figure.</summary>
    public static PnLSummary AggregatePnl(IEnumerable<PositionPnL> positions)
    {
        double realized = 0, unrealized = 0;
        int priced = 0, unpriced = 0;
        foreach (var p in positions)
        {
            realized += p.RealizedPnl;
            if (p.Closed) continue;
            if (!IsPrice(p.LastPrice) || !IsPrice(p.EntryPrice))
            {
                unpriced++;
                continue;
            }

            priced++;
            unrealized += PositionPnl(p.Side, p.EntryPrice, p.Quantity, p.LastPrice);
        }

        return new PnLSummary(realized, unrealized, realized + unrealized, priced, unpriced);
    }

    public static AggregateDecision Evaluate(AggregateRisk risk, double realizedPnl, double unrealizedPnl)
    {
        var total = realizedPnl + unrealizedPnl;
        var peak = Math.Max(risk.PeakPnl, total);
        var trough = Math.Min(risk.TroughPnl, total);
        var lockArmed = risk.LockArmed;
        var lockFloor = risk.LockFloor;
        var armedNow = false;
        var floorRaised = false;

        if (risk.LockProfitAt is { } at)
        {
            if (!lockArmed && total >= at)
            {
                lockArmed = true;
                armedNow = true;
            }

            if (lockArmed)
            {
                var candidate = risk.LockProfitFloor ?? 0.0;
                if (lockFloor is { } existing) candidate = Math.Max(candidate, existing);
                if (risk.LockTrailStep is { } step && step > 0.0) candidate = Math.Max(candidate, peak - step);
                floorRaised = !armedNow && lockFloor is { } prior && candidate > prior;
                lockFloor = candidate;
            }
        }

        AggregateDecision Decide(BreachReason? reason, string detail) => new()
        {
            TotalPnl = total,
            RealizedPnl = realizedPnl,
            UnrealizedPnl = unrealizedPnl,
            PeakPnl = peak,
            TroughPnl = trough,
            LockArmed = lockArmed,
            LockFloor = lockFloor,
            LockArmedNow = armedNow,
            LockFloorRaised = floorRaised,
            Breached = reason is not null,
            Reason = reason,
            Detail = detail,
        };

        if (risk.LockProfitAt is { } threshold && lockArmed && lockFloor is { } floor && total <= floor)
        {
            if (armedNow && floor > threshold)
            {
                return Decide(BreachReason.LockProfit,
                    $"lock profit floor {FormatPrice(floor)} is above its activation threshold {FormatPrice(threshold)}, so it triggered on the tick it became active; the floor must be below the threshold");
            }

            return Decide(BreachReason.LockProfit,
                $"lock profit triggered: mark to market {FormatPrice(total)} fell to or below the locked floor {FormatPrice(floor)}");
        }

        if (!risk.StopBypassed && risk.CombinedStopLoss is { } sl)
        {
            var limit = Math.Abs(sl);
            if (total <= -limit)
                return Decide(BreachReason.CombinedStop, $"combined stop loss hit: mark to market {FormatPrice(total)} fell to or below the limit {FormatPrice(-limit)}");
        }

        if (risk.CombinedTarget is { } tgt && total >= tgt)
            return Decide(BreachReason.CombinedTarget, $"combined target hit: mark to market {FormatPrice(total)} reached the target {FormatPrice(tgt)}");

        var note = "";
        if (armedNow) note = $"lock profit active at {FormatPrice(total)}, floor set to {FormatPrice(lockFloor!.Value)}";
        else if (floorRaised && lockFloor is { } raised) note = $"lock profit floor raised to {FormatPrice(raised)} on a peak of {FormatPrice(peak)}";
        return Decide(null, note);
    }

    /// <summary>
    /// Moves every remaining stop to its own entry, returning the moves rather than
    /// applying them. Skips a move that would not tighten the stop, or that would put
    /// the stop through the current market (an instant exit at a loss).
    /// </summary>
    public static TrailToEntryDecision TrailStopsToEntry(
        IEnumerable<PositionRisk> positions,
        IEnumerable<string>? exclude = null,
        IReadOnlyDictionary<string, double>? lastPrices = null)
    {
        var excluded = new HashSet<string>(exclude ?? [], StringComparer.Ordinal);
        var moves = new List<StopMove>();
        var notImproving = new List<string>();
        var throughPrice = new List<string>();
        var noEntry = new List<string>();
        foreach (var risk in positions)
        {
            if (excluded.Contains(risk.Identifier)) continue;
            if (!IsPrice(risk.EntryPrice))
            {
                noEntry.Add(risk.Identifier);
                continue;
            }

            var entry = risk.EntryPrice;
            var current = risk.EffectiveStop;
            var isLong = risk.IsLong;
            if (current is { } c && (isLong ? entry <= c : entry >= c))
            {
                notImproving.Add(risk.Identifier);
                continue;
            }

            if (lastPrices is not null && lastPrices.TryGetValue(risk.Identifier, out var lp) && AsPrice(lp) is { } reference
                && (isLong ? entry >= reference : entry <= reference))
            {
                throughPrice.Add(risk.Identifier);
                continue;
            }

            moves.Add(new StopMove(risk.Identifier, current, entry));
        }

        var detail = "";
        if (moves.Count > 0)
        {
            detail = $"moved {moves.Count} stop(s) to entry";
            if (throughPrice.Count > 0) detail += $"; left {throughPrice.Count} alone because entry is already through the market";
        }
        else if (throughPrice.Count > 0 || notImproving.Count > 0 || noEntry.Count > 0)
        {
            detail = "no stop moved to entry";
        }

        return new TrailToEntryDecision
        {
            Moves = moves, SkippedNotImproving = notImproving, SkippedThroughPrice = throughPrice, SkippedNoEntry = noEntry, Detail = detail,
        };
    }
}
