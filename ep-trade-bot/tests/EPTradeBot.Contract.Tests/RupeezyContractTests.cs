using EPTradeBot.IntegrationTests;
using System.Text.Json.Nodes;
using Xunit.Abstractions;
using static EPTradeBot.Contract.Tests.ContractRunner;

namespace EPTradeBot.Contract.Tests;

public sealed class RupeezyHost : BrokerContractHost
{
    public override string BrokerName => "rupeezy";
    public override string FixtureFile => "rupeezy.fixtures.json";
    public override bool NumbersMayBeText => true;
    public override IReadOnlyDictionary<string, string> Keys { get; } = new Dictionary<string, string> { ["ApiKey"] = "rupeezy-app-id", ["ApiSecret"] = "rupeezy-api-key" };
    public override IReadOnlyDictionary<(string, string), decimal> Prices { get; } = new Dictionary<(string, string), decimal>
    {
        [("NSE", "SBIN")] = 805m, [("NSE", "INFY")] = 1500m, [("NFO", "NIFTY25NOV26FUT")] = 24000m, [("MCX", "GOLDM25NOV26FUT")] = 75000m,
    };
    public override IReadOnlyDictionary<string, string> HarnessReplies { get; } = new Dictionary<string, string>
    {
        ["GET /v2/trading/portfolio/positions"] = """{"status":"success","data":{"net":[],"day":[]}}""",
    };
}

/// <summary>Rupeezy against the Python implementation (tools/contract-fixtures/rupeezy.fixtures.json).</summary>
public sealed class RupeezyContractTests(RupeezyHost host, ITestOutputHelper output) : IClassFixture<RupeezyHost>
{
    public static readonly Dictionary<string, Deviation> Deviations = new()
    {
        ["placeorder_unknown_symbol"] = new("Same status; the platform's own words for a symbol missing from the master contract, where OpenAlgo's plugin words it itself.",
            f => Expect(f, 400, Error("NOPE on NSE is not in the Rupeezy master contract. Check the symbol, or download the master contract again."))),
        ["funds_refused"] = new("OpenAlgo shows an empty balance when Rupeezy refuses the funds call. EP Trade bot reports the refusal, so an empty balance is never mistaken for a real one.", Status: 500),
        ["placeorder_success_without_an_order_id"] = new("OpenAlgo answers success with no order id when Rupeezy says success without one. EP Trade bot refuses, so a trader is never told an order was placed that cannot be found.",
            f => Expect(f, 400, Error("Failed to place order"))),
        ["placeorder_reply_is_not_json"] = new("Same status; the words name Rupeezy and say to retry.",
            f => Expect(f, 500, Error("Rupeezy returned an unexpected response. Please retry."))),
        ["orderbook_refused"] = new("Same status; EP Trade bot says which call Rupeezy refused.", f => f["expected"]!["body"]!["message"] = "Rupeezy could not send the order book: Invalid token"),
    };

    public static IEnumerable<object[]> Names() => ContractRunner.Names("rupeezy.fixtures.json");

    [PostgresTheory]
    [MemberData(nameof(Names))]
    public Task Matches_the_python_implementation(string name) => RunAsync(host, name, Deviations, output);
}
