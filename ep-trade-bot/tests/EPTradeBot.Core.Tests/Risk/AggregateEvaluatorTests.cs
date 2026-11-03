using EPTradeBot.Core.Risk;

namespace EPTradeBot.Core.Tests.Risk;

/// <summary>Aggregate rules, including the defects OpenAlgo's port of the strategy module carried.</summary>
public sealed class AggregateEvaluatorTests
{
    [Fact]
    public void Combined_stop_is_a_magnitude()
    {
        var a = AggregateEvaluator.Evaluate(new AggregateRisk { CombinedStopLoss = 5000 }, -2000, -3000);
        var b = AggregateEvaluator.Evaluate(new AggregateRisk { CombinedStopLoss = -5000 }, -2000, -3000);
        Assert.Equal(BreachReason.CombinedStop, a.Reason);
        Assert.Equal(BreachReason.CombinedStop, b.Reason);
    }

    [Fact]
    public void Lock_profit_arms_then_trails_and_never_falls_back()
    {
        var risk = new AggregateRisk { LockProfitAt = 1000, LockProfitFloor = 500, LockTrailStep = 300 };
        var armed = AggregateEvaluator.Evaluate(risk, 0, 1200);
        Assert.True(armed.LockArmedNow);
        Assert.Equal(900, armed.LockFloor);

        var higher = AggregateEvaluator.Evaluate(risk with { LockArmed = true, LockFloor = armed.LockFloor, PeakPnl = armed.PeakPnl }, 0, 2000);
        Assert.Equal(1700, higher.LockFloor);
        Assert.True(higher.LockFloorRaised);

        var pullback = AggregateEvaluator.Evaluate(risk with { LockArmed = true, LockFloor = higher.LockFloor, PeakPnl = higher.PeakPnl }, 0, 1650);
        Assert.Equal(BreachReason.LockProfit, pullback.Reason);
        Assert.Equal(1700, pullback.LockFloor);
    }

    [Fact]
    public void Peak_and_trough_are_returned_even_without_a_breach()
    {
        // PORTED DEFECT: the source module persisted peak and trough as zero on most paths.
        var d = AggregateEvaluator.Evaluate(new AggregateRisk { PeakPnl = 100, TroughPnl = -50 }, 0, -80);
        Assert.False(d.Breached);
        Assert.Equal(100, d.PeakPnl);
        Assert.Equal(-80, d.TroughPnl);
    }

    [Fact]
    public void Aggregate_marks_open_positions_from_their_own_prices()
    {
        // PORTED DEFECT: the source module summed a stale per-leg mtm field.
        var summary = AggregateEvaluator.AggregatePnl(
        [
            new PositionPnL { Identifier = "a", Side = Side.BUY, EntryPrice = 100, Quantity = 10, LastPrice = 105 },
            new PositionPnL { Identifier = "b", Side = Side.SELL, EntryPrice = 200, Quantity = 5, LastPrice = 210 },
            new PositionPnL { Identifier = "c", Closed = true, RealizedPnl = 75 },
            new PositionPnL { Identifier = "d", Side = Side.BUY, EntryPrice = 50, Quantity = 1, LastPrice = null },
        ]);
        Assert.Equal(75, summary.Realized);
        Assert.Equal(0, summary.Unrealized);
        Assert.Equal(2, summary.Priced);
        Assert.Equal(1, summary.Unpriced);
    }

    [Fact]
    public void Trail_to_entry_never_moves_a_stop_through_the_market()
    {
        var decision = AggregateEvaluator.TrailStopsToEntry(
        [
            new PositionRisk { Identifier = "winner", Side = Side.BUY, EntryPrice = 100, StopPrice = 95 },
            new PositionRisk { Identifier = "loser", Side = Side.BUY, EntryPrice = 100, StopPrice = 95 },
            new PositionRisk { Identifier = "already", Side = Side.SELL, EntryPrice = 100, StopPrice = 99 },
        ],
        lastPrices: new Dictionary<string, double> { ["winner"] = 110, ["loser"] = 98 });
        Assert.Equal(["winner"], decision.Moves.Select(m => m.Identifier));
        Assert.Equal(["loser"], decision.SkippedThroughPrice);
        Assert.Equal(["already"], decision.SkippedNotImproving);
    }

    [Fact]
    public void A_short_with_no_recorded_side_is_not_evaluated_as_short_by_accident()
    {
        // PORTED DEFECT: a leg with no recorded side was evaluated as a short, so its stop fired on a favourable move.
        var risk = PositionRisk.FromState(new Dictionary<string, object?> { ["entry_price"] = 100.0, ["current_sl"] = 95.0, ["quantity"] = 1.0 });
        Assert.Equal(Side.BUY, risk.Side);
        Assert.False(PositionEvaluator.Evaluate(risk, 110).Breached);
    }
}
