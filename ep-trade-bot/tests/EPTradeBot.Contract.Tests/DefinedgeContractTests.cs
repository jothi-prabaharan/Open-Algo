using System.Text.Json.Nodes;
using EPTradeBot.IntegrationTests;
using Xunit.Abstractions;
using static EPTradeBot.Contract.Tests.ContractRunner;

namespace EPTradeBot.Contract.Tests;

public abstract class DefinedgeFileHost(string file) : BrokerContractHost
{
    public override string BrokerName => "definedge";
    public override string FixtureFile => file;
    public override bool NumbersMayBeText => true;
    public override IReadOnlyDictionary<string, string> Keys { get; } = new Dictionary<string, string> { ["ApiKey"] = "dg-api-token", ["ApiSecret"] = "dg-secret" };
    public override IReadOnlyDictionary<(string, string), decimal> Prices { get; } = new Dictionary<(string, string), decimal>
    {
        [("NSE", "SBIN")] = 805m, [("NSE", "INFY")] = 1500m, [("BSE", "TATAMOTORS")] = 500m, [("NFO", "NIFTY25NOV26FUT")] = 24000m, [("BFO", "SENSEX26NOVFUT")] = 80000m, [("MCX", "GOLDM25NOV26FUT")] = 75000m, [("CDS", "USDINR25NOV26FUT")] = 83.5m,
    };
    public override IReadOnlyDictionary<string, string> HarnessReplies { get; } = new Dictionary<string, string> { ["GET /dart/v1/positions"] = """{"status":"SUCCESS","positions":[]}""" };
    public override IReadOnlyDictionary<string, string> Settings { get; } = new Dictionary<string, string> { ["Brokers:RetryBackoffMs:definedge"] = "0", ["OpenAlgoApi:HistorySpacingMs"] = "0", ["Brokers:HistoryGapMs:definedge"] = "0" };
}

public sealed class DefinedgeHost() : DefinedgeFileHost("definedge.fixtures.json");

/// <summary>Definedge against the Python implementation (tools/contract-fixtures/definedge.fixtures.json).</summary>
public sealed class DefinedgeContractTests(DefinedgeHost host, ITestOutputHelper output) : IClassFixture<DefinedgeHost>
{
    private const string Words = "OpenAlgo shows a failed read as an empty list or an empty balance. EP Trade bot says what Definedge did and what to do.";

    private static Deviation Says(int status, string message) => new(Words, f => Expect(f, status, Error(message)));

    public static readonly Dictionary<string, Deviation> Deviations = new()
    {
        ["placeorder_refused_with_an_emsg"] = new("OpenAlgo answers a refused order with a generic sentence and drops Definedge's reason. EP Trade bot passes Definedge's own words on.", f => Expect(f, 400, Error("Bad quantity"))),
        ["placeorder_success_without_an_order_number"] = new("OpenAlgo reports a refusal with a generic sentence when Definedge accepts an order and sends no number. EP Trade bot says it may have been placed and to check the order book.",
            f => Expect(f, 502, Error("Definedge accepted the order but sent no order number, so it cannot be followed. Check the order book on Definedge before trying again."))),
        ["placeorder_not_json"] = new("OpenAlgo answers a failed call with a generic sentence. EP Trade bot says the order may have been placed and to check the order book, because the call failed after it was sent.",
            f => Expect(f, 502, Error("Definedge could not confirm the order, so it may have been placed. Check the order book on Definedge before trying again."))),
        ["placeorder_unknown_symbol"] = new("OpenAlgo sends the order with no symbol and reports it placed. The platform names the symbol and says what to do, and sends nothing.",
            f => { Expect(f, 400, Error("NOPE on NSE is not in the Definedge master contract. Check the symbol, or download the master contract again.")); f["sent"]!.AsArray().Clear(); }),
        ["modifyorder_refused_with_an_emsg"] = new("Same words.", f => Expect(f, 400, Error("Nope"))),
        ["orderbook_failed"] = Says(500, "Definedge could not send the order book: Session expired"),
        ["tradebook_failed"] = Says(500, "Definedge could not send the trade book: Session expired"),
        ["positionbook_failed"] = Says(500, "Definedge could not send the positions: Session expired"),
        ["holdings_failed"] = Says(500, "Definedge could not send the holdings: Session expired"),
        ["funds_failed"] = Says(500, "Definedge could not send the funds: Session expired"),
        ["funds_http_error"] = Says(500, "Definedge could not send the funds: no reason was given."),
        ["cancelallorder_book_failed"] = new("OpenAlgo cancels nothing and reports success when the order book cannot be read. EP Trade bot reports the failure.", f => Expect(f, 500, Error("Failed to cancel all orders due to internal error"))),
        ["closeposition_book_failed"] = new("OpenAlgo answers that every position was squared off when the positions cannot be read. EP Trade bot says nothing was closed and to try again.", f => Expect(f, 500, Error("Your positions could not be read from definedge, so nothing was closed. Try again in a moment."))),
        ["closeposition"] = new("OpenAlgo sends an order with no symbol for a position it does not know and still reports every position squared off. EP Trade bot names the position it could not close and sends no order for it.",
            f =>
            {
                Expect(f, 500, Error("1 position(s) could not be squared off and are still open: NOSUCH-EQ (NSE): NOSUCH-EQ on NSE is not in the Definedge master contract. Check the symbol, or download the master contract again."));
                var sent = f["sent"]!.AsArray();
                foreach (var order in sent.OfType<JsonObject>().Where(s => s["json"] is JsonObject j && j["tradingsymbol"]?.GetValue<string>() == "NOSUCH-EQ" || s["json"] is JsonObject k && k["tradingsymbol"] is null).ToList()) sent.Remove(order);
            }),
        ["placesmartorder_already_matched"] = new("OpenAlgo answers an already-matched smart order with a 500 whose message reads as a failure. EP Trade bot answers success and says the position already matches.", f => Expect(f, 200, new JsonObject { ["status"] = "success", ["message"] = "Positions Already Matched. No Action needed." })),
    };

    public static IEnumerable<object[]> Names() => ContractRunner.Names("definedge.fixtures.json");

    [PostgresTheory]
    [MemberData(nameof(Names))]
    public Task Matches_the_python_implementation(string name) => RunAsync(host, name, Deviations, output);
}
