using System.Globalization;

namespace EPTradeBot.Core.Risk;

// Port of OpenAlgo services/risk/models.py. Pure: no clock, database, broker,
// feed or logger. Every type is an immutable record, so a decision can cross
// threads or be queued without anyone mutating it.

public enum Side { BUY, SELL }

/// <summary>CONTINUOUS keeps a fixed gap behind the best price; STEPPED advances the configured stop one step per trigger.</summary>
public enum TrailMode { Continuous, Stepped }

/// <summary>Why a rule fired. Wire values match OpenAlgo: sl, target, combined_sl, combined_target, lock_profit.</summary>
public enum BreachReason { Stop, Target, CombinedStop, CombinedTarget, LockProfit }

public static class RiskValues
{
    /// <summary>The favourable excursion a position must show before a trail moves the stop (OpenAlgo MIN_TRAIL_PROFIT).</summary>
    public const double DefaultTrailTrigger = 1.0;

    public static string Wire(this BreachReason reason) => reason switch
    {
        BreachReason.Stop => "sl",
        BreachReason.Target => "target",
        BreachReason.CombinedStop => "combined_sl",
        BreachReason.CombinedTarget => "combined_target",
        BreachReason.LockProfit => "lock_profit",
        _ => throw new ArgumentOutOfRangeException(nameof(reason)),
    };

    /// <summary>
    /// A usable price is finite and strictly positive. Zero is never a price: a zero
    /// stop on a short would otherwise breach on every tick.
    /// </summary>
    public static bool IsPrice(double? value) => value is { } v && double.IsFinite(v) && v > 0.0;

    public static double? AsPrice(double? value) => IsPrice(value) ? value : null;

    public static double AsDouble(object? value, double fallback = 0.0) => value switch
    {
        null or bool => fallback,
        double d => double.IsFinite(d) ? d : fallback,
        float f => double.IsFinite(f) ? f : fallback,
        int i => i,
        long l => l,
        decimal m => (double)m,
        string s when double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) && double.IsFinite(d) => d,
        _ => fallback,
    };

    public static double? AsPriceObject(object? value)
    {
        if (value is null or bool) return null;
        var d = AsDouble(value, double.NaN);
        return IsPrice(d) ? d : null;
    }

    public static double? OptionalDouble(object? value)
    {
        if (value is null or bool) return null;
        var d = AsDouble(value, double.NaN);
        return double.IsFinite(d) ? d : null;
    }

    /// <summary>Unknown values fall back to BUY, as OpenAlgo's scalping engine does.</summary>
    public static Side NormaliseSide(object? value)
    {
        if (value is Side s) return s;
        var text = (Convert.ToString(value, CultureInfo.InvariantCulture) ?? "").Trim().ToUpperInvariant();
        return text is "SELL" or "S" or "SHORT" or "-1" ? Side.SELL : Side.BUY;
    }

    public static TrailMode NormaliseTrailMode(object? value)
    {
        if (value is TrailMode m) return m;
        var text = (Convert.ToString(value, CultureInfo.InvariantCulture) ?? "").Trim().ToLowerInvariant();
        return text is "stepped" or "step" or "staircase" ? TrailMode.Stepped : TrailMode.Continuous;
    }

    public static Side SideFromQuantity(double netQuantity) => netQuantity < 0.0 ? Side.SELL : Side.BUY;

    public static double? StopFromPoints(Side side, double entry, double points)
    {
        if (!IsPrice(entry) || !IsPrice(points)) return null;
        var price = side == Side.BUY ? entry - points : entry + points;
        return price > 0.0 ? price : null;
    }

    public static double? TargetFromPoints(Side side, double entry, double points)
    {
        if (!IsPrice(entry) || !IsPrice(points)) return null;
        var price = side == Side.BUY ? entry + points : entry - points;
        return price > 0.0 ? price : null;
    }

    /// <summary>Compact plain-text price for a detail message. No currency symbol.</summary>
    public static string FormatPrice(double value)
    {
        var text = value.ToString("F4", CultureInfo.InvariantCulture).TrimEnd('0').TrimEnd('.');
        return text.Length == 0 ? "0" : text;
    }
}

/// <summary>Everything needed to judge one position against one tick. <see cref="Quantity"/> is a magnitude; <see cref="Side"/> carries direction.</summary>
public sealed record PositionRisk
{
    public string Identifier { get; init; } = "";
    public Side Side { get; init; } = Side.BUY;
    public double EntryPrice { get; init; }
    public double Quantity { get; init; }

    /// <summary>The live stop, which a trail may already have moved.</summary>
    public double? StopPrice { get; init; }

    /// <summary>The stop the user configured: the anchor a stepped trail advances from.</summary>
    public double? InitialStopPrice { get; init; }

    public double? TargetPrice { get; init; }
    public bool TrailingEnabled { get; init; }
    public double TrailStep { get; init; }
    public double TrailTrigger { get; init; } = RiskValues.DefaultTrailTrigger;
    public TrailMode TrailMode { get; init; } = TrailMode.Continuous;
    public double? HighestPrice { get; init; }
    public double? LowestPrice { get; init; }

    public bool IsLong => Side == Side.BUY;

    /// <summary>The stop in force: the live one, else the configured one. Never a fallback to entry.</summary>
    public double? EffectiveStop => RiskValues.AsPrice(StopPrice) ?? RiskValues.AsPrice(InitialStopPrice);

    /// <summary>Builds from a loose map using either the scalping field names or the canonical ones.</summary>
    public static PositionRisk FromState(IReadOnlyDictionary<string, object?> state)
    {
        object? First(params string[] names)
        {
            foreach (var n in names)
            {
                if (state.TryGetValue(n, out var v) && v is not null) return v;
            }

            return null;
        }

        var entry = RiskValues.AsDouble(First("entry_price", "entry", "entry_avg", "average_price"));
        var side = RiskValues.NormaliseSide(First("side", "action", "position"));
        var stop = RiskValues.AsPriceObject(First("stop_price", "current_sl", "currentSl"));
        var initialStop = RiskValues.AsPriceObject(First("initial_stop_price", "initial_sl", "initialSl"));
        var target = RiskValues.AsPriceObject(First("target_price", "target", "targetPrice"));
        if (stop is null && initialStop is null)
            initialStop = RiskValues.StopFromPoints(side, entry, RiskValues.AsDouble(First("sl_points", "sl_pts")));
        target ??= RiskValues.TargetFromPoints(side, entry, RiskValues.AsDouble(First("target_points", "target_pts")));
        var trigger = First("trail_trigger", "trailing_trigger", "trail_x");

        return new PositionRisk
        {
            Identifier = Convert.ToString(First("identifier", "id", "symbol"), CultureInfo.InvariantCulture) ?? "",
            Side = side,
            EntryPrice = entry,
            Quantity = Math.Abs(RiskValues.AsDouble(First("quantity", "qty"))),
            StopPrice = stop,
            InitialStopPrice = initialStop,
            TargetPrice = target,
            TrailingEnabled = First("trailing_enabled", "trailingEnabled") is true,
            TrailStep = RiskValues.AsDouble(First("trail_step", "trailing_step", "trailingStep", "trail_y")),
            TrailTrigger = trigger is null ? RiskValues.DefaultTrailTrigger : RiskValues.AsDouble(trigger, RiskValues.DefaultTrailTrigger),
            TrailMode = RiskValues.NormaliseTrailMode(First("trail_mode", "trailMode")),
            HighestPrice = RiskValues.AsPriceObject(First("highest_price", "highestPrice")),
            LowestPrice = RiskValues.AsPriceObject(First("lowest_price", "lowestPrice")),
        };
    }
}

/// <summary>
/// What one tick did to one position. When <see cref="Evaluated"/> is false the tick was
/// unusable and every other field is the input carried through, so a caller can always write it back.
/// </summary>
public sealed record PositionDecision
{
    public string Identifier { get; init; } = "";
    public bool Evaluated { get; init; } = true;
    public double? StopPrice { get; init; }
    public double? TargetPrice { get; init; }
    public double? HighestPrice { get; init; }
    public double? LowestPrice { get; init; }
    public double Pnl { get; init; }
    public bool Breached { get; init; }
    public BreachReason? Reason { get; init; }
    public string Detail { get; init; } = "";
    public bool StopMoved { get; init; }
    public bool TrailArmed { get; init; }
}

/// <summary>One position's contribution to an aggregate mark to market.</summary>
public sealed record PositionPnL
{
    public string Identifier { get; init; } = "";
    public Side Side { get; init; } = Side.BUY;
    public double EntryPrice { get; init; }
    public double Quantity { get; init; }
    public double? LastPrice { get; init; }
    public bool Closed { get; init; }
    public double RealizedPnl { get; init; }
}

public sealed record PnLSummary(double Realized, double Unrealized, double Total, int Priced, int Unpriced);

/// <summary>Portfolio limits and the ratchet state carried between ticks. Amounts are currency, not prices.</summary>
public sealed record AggregateRisk
{
    /// <summary>Read as a magnitude: 5000 and -5000 mean the same loss.</summary>
    public double? CombinedStopLoss { get; init; }
    public double? CombinedTarget { get; init; }
    public double? LockProfitAt { get; init; }
    public double? LockProfitFloor { get; init; }
    public double? LockTrailStep { get; init; }
    public bool LockArmed { get; init; }
    public double? LockFloor { get; init; }
    public double PeakPnl { get; init; }
    public double TroughPnl { get; init; }
    public bool StopBypassed { get; init; }
}

/// <summary>Ratchet fields are always returned, breach or not, because the caller must persist them.</summary>
public sealed record AggregateDecision
{
    public double TotalPnl { get; init; }
    public double RealizedPnl { get; init; }
    public double UnrealizedPnl { get; init; }
    public double PeakPnl { get; init; }
    public double TroughPnl { get; init; }
    public bool LockArmed { get; init; }
    public double? LockFloor { get; init; }
    public bool LockArmedNow { get; init; }
    public bool LockFloorRaised { get; init; }
    public bool Breached { get; init; }
    public BreachReason? Reason { get; init; }
    public string Detail { get; init; } = "";
}

public sealed record StopMove(string Identifier, double? PreviousStop, double NewStop);

public sealed record TrailToEntryDecision
{
    public IReadOnlyList<StopMove> Moves { get; init; } = [];
    public IReadOnlyList<string> SkippedNotImproving { get; init; } = [];
    public IReadOnlyList<string> SkippedThroughPrice { get; init; } = [];
    public IReadOnlyList<string> SkippedNoEntry { get; init; } = [];
    public string Detail { get; init; } = "";
}
