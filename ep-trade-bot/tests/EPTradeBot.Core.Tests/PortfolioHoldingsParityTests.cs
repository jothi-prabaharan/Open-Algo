using System.Text.Json.Nodes;
using EPTradeBot.Core.Portfolio;

namespace EPTradeBot.Core.Tests;

/// <summary>Reading a broker's holdings against OpenAlgo's (portfolio_holdings.fixtures.json, from record_portfolio_holdings.py).</summary>
public sealed class PortfolioHoldingsParityTests
{
    private static readonly JsonArray Cases = JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "portfolio_holdings.fixtures.json")))!.AsArray();

    public static IEnumerable<object[]> Names() => Cases.Select(c => new object[] { c!["name"]!.GetValue<string>() });

    [Theory]
    [MemberData(nameof(Names))]
    public void Holdings_are_read_and_summarised_as_OpenAlgo_does(string name)
    {
        var fixture = Cases.Single(c => c!["name"]!.GetValue<string>() == name)!.AsObject();
        var rows = fixture["rows"]!.AsArray().Select(r => r!.AsObject());
        var summary = PortfolioHoldings.Summarise(PortfolioHoldings.Parse(rows));
        ToolsParityTests.AssertSame(fixture["summary"], PortfolioBacktestService.CleanTree(summary), 1e-9);
    }
}
