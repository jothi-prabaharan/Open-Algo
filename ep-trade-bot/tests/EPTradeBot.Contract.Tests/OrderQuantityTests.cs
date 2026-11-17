using EPTradeBot.Core.Brokers;

namespace EPTradeBot.Contract.Tests;

public class OrderQuantityTests
{
    [Theory]
    [InlineData("1", 1)]
    [InlineData("25", 25)]
    [InlineData("-3", -3)]
    [InlineData("0", 0)]
    [InlineData("7.0", 7)]
    [InlineData("2147483647", int.MaxValue)]
    public void A_whole_quantity_passes_through_unchanged(string quantity, int expected) =>
        Assert.Equal(expected, OrderQuantity.Whole(decimal.Parse(quantity, System.Globalization.CultureInfo.InvariantCulture)));

    [Theory]
    [InlineData("0.5")]
    [InlineData("1.0001")]
    [InlineData("-2.5")]
    public void A_fractional_quantity_is_refused_with_a_sentence_a_trader_can_read(string quantity)
    {
        var failure = Assert.Throws<BrokerException>(() => OrderQuantity.Whole(decimal.Parse(quantity, System.Globalization.CultureInfo.InvariantCulture)));
        Assert.Equal(400, failure.HttpStatus);
        Assert.Equal($"This broker takes whole quantities only, and {quantity} is not one. Use a whole quantity.", failure.Message);
    }

    [Theory]
    [InlineData("2147483648")]
    [InlineData("-2147483649")]
    public void A_whole_quantity_too_large_for_an_order_is_refused_not_wrapped(string quantity)
    {
        var failure = Assert.Throws<BrokerException>(() => OrderQuantity.Whole(decimal.Parse(quantity, System.Globalization.CultureInfo.InvariantCulture)));
        Assert.Equal(400, failure.HttpStatus);
    }
}
