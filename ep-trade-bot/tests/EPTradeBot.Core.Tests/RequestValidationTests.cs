using EPTradeBot.Core.Contracts.Management;
using EPTradeBot.Core.Contracts.V1;
using EPTradeBot.Core.Validation;

namespace EPTradeBot.Core.Tests;

/// <summary>The /api/v1 DTOs refuse exactly what OpenAlgo's schemas refused.</summary>
public sealed class RequestValidationTests
{
    private static PlaceOrderRequest ValidOrder() => new()
    {
        ApiKey = "key", Strategy = "s1", Exchange = "NSE", Symbol = "SBIN", Action = "BUY", Quantity = 1,
    };

    private static IReadOnlyCollection<string> Errors(object o) =>
        AnnotationValidator.Validate(o).SelectMany(r => r.MemberNames).ToList();

    [Fact]
    public void A_valid_order_passes_with_openalgo_defaults()
    {
        var order = ValidOrder();
        Assert.Empty(Errors(order));
        Assert.Equal("MARKET", order.PriceType);
        Assert.Equal("MIS", order.Product);
    }

    [Theory]
    [InlineData("Exchange", "NASDAQ", "exchange")]
    [InlineData("Action", "HOLD", "action")]
    [InlineData("PriceType", "STOP", "pricetype")]
    [InlineData("Product", "BO", "product")]
    [InlineData("ApiKey", "", "apikey")]
    [InlineData("Strategy", "", "strategy")]
    public void An_order_with_a_value_openalgo_refused_is_refused(string property, string value, string field)
    {
        var order = ValidOrder();
        typeof(PlaceOrderRequest).GetProperty(property)!.SetValue(order, value);
        Assert.Contains(field, Errors(order));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Quantity_must_be_positive(int quantity)
    {
        var order = ValidOrder();
        order.Quantity = quantity;
        Assert.Contains("quantity", Errors(order));
    }

    [Fact]
    public void Fractional_quantity_is_refused_outside_crypto_and_allowed_on_crypto()
    {
        var nse = ValidOrder();
        nse.Quantity = 1.5m;
        Assert.Contains("quantity", Errors(nse));

        var crypto = ValidOrder();
        crypto.Exchange = "CRYPTO";
        crypto.Symbol = "BTCUSDT";
        crypto.Quantity = 0.001m;
        Assert.Empty(Errors(crypto));
    }

    [Fact]
    public void Basket_items_are_validated_and_reported_by_position()
    {
        var basket = new BasketOrderRequest
        {
            ApiKey = "key", Strategy = "s1",
            Orders =
            [
                new BasketOrderItem { Exchange = "NSE", Symbol = "SBIN", Action = "BUY", Quantity = 1 },
                new BasketOrderItem { Exchange = "NSE", Symbol = "INFY", Action = "BUY", Quantity = 0 },
            ],
        };
        Assert.Contains("orders[1].quantity", Errors(basket));
        Assert.DoesNotContain("orders[0].quantity", Errors(basket));
    }

    [Fact]
    public void An_empty_basket_is_refused()
    {
        Assert.Contains("orders", Errors(new BasketOrderRequest { ApiKey = "k", Strategy = "s" }));
    }

    [Fact]
    public void Options_multi_order_accepts_one_to_twenty_legs()
    {
        var leg = new OptionsMultiOrderLeg { Offset = "ATM", OptionType = "CE", Action = "BUY", Quantity = 75 };
        var request = new OptionsMultiOrderRequest { ApiKey = "k", Strategy = "s", Underlying = "NIFTY", Exchange = "NSE_INDEX", Legs = [leg] };
        Assert.Empty(Errors(request));

        request.Legs = Enumerable.Range(0, 21).Select(_ => leg).ToList();
        Assert.Contains("legs", Errors(request));
    }

    [Theory]
    [InlineData("ATM", true)]
    [InlineData("itm1", true)]
    [InlineData("OTM50", true)]
    [InlineData("OTM51", false)]
    [InlineData("ITM0", false)]
    [InlineData("NEAR", false)]
    public void Option_offset_follows_openalgo_rule(string offset, bool valid)
    {
        var request = new OptionSymbolRequest { ApiKey = "k", Underlying = "NIFTY", Exchange = "NSE_INDEX", Offset = offset, OptionType = "CE" };
        Assert.Equal(valid, !Errors(request).Contains("offset"));
    }

    [Fact]
    public void Oco_gtt_needs_all_four_prices_with_stoploss_below_target()
    {
        var gtt = new PlaceGttOrderRequest
        {
            ApiKey = "k", Strategy = "s", TriggerType = "OCO", Exchange = "NSE", Symbol = "SBIN", Action = "SELL",
            Product = "CNC", Quantity = 1, Price = 0, TriggerPriceSl = 700, StopLoss = 699, TriggerPriceTg = 650, Target = 651,
        };
        Assert.Contains("triggerprice_sl", Errors(gtt));

        gtt.TriggerPriceTg = 800;
        gtt.Target = 801;
        Assert.Empty(Errors(gtt));
    }

    [Fact]
    public void Gtt_refuses_intraday_product()
    {
        var gtt = new PlaceGttOrderRequest
        {
            ApiKey = "k", Strategy = "s", TriggerType = "SINGLE", Exchange = "NSE", Symbol = "SBIN", Action = "BUY",
            Product = "MIS", Quantity = 1, Price = 100, TriggerPriceSl = 99,
        };
        Assert.Contains("product", Errors(gtt));
    }

    [Fact]
    public void Strategy_start_without_a_mode_is_refused_never_defaulted()
    {
        Assert.Contains("mode", Errors(new StrategyStartRequest { ApiKey = "k", StrategyId = 1 }));
        Assert.Contains("mode", Errors(new StrategyStartRequest { ApiKey = "k", StrategyId = 1, Mode = "paper" }));
        Assert.Empty(Errors(new StrategyStartRequest { ApiKey = "k", StrategyId = 1, Mode = "sandbox" }));
    }

    [Fact]
    public void Strategy_event_limit_is_bounded_not_clamped()
    {
        Assert.Contains("limit", Errors(new StrategyEventsRequest { ApiKey = "k", StrategyId = 1, Limit = -1 }));
        Assert.Contains("limit", Errors(new StrategyEventsRequest { ApiKey = "k", StrategyId = 1, Limit = 1001 }));
    }

    [Fact]
    public void History_dates_must_be_real_dates()
    {
        var h = new HistoryRequest { ApiKey = "k", Symbol = "SBIN", Exchange = "NSE", Interval = "D", StartDate = "2026-02-30", EndDate = "2026-03-01" };
        Assert.Contains("start_date", Errors(h));
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData("LIVE", true)]
    [InlineData("PAPER", true)]
    [InlineData("live", false)]
    [InlineData("SANDBOX", false)]
    public void Broker_account_type_must_be_given_as_LIVE_or_PAPER(string? type, bool valid)
    {
        var request = new CreateBrokerAccountRequest { DisplayName = "Main", Broker = "zerodha", AccountType = type };
        Assert.Equal(valid, !Errors(request).Contains("account_type"));
    }

    [Fact]
    public void Risk_settings_are_required_and_ranged()
    {
        Assert.Equal(3, AnnotationValidator.Validate(new UpdateAccountRiskSettingsRequest()).Count);
        var bad = new UpdateAccountRiskSettingsRequest { MaxDailyLoss = -1, MaxOrderQuantity = 0, MaxPositionValue = -5 };
        Assert.Equal(3, AnnotationValidator.Validate(bad).Count);
        var ok = new UpdateAccountRiskSettingsRequest { MaxDailyLoss = 25_000, MaxOrderQuantity = 1800, MaxPositionValue = 5_000_000 };
        Assert.Empty(AnnotationValidator.Validate(ok));
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData("kill all", false)]
    [InlineData("KILL ALL", true)]
    public void Global_kill_switch_needs_the_typed_confirmation(string? phrase, bool valid)
    {
        var request = new GlobalKillSwitchRequest { Confirmation = phrase };
        Assert.Equal(valid, AnnotationValidator.Validate(request).Count == 0);
    }
}
