using EPTradeBot.IntegrationTests;
using Xunit.Abstractions;
using static EPTradeBot.Contract.Tests.ContractRunner;

namespace EPTradeBot.Contract.Tests;

public sealed class ArrowHost : BrokerContractHost
{
    public override string BrokerName => "arrow";
    public override string FixtureFile => "arrow.fixtures.json";
    public override bool NumbersMayBeText => true;
    public override IReadOnlyDictionary<string, string> Keys { get; } = new Dictionary<string, string> { ["ApiKey"] = "arrow-app-id", ["ApiSecret"] = "arrow-api-secret" };
    public override IReadOnlyDictionary<(string, string), decimal> Prices { get; } = new Dictionary<(string, string), decimal>
    {
        [("NSE", "SBIN")] = 805m, [("NSE", "INFY")] = 1500m, [("NFO", "NIFTY25NOV26FUT")] = 24000m, [("MCX", "GOLDM25NOV26FUT")] = 75000m,
    };
    public override IReadOnlyDictionary<string, string> HarnessReplies { get; } = new Dictionary<string, string>
    {
        ["GET /user/positions"] = """{"status":"success","data":[]}""",
    };
}

/// <summary>Arrow against the Python implementation (tools/contract-fixtures/arrow.fixtures.json).</summary>
public sealed class ArrowContractTests(ArrowHost host, ITestOutputHelper output) : IClassFixture<ArrowHost>
{
    private const string Refusal = "Same status; EP Trade bot words the refusal for the trader and names which call Arrow refused, where OpenAlgo shows the HTTP library's own sentence with the address in it.";

    private static Deviation Words(string message, int status = 500) => new(Refusal, f => Expect(f, status, Error(message)));

    public static readonly Dictionary<string, Deviation> Deviations = new()
    {
        ["orderbook_refused"] = Words("Arrow could not send the order book: bad token"),
        ["tradebook_refused"] = Words("Arrow could not send the trade book: bad token"),
        ["positionbook_refused"] = Words("Arrow could not send the positions: bad token"),
        ["holdings_refused"] = Words("Arrow could not send the holdings: bad token"),
        ["cancelorder_refused_without_a_message"] = Words("Failed to cancel order: Arrow did not accept the cancel."),
        ["placeorder_unknown_symbol"] = new("Same status; the platform's own words for a symbol missing from the master contract. OpenAlgo sends the order with no symbol.",
            f =>
            {
                Expect(f, 400, Error("NOPE on NSE is not in the Arrow master contract. Check the symbol, or download the master contract again."));
                f["sent"]!.AsArray().Clear();
            }),
        ["placeorder_refused_with_an_ok_status"] = new("OpenAlgo answers success with no order id when Arrow refuses an order but answers with an OK status. EP Trade bot refuses, so a trader is never told an order was placed that does not exist.",
            f => Expect(f, 400, Error("Rejected"))),
        ["placeorder_success_without_an_order_number"] = new("OpenAlgo answers success with no order id when Arrow says success without an order number. EP Trade bot refuses, so a trader is never told an order was placed that cannot be found.",
            f => Expect(f, 400, Error("Failed to place order"))),
        ["funds_refused"] = new("OpenAlgo shows an empty balance when Arrow refuses the funds call. EP Trade bot reports the refusal, so an empty balance is never mistaken for a real one.", Status: 500),
        ["funds_error_status_in_an_ok_answer"] = new("OpenAlgo shows an empty balance when Arrow answers the funds call with an error status in the body. EP Trade bot reports the refusal.", Status: 500),
    };

    public static IEnumerable<object[]> Names() => ContractRunner.Names("arrow.fixtures.json");

    [PostgresTheory]
    [MemberData(nameof(Names))]
    public Task Matches_the_python_implementation(string name) => RunAsync(host, name, Deviations, output);
}
