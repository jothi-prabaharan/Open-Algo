using System.Text.Json.Nodes;
using EPTradeBot.Core.Brokers;

namespace EPTradeBot.Core.Tests.Brokers;

public class BookFormattingTests
{
    // Values checked against CPython 3.13: round(x, 2) and f"{x:.2f}".
    [Theory]
    [InlineData(2.675, 2.67)]
    [InlineData(1.005, 1.0)]
    [InlineData(0.125, 0.12)]
    [InlineData(0.375, 0.38)]
    [InlineData(43.3333, 43.33)]
    [InlineData(-2.675, -2.67)]
    [InlineData(805.456, 805.46)]
    public void Round2_matches_python_round(double value, double expected) =>
        Assert.Equal(expected, BookFormatting.Round2(value));

    [Theory]
    [InlineData(801.1234, "801.12")]
    [InlineData(24005.5, "24005.50")]
    [InlineData(1942.516, "1942.52")]
    [InlineData(2.675, "2.67")]
    [InlineData(0.0, "0.00")]
    public void Fixed2_matches_python_format(double value, string expected) =>
        Assert.Equal(expected, BookFormatting.Fixed2(value));

    [Fact]
    public void Orders_keep_whole_quantities_as_integers_and_zero_the_price_of_market_orders()
    {
        var rows = (JsonArray)JsonNode.Parse("""[{"quantity": 10.0, "price": 1499.954, "pricetype": "MARKET", "trigger_price": 0},{"quantity": 0.5, "price": 2.675, "pricetype": "LIMIT"}]""")!;
        var formatted = BookFormatting.Orders(rows);
        Assert.Equal("""[{"quantity":10,"price":0,"pricetype":"MARKET","trigger_price":0},{"quantity":0.5,"price":2.67,"pricetype":"LIMIT"}]""", formatted.ToJsonString());
    }
}
