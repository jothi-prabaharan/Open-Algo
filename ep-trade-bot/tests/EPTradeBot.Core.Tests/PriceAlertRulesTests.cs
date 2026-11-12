using System.Text.Json.Nodes;
using EPTradeBot.Core.Flow;

namespace EPTradeBot.Core.Tests;

public sealed class PriceAlertRulesTests
{
    private static PriceAlert Alert(string condition, double target = 100, double? lower = null, double? upper = null, double pct = 0, bool every = false) =>
        new("SBIN", "NSE", condition, target, lower, upper, pct, every);

    [Theory]
    [InlineData("above", "greater_than")]
    [InlineData("price_above", "greater_than")]
    [InlineData("Below", "less_than")]
    [InlineData("crosses-above", "crossing_up")]
    [InlineData("cross_below", "crossing_down")]
    [InlineData("crosses", "crossing")]
    [InlineData("entering_channel", "entering_channel")]
    [InlineData(" Greater-Than ", "greater_than")]
    public void Both_vocabularies_are_accepted_and_named_one_way(string written, string canonical) => Assert.Equal(canonical, PriceAlertRules.Normalize(written));

    [Theory]
    [InlineData("greater_than", 100.01, null, true)]
    [InlineData("greater_than", 100, null, false)]
    [InlineData("less_than", 99.99, null, true)]
    [InlineData("less_than", 100, null, false)]
    [InlineData("crossing", 100.05, null, true)]   // within 0.1% of the level
    [InlineData("crossing", 100.2, null, false)]
    [InlineData("crossing_up", 101, 99.0, true)]
    [InlineData("crossing_up", 101, 100.0, true)]  // it was exactly on the level, and now is above
    [InlineData("crossing_up", 101, 100.5, false)] // already above before this price: not a cross
    [InlineData("crossing_up", 101, null, true)]   // no earlier price: above is enough
    [InlineData("crossing_down", 99, 101.0, true)]
    [InlineData("crossing_down", 99, 99.5, false)]
    [InlineData("crossing_down", 99, null, true)]
    [InlineData("moving_up", 101, 100.0, true)]
    [InlineData("moving_up", 101, null, false)]
    [InlineData("moving_down", 99, 100.0, true)]
    [InlineData("moving_down", 101, 100.0, false)]
    public void A_condition_holds_for_a_price_given_the_last_one(string condition, double price, double? last, bool expected) =>
        Assert.Equal(expected, PriceAlertRules.Holds(Alert(condition), price, last));

    [Theory]
    [InlineData("entering_channel", 95, true)]
    [InlineData("inside_channel", 90, true)]
    [InlineData("inside_channel", 110, true)]
    [InlineData("inside_channel", 111, false)]
    [InlineData("exiting_channel", 89.99, true)]
    [InlineData("outside_channel", 110.01, true)]
    [InlineData("outside_channel", 100, false)]
    public void A_channel_condition_looks_at_the_lower_and_upper_price(string condition, double price, bool expected) =>
        Assert.Equal(expected, PriceAlertRules.Holds(Alert(condition, lower: 90, upper: 110), price, null));

    [Theory]
    [InlineData("moving_up_percent", 102, 100.0, true)]
    [InlineData("moving_up_percent", 101.9, 100.0, false)]
    [InlineData("moving_down_percent", 98, 100.0, true)]
    [InlineData("moving_down_percent", 98.1, 100.0, false)]
    [InlineData("moving_up_percent", 102, null, false)]
    [InlineData("moving_up_percent", 102, 0.0, false)]
    public void A_percentage_move_is_measured_from_the_last_price(string condition, double price, double? last, bool expected) =>
        Assert.Equal(expected, PriceAlertRules.Holds(Alert(condition, pct: 2), price, last));

    [Fact]
    public void An_unknown_condition_never_holds() => Assert.False(PriceAlertRules.Holds(Alert("sideways"), 100, 99));

    [Fact]
    public void A_once_only_alert_fires_once_and_is_spent()
    {
        var alert = Alert("greater_than");
        var state = new PriceAlertState();
        Assert.False(PriceAlertRules.Check(alert, state, 99));
        Assert.True(PriceAlertRules.Check(alert, state, 101));
        Assert.False(PriceAlertRules.Check(alert, state, 102));
        Assert.False(PriceAlertRules.Check(alert, state, 90));
        Assert.False(PriceAlertRules.Check(alert, state, 103));
    }

    [Fact]
    public void An_every_time_alert_keeps_watching_and_a_crossing_needs_a_fresh_cross()
    {
        var alert = Alert("crossing_up", every: true);
        var state = new PriceAlertState();
        Assert.False(PriceAlertRules.Check(alert, state, 99));
        Assert.True(PriceAlertRules.Check(alert, state, 101));
        Assert.False(PriceAlertRules.Check(alert, state, 102)); // already above: no second firing from the same cross
        Assert.False(PriceAlertRules.Check(alert, state, 99));
        Assert.True(PriceAlertRules.Check(alert, state, 101));  // a fresh cross
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(-5.0)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void A_price_that_is_not_a_price_is_ignored_and_not_remembered(double price)
    {
        var state = new PriceAlertState { LastPrice = 99 };
        Assert.False(PriceAlertRules.Check(Alert("less_than", 1000), state, price));
        Assert.Equal(99, state.LastPrice);
    }

    private static (PriceAlert? Alert, string? Problem) Read(string json) => PriceAlertRules.FromNode(JsonNode.Parse(json)!.AsObject());

    [Fact]
    public void An_alert_is_read_from_its_trigger_node_with_the_defaults_of_the_editor()
    {
        var (alert, problem) = Read("""{"symbol":"sbin","condition":"price_above","price":"505.5","trigger":"every-time"}""");
        Assert.Null(problem);
        Assert.Equal(new PriceAlert("SBIN", "NSE", "greater_than", 505.5, null, null, 0, true), alert);
        Assert.Equal("greater_than", Read("""{"symbol":"X","price":1}""").Alert!.Condition);
    }

    [Theory]
    [InlineData("""{"condition":"greater_than","price":100}""", "no symbol")]
    [InlineData("""{"symbol":"X","condition":"sideways","price":100}""", "'sideways' is not one this platform knows")]
    [InlineData("""{"symbol":"X","condition":"crossing_up"}""", "needs a price above zero")]
    [InlineData("""{"symbol":"X","condition":"less_than","price":0}""", "needs a price above zero")]
    [InlineData("""{"symbol":"X","condition":"inside_channel","priceLower":90}""", "needs a lower and an upper price")]
    [InlineData("""{"symbol":"X","condition":"inside_channel","priceLower":110,"priceUpper":90}""", "inverted channel")]
    [InlineData("""{"symbol":"X","condition":"moving_up_percent"}""", "needs a percentage above zero")]
    public void An_alert_that_could_never_fire_is_refused_with_the_reason(string json, string reason)
    {
        var (alert, problem) = Read(json);
        Assert.Null(alert);
        Assert.Contains(reason, problem);
    }
}
