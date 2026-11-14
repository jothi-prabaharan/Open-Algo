using EPTradeBot.IntegrationTests;
using Xunit.Abstractions;
using static EPTradeBot.Contract.Tests.ContractRunner;

namespace EPTradeBot.Contract.Tests;

public sealed class HdfcSecuritiesHost : BrokerContractHost
{
    public override string BrokerName => "hdfcsecurities";
    public override string FixtureFile => "hdfcsecurities.fixtures.json";
    public override bool NumbersMayBeText => true;
    public override IReadOnlyDictionary<string, string> Keys { get; } = new Dictionary<string, string> { ["ApiKey"] = "hdfc-api-key", ["ApiSecret"] = "hdfc-api-secret" };
    public override IReadOnlyDictionary<(string, string), decimal> Prices { get; } = new Dictionary<(string, string), decimal>
    {
        [("NSE", "SBIN")] = 805m, [("NSE", "INFY")] = 1500m, [("NFO", "NIFTY25NOV26FUT")] = 24000m, [("NFO", "NIFTY25NOV2624000CE")] = 100m, [("NFO", "NIFTY25NOV2624000PE")] = 100m,
        [("NFO", "RELIANCE25NOV26FUT")] = 2900m, [("BFO", "SENSEX26NOVFUT")] = 80000m, [("MCX", "GOLDM25NOV26FUT")] = 75000m, [("CDS", "USDINR25NOV26FUT")] = 83.5m,
    };
    public override IReadOnlyDictionary<string, string> HarnessReplies { get; } = new Dictionary<string, string>
    {
        ["GET /oapi/v1/portfolio/cumulative-positions"] = """{"status":"success","data":{"net":[]}}""",
    };
    public override IReadOnlyDictionary<string, string> VolatileFields { get; } = new Dictionary<string, string> { ["json.external_reference_number"] = @"^\d{13}$" };
    public override IReadOnlyDictionary<string, string> Settings { get; } = new Dictionary<string, string> { ["Brokers:RetryBackoffMs:hdfcsecurities"] = "0" };
}

/// <summary>HDFC Securities against the Python implementation (tools/contract-fixtures/hdfcsecurities.fixtures.json).</summary>
public sealed class HdfcSecuritiesContractTests(HdfcSecuritiesHost host, ITestOutputHelper output) : IClassFixture<HdfcSecuritiesHost>
{
    private const string Empty = "OpenAlgo shows an empty book (or an empty balance) when HDFC Securities answers with an expired-session or error body, so a trader reads a dead session as no orders and no positions, and close-all reports nothing open. EP Trade bot reports the refusal.";

    private static Deviation Refused(string what, string why) => new(Empty, f => Expect(f, 500, Error($"HDFC Securities could not send the {what}: {why}")));

    public static readonly Dictionary<string, Deviation> Deviations = new()
    {
        ["orderbook_expired_session"] = Refused("order book", "token expired"),
        ["tradebook_expired_session"] = Refused("trade book", "token expired"),
        ["positionbook_expired_session"] = Refused("positions", "token expired"),
        ["holdings_expired_session"] = Refused("holdings", "token expired"),
        ["funds_expired_session"] = Refused("funds", "token expired"),
        ["funds_error_status"] = Refused("funds", "x"),
        ["funds_without_equity"] = new("OpenAlgo shows an empty balance when the funds answer has no equity block. EP Trade bot says it could not read the funds.", f => Expect(f, 502, Error("HDFC Securities sent funds EP Trade bot could not read. Try again in a moment."))),
        ["cancelallorder_book_error"] = new("OpenAlgo cancels nothing and reports success when the order book cannot be read. EP Trade bot reports the failure.", f => Expect(f, 500, Error("Failed to cancel all orders due to internal error"))),
        ["placeorder_success_without_an_order_id"] = new("OpenAlgo answers success with no order id when HDFC Securities says success without one. EP Trade bot refuses, so a trader is never told an order was placed that cannot be found.", f => Expect(f, 400, Error("Failed to place order"))),
        ["placeorder_success_without_data"] = new("OpenAlgo answers success with no order id when HDFC Securities says success without one. EP Trade bot refuses, so a trader is never told an order was placed that cannot be found.", f => Expect(f, 400, Error("Failed to place order"))),
        ["placeorder_unknown_symbol"] = new("OpenAlgo answers 500 with an internal error. The platform names the symbol and says what to do.", f => { Expect(f, 400, Error("NOPE on NSE is not in the HDFC Securities master contract. Check the symbol, or download the master contract again.")); f["sent"]!.AsArray().Clear(); }),
        ["closeposition_an_order_is_refused"] = new("Same status; the shared close-all path names the positions its own way.", f => Expect(f, 500, Error("2 position(s) could not be squared off and are still open: SBIN (NSE): Insufficient funds; NIFTY25NOV26FUT (NFO): Insufficient funds"))),
    };

    public static IEnumerable<object[]> Names() => ContractRunner.Names("hdfcsecurities.fixtures.json");

    [PostgresTheory]
    [MemberData(nameof(Names))]
    public Task Matches_the_python_implementation(string name) => RunAsync(host, name, Deviations, output);
}
