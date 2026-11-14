using System.Text.Json.Nodes;
using EPTradeBot.IntegrationTests;
using Xunit.Abstractions;
using static EPTradeBot.Contract.Tests.ContractRunner;

namespace EPTradeBot.Contract.Tests;

public sealed class IiflCapitalHost : BrokerContractHost
{
    public override string BrokerName => "iiflcapital";
    public override string FixtureFile => "iiflcapital.fixtures.json";
    public override bool NumbersMayBeText => true;
    public override IReadOnlyDictionary<string, string> Keys { get; } = new Dictionary<string, string> { ["ApiKey"] = "iifl-app-key", ["ApiSecret"] = "iifl-app-secret" };
    public override IReadOnlyDictionary<(string, string), decimal> Prices { get; } = new Dictionary<(string, string), decimal>
    {
        [("NSE", "SBIN")] = 805m, [("NSE", "INFY")] = 1500m, [("BSE", "TATAMOTORS")] = 500m, [("NFO", "NIFTY25NOV26FUT")] = 24000m, [("NFO", "NIFTY25NOV2624000CE")] = 100m,
        [("BFO", "SENSEX26NOVFUT")] = 80000m, [("MCX", "GOLDM25NOV26FUT")] = 75000m, [("CDS", "USDINR25NOV26FUT")] = 83.5m,
    };
    public override IReadOnlyDictionary<string, string> HarnessReplies { get; } = new Dictionary<string, string> { ["GET /v1/positions"] = """{"status":"Ok","result":[]}""" };
    public override IReadOnlyDictionary<string, string> Settings { get; } = new Dictionary<string, string> { ["Brokers:RetryBackoffMs:iiflcapital"] = "0" };
}

/// <summary>IIFL Capital against the Python implementation (tools/contract-fixtures/iiflcapital.fixtures.json).</summary>
public sealed class IiflCapitalContractTests(IiflCapitalHost host, ITestOutputHelper output) : IClassFixture<IiflCapitalHost>
{
    private const string Same = "OpenAlgo shows an empty balance when none of IIFL Capital's three limit calls answers, so a dead session reads as no money. EP Trade bot reports that the funds did not arrive.";

    private static void AddSent(JsonObject f, string method, string path, string before)
    {
        var sent = f["sent"]!.AsArray();
        var copy = (JsonObject)sent.First(x => x!["method"]!.GetValue<string>() == method)!.DeepClone();
        copy["path"] = path;
        var at = sent.ToList().FindIndex(x => x!["path"]!.GetValue<string>() == before);
        sent.Insert(at, copy);
    }

    private static void SquareOffTag(JsonObject f)
    {
        foreach (var write in f["sent"]!.AsArray().Where(x => x!["method"]!.GetValue<string>() == "POST")) write!["json"]![0]!["orderTag"] = "Squareoff";
    }

    public static readonly Dictionary<string, Deviation> Deviations = new()
    {
        ["cancelallorder"] = new("OpenAlgo cancels only orders whose raw status is one of six it lists. EP Trade bot's shared cancel-all cancels every order the book shows as open, which includes a status OpenAlgo does not list (here a row with an unknown status), so one more cancel is tried.",
            f => { AddSent(f, "DELETE", "/v1/orders/I8", "/v1/orders/I9"); var body = f["expected"]!["body"]!.AsObject(); body["failed_cancellations"] = new JsonArray("I8"); body["message"] = "Canceled 5 orders. Failed to cancel 1 orders."; }),
        ["cancelallorder_book_error"] = new("OpenAlgo cancels nothing and reports success when the order book cannot be read. EP Trade bot reports the failure.", f => Expect(f, 500, Error("Failed to cancel all orders due to internal error"))),
        ["modifyorder_refused_with_an_ok_status"] = new("OpenAlgo reports a modify as done when IIFL Capital refuses it with an ok status. EP Trade bot reports the refusal, so a trader is never told an order was changed that was not.", f => Expect(f, 400, Error("Rejected"))),
        ["holdings_expired_session"] = new("Same status; EP Trade bot says which call failed.", f => Expect(f, 500, Error("IIFL Capital could not send the holdings: Invalid session"))),
        ["positionbook_expired_session"] = new("Same status; EP Trade bot says which call failed.", f => Expect(f, 500, Error("IIFL Capital could not send the positions: Invalid session"))),
        ["funds_nothing_answers"] = new(Same, f => Expect(f, 502, Error("IIFL Capital did not send your funds. Check that you are signed in to IIFL Capital, then try again."))),
        ["placeorder_a_rate_limited_write_is_sent_again"] = new("OpenAlgo sends an order again when IIFL Capital answers it with a rate-limit reply, which can place it twice because that reply can arrive after the order was accepted. EP Trade bot never sends a write again and tells the trader to check the order book.",
            f => { Expect(f, 502, Error("IIFL Capital did not confirm this order and asked for it to be tried again later. EP Trade bot did not send it again, because the first one may already have reached IIFL Capital. Check the order book before sending it again.")); f["sent"]!.AsArray().RemoveAt(1); }),
        ["closeposition"] = new("The close-all orders carry the shared tag Squareoff where OpenAlgo's carry close_all_positions.", SquareOffTag),
        ["placeorder_unknown_symbol"] = new("OpenAlgo says the symbol token was not found. The platform names the symbol and says what to do.", f => Expect(f, 400, Error("NOPE on NSE is not in the IIFL Capital master contract. Check the symbol, or download the master contract again."))),
        ["closeposition_an_order_is_refused"] = new("OpenAlgo answers 207 with a count. EP Trade bot names the positions that are still open.", f => { SquareOffTag(f); Expect(f, 500, Error("4 position(s) could not be squared off and are still open: SBIN (NSE): Insufficient funds; NIFTY25NOV26FUT (NFO): Insufficient funds; GOLDM25NOV26FUT (MCX): Insufficient funds; USDINR25NOV26FUT (CDS): Insufficient funds")); }),
        ["placesmartorder_already_matched"] = new("OpenAlgo answers an already-matched smart order with a 500 whose message reads as a failure. EP Trade bot answers success and says the position already matches.", f => Expect(f, 200, new JsonObject { ["status"] = "success", ["message"] = "Positions Already Matched. No Action needed." })),
    };

    public static IEnumerable<object[]> Names() => ContractRunner.Names("iiflcapital.fixtures.json");

    [PostgresTheory]
    [MemberData(nameof(Names))]
    public Task Matches_the_python_implementation(string name) => RunAsync(host, name, Deviations, output);
}
