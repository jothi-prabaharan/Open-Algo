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
