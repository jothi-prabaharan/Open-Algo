using EPTradeBot.Core.Portfolio;

namespace EPTradeBot.Core.Tests;

/// <summary>Hand-worked SIP cases that OpenAlgo's own recordings cannot reach: a model with no charges, where a purchase can spend the budget to the last paisa.</summary>
public sealed class SipEngineTests
{
    private static PriceSeries Flat(double price, int days)
    {
        var dates = Enumerable.Range(0, days).Select(i => new DateOnly(2024, 1, 1).AddDays(i)).Where(d => d.DayOfWeek is not (DayOfWeek.Saturday or DayOfWeek.Sunday)).ToArray();
        return new PriceSeries(dates, dates.Select(_ => price).ToArray());
    }

    [Fact]
    public void A_purchase_that_spends_the_budget_exactly_is_made()
    {
        // Two installments of 1,000 buy exactly ten shares each at 100 with no charges. A guard that treated "equal to the budget" as too much would buy nine and leave 100 idle each time.
        var result = SipEngine.Run(Flat(100, 40), "X", "NSE", new DateOnly(2024, 1, 1), new DateOnly(2024, 2, 9), 1000, new SipOptions("monthly", 1, 0, new FlatCosts(0, 0)));
        Assert.Equal(2, result.InstallmentCount);
        Assert.Equal(20, result.TotalUnits);
        Assert.Equal(0, result.Cash);
    }

    [Fact]
    public void Money_that_buys_no_whole_share_waits_for_the_next_installment()
    {
        var result = SipEngine.Run(Flat(300, 120), "X", "NSE", new DateOnly(2024, 1, 1), new DateOnly(2024, 4, 30), 1000, new SipOptions("monthly", 1, 0, new FlatCosts(0, 0)));
        Assert.Equal(4, result.InstallmentCount);
        Assert.Equal(13, result.TotalUnits);   // 1000 -> 3 shares (100 left), 1100 -> 3 (200), 1200 -> 4 (0), 1000 -> 3 (100)
        Assert.Equal(100, result.Cash);
    }

    [Fact]
    public void A_flat_basis_point_model_charges_traded_value_and_steps_down_a_share_to_fit_it()
    {
        // 10 bps plus 5 bps of slippage is 0.15% of the traded value. Ten shares (1,000) would cost 1.50 on top of the 1,000 and not fit, so nine are bought for a 1.35 charge and 98.65 carries
        // forward; the second installment then has 1,098.65 and ten shares and their 1.50 charge fit.
        var result = SipEngine.Run(Flat(100, 40), "X", "NSE", new DateOnly(2024, 1, 1), new DateOnly(2024, 2, 9), 1000, new SipOptions("monthly", 1, 0, new FlatCosts(10, 0.0005)));
        Assert.Equal(19, result.TotalUnits);
        Assert.Equal(2.85, result.Charges, 10);
        Assert.Equal(1098.65 - 1000 - 1.5, result.Cash, 8);
    }
}

public sealed class PriceSeriesBuilderTests
{
    private static readonly DateOnly D1 = new(2026, 1, 5);

    [Fact]
    public void A_session_read_twice_keeps_the_last_value_and_bars_without_a_close_are_dropped()
    {
        var series = EPTradeBot.Core.Portfolio.PriceSeriesBuilder.Build([(D1.AddDays(1), 11.0), (D1, 10.0), (D1, 12.0), (D1.AddDays(2), null), (D1.AddDays(3), double.NaN)], "X", "a", "b");
        Assert.Equal([D1, D1.AddDays(1)], series.Dates);
        Assert.Equal([12.0, 11.0], series.Closes);
    }

    [Fact]
    public void Too_few_sessions_non_positive_closes_and_no_bars_are_refused_with_the_reason()
    {
        var one = Assert.Throws<EPTradeBot.Core.Portfolio.PriceDataException>(() => EPTradeBot.Core.Portfolio.PriceSeriesBuilder.Build([(D1, 1.0)], "X", "2026-01-01", "2026-02-01"));
        Assert.Equal("only 1 session(s) are common to all 1 symbols between 2026-01-01 and 2026-02-01; a shorter window or a symbol with a later listing date is likely", one.Message);
        var negative = Assert.Throws<EPTradeBot.Core.Portfolio.PriceDataException>(() => EPTradeBot.Core.Portfolio.PriceSeriesBuilder.Build([(D1, 1.0), (D1.AddDays(1), 0.0)], "X", "a", "b"));
        Assert.Equal("price matrix must contain only positive finite closes", negative.Message);
        var none = Assert.Throws<EPTradeBot.Core.Portfolio.PriceDataException>(() => EPTradeBot.Core.Portfolio.PriceSeriesBuilder.Build(Array.Empty<(DateOnly, double?)>(), "X", "a", "b"));
        Assert.Equal("X: no rows survived timestamp/close parsing", none.Message);
    }

    [Fact]
    public void A_move_past_35_percent_is_reported_as_python_prints_it_and_a_smaller_one_is_not()
    {
        var series = EPTradeBot.Core.Portfolio.PriceSeriesBuilder.Build([(D1, 100.0), (D1.AddDays(1), 50.0), (D1.AddDays(2), 52.0), (D1.AddDays(3), 69.0)], "X", "a", "b");
        Assert.Equal(["('2026-01-06', -0.5)"], EPTradeBot.Core.Portfolio.PriceSeriesBuilder.SplitWarnings(series));
    }
}
