using EPTradeBot.Core.Entities.Audit;
using EPTradeBot.Core.Entities.Core;
using EPTradeBot.Core.Trading;

namespace EPTradeBot.Core.Tests.Trading;

public sealed class AccountRiskRulesTests
{
    private static readonly BrokerAccount Paper = new() { DisplayName = "Paper", Broker = "sandbox", AccountType = AccountType.PAPER };
    private static readonly BrokerAccount Live = new() { DisplayName = "Live", Broker = "zerodha", AccountType = AccountType.LIVE };

    private static readonly AccountRiskSetting Limits = new() { MaxDailyLoss = 10_000m, MaxOrderQuantity = 100, MaxPositionValue = 100_000m };

    private static readonly AccountRiskSetting WideOrders = new() { MaxDailyLoss = 10_000m, MaxOrderQuantity = 1000, MaxPositionValue = 100_000m };

    private static OrderIntent Buy(int qty, OrderSource source = OrderSource.Manual) => new()
    {
        AccountId = Guid.Empty, Source = source, Symbol = "SBIN", Exchange = "NSE", Action = "BUY", Quantity = qty,
    };

    private static OrderIntent Sell(int qty, OrderSource source = OrderSource.Manual) => Buy(qty, source) with { Action = "SELL" };

    private static AccountExposure Flat(decimal mark = 500m) => new() { MarkPrice = mark };

    [Fact]
    public void Max_order_quantity_allows_the_limit_and_refuses_one_more()
    {
        Assert.Null(AccountRiskRules.Check(Paper, Limits, Flat(), Buy(100)));
        Assert.Equal("above_max_order_quantity", AccountRiskRules.Check(Paper, Limits, Flat(), Buy(101))?.Code);
    }

    [Fact]
    public void Max_daily_loss_blocks_new_exposure_at_the_limit_but_not_just_before()
    {
        Assert.Null(AccountRiskRules.Check(Paper, Limits, Flat() with { DayPnl = -9_999.99m }, Buy(1)));
        Assert.Equal("max_daily_loss_reached", AccountRiskRules.Check(Paper, Limits, Flat() with { DayPnl = -10_000m }, Buy(1))?.Code);
    }

    [Fact]
    public void Max_position_value_is_the_projected_gross_after_the_order()
    {
        // 100,000 limit, 500 price: 200 shares fills it exactly; 50 already held.
        var held = Flat() with { GrossOpenValue = 25_000m, NetQuantityInInstrument = 50 };
        Assert.Null(AccountRiskRules.Check(Paper, WideOrders, held, Buy(150)));
        Assert.Equal("above_max_position_value", AccountRiskRules.Check(Paper, WideOrders, held, Buy(151))?.Code);
    }

    [Fact]
    public void An_exit_is_never_refused_by_a_loss_or_exposure_limit()
    {
        var losing = Flat() with { DayPnl = -50_000m, GrossOpenValue = 500_000m, NetQuantityInInstrument = 1000 };
        Assert.Null(AccountRiskRules.Check(Paper, Limits, losing, Sell(1000)));
        Assert.Null(AccountRiskRules.Check(Paper, Limits, losing, Sell(400)));
    }

    [Fact]
    public void A_sell_larger_than_the_long_is_new_exposure_and_is_checked()
    {
        var losing = Flat() with { DayPnl = -50_000m, NetQuantityInInstrument = 10 };
        Assert.Equal("max_daily_loss_reached", AccountRiskRules.Check(Paper, Limits, losing, Sell(11))?.Code);
    }

    [Fact]
    public void Kill_switch_blocks_everything_except_its_own_square_off()
    {
        var killed = new BrokerAccount { DisplayName = "Paper", Broker = "sandbox", AccountType = AccountType.PAPER, KillSwitchEngaged = true };
        var holding = Flat() with { NetQuantityInInstrument = 10 };
        Assert.Equal("kill_switch_engaged", AccountRiskRules.Check(killed, Limits, holding, Sell(10))?.Code);
        Assert.Null(AccountRiskRules.Check(killed, Limits, holding, Sell(10, OrderSource.KillSwitch)));
    }

    [Fact]
    public void Freeze_limit_applies_and_zero_means_unknown_not_one()
    {
        Assert.Equal("above_freeze_quantity", AccountRiskRules.Check(Paper, null, Flat() with { FreezeQuantity = 1800 }, Buy(1801))?.Code);
        Assert.Null(AccountRiskRules.Check(Paper, null, Flat() with { FreezeQuantity = 0 }, Buy(5000)));
    }

    [Fact]
    public void Live_accounts_need_limits_before_they_trade()
    {
        Assert.Equal("risk_limits_missing", AccountRiskRules.Check(Live, null, Flat(), Buy(1))?.Code);
        Assert.Null(AccountRiskRules.Check(Paper, null, Flat(), Buy(1)));
    }

    [Fact]
    public void Without_a_price_an_opening_order_fails_closed()
    {
        Assert.Equal("price_unavailable", AccountRiskRules.Check(Paper, Limits, new AccountExposure(), Buy(1))?.Code);
    }
}
