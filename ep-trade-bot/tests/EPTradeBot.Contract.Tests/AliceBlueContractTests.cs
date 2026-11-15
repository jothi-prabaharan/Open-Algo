using System.Text.Json.Nodes;
using EPTradeBot.IntegrationTests;
using Xunit.Abstractions;
using static EPTradeBot.Contract.Tests.ContractRunner;

namespace EPTradeBot.Contract.Tests;

public abstract class AliceBlueFileHost(string file) : BrokerContractHost
{
    public override string BrokerName => "aliceblue";
    public override string FixtureFile => file;
    public override bool NumbersMayBeText => true;
    public override IReadOnlyDictionary<string, string> Keys { get; } = new Dictionary<string, string> { ["ApiKey"] = "ab-app-code", ["ApiSecret"] = "ab-secret" };
    public override IReadOnlyDictionary<(string, string), decimal> Prices { get; } = new Dictionary<(string, string), decimal>
    {
        [("NSE", "SBIN")] = 805m, [("NSE", "INFY")] = 1500m, [("BSE", "TATAMOTORS")] = 500m, [("NFO", "NIFTY25NOV26FUT")] = 24000m, [("BFO", "SENSEX26NOVFUT")] = 80000m, [("MCX", "GOLDM25NOV26FUT")] = 75000m, [("CDS", "USDINR25NOV26FUT")] = 83.5m,
    };
    public override IReadOnlyDictionary<string, string> HarnessReplies { get; } = new Dictionary<string, string> { ["GET /open-api/od/v1/positions"] = """{"status":"Ok","result":[]}""" };
    public override IReadOnlyDictionary<string, string> Settings { get; } = new Dictionary<string, string> { ["Brokers:RetryBackoffMs:aliceblue"] = "0", ["OpenAlgoApi:HistorySpacingMs"] = "0", ["Brokers:HistoryGapMs:aliceblue"] = "0" };
}

public sealed class AliceBlueHost() : AliceBlueFileHost("aliceblue.fixtures.json");

/// <summary>AliceBlue orders, books, positions and funds (tools/contract-fixtures/aliceblue.fixtures.json).</summary>
public sealed class AliceBlueContractTests(AliceBlueHost host, ITestOutputHelper output) : IClassFixture<AliceBlueHost>
{
    private const string Words = "OpenAlgo shows a failed read as an empty list or an empty balance. EP Trade bot says what AliceBlue did and what to do.";

    private static Deviation Says(int status, string message) => new(Words, f => Expect(f, status, Error(message)));

    private const string Refusal = "OpenAlgo answers a refused order with a generic sentence that carries the status and a web address. EP Trade bot passes AliceBlue's own words on.";

    public static readonly Dictionary<string, Deviation> Deviations = new()
    {
        ["placeorder_refused"] = new(Refusal, f => Expect(f, 400, Error("Failed to place the order."))),
        ["placeorder_refused_with_prose"] = new(Refusal, f => Expect(f, 400, Error("Margin shortfall"))),
        ["placeorder_refused_without_a_message"] = new(Refusal, f => Expect(f, 400, Error("Failed to place order"))),
        ["placeorder_refused_with_status_200"] = new("OpenAlgo answers success with no order number when AliceBlue refuses an order. EP Trade bot reports the refusal.", f => Expect(f, 400, Error("Rejected by RMS"))),
        ["placeorder_item_error_without_an_order_id"] = new("OpenAlgo answers success with no order number when AliceBlue refuses the order inside an accepted answer. EP Trade bot reports the refusal.",
            f => Expect(f, 400, Error("Quantity must be a multiple of the lot size"))),
        ["placeorder_ok_with_no_result"] = new("OpenAlgo answers success with no order number. EP Trade bot says the order may have been placed and to check the order book.",
            f => Expect(f, 502, Error("AliceBlue accepted the order but sent no order number, so it cannot be followed. Check the order book on AliceBlue before trying again."))),
        ["placeorder_ok_without_an_order_number"] = new("Same.", f => Expect(f, 502, Error("AliceBlue accepted the order but sent no order number, so it cannot be followed. Check the order book on AliceBlue before trying again."))),
        ["placeorder_not_json"] = new("OpenAlgo answers a failed call with a generic sentence. EP Trade bot says the order may have been placed and to check the order book, because the call failed after it was sent.",
            f => Expect(f, 502, Error("AliceBlue could not confirm the order, so it may have been placed. Check the order book on AliceBlue before trying again."))),
        ["placeorder_unknown_symbol"] = new("OpenAlgo fails with an internal error. The platform names the symbol and says what to do, and sends nothing.",
            f => { Expect(f, 400, Error("NOPE on NSE is not in the AliceBlue master contract. Check the symbol, or download the master contract again.")); f["sent"]!.AsArray().Clear(); }),
        ["modifyorder_refused"] = new(Refusal, f => Expect(f, 400, Error("Order is not open"))),
        ["modifyorder_refused_with_status_200"] = new("OpenAlgo answers success when AliceBlue refuses a change. EP Trade bot reports the refusal.", f => Expect(f, 400, Error("Too late"))),
        ["modifyorder_refused_without_a_message"] = new(Refusal, f => Expect(f, 400, Error("Failed to modify order"))),
        ["cancelorder_refused"] = new(Refusal, f => Expect(f, 404, Error("Order not found to cancel"))),
        ["cancelorder_refused_with_status_200"] = new("OpenAlgo answers success when AliceBlue refuses a cancel. EP Trade bot reports the refusal.", f => Expect(f, 400, Error("Already complete"))),
        ["cancelorder_refused_without_a_message"] = new(Refusal, f => Expect(f, 400, Error("Failed to cancel order"))),
        ["cancelallorder_book_failed"] = new("OpenAlgo cancels nothing and reports success when the order book cannot be read. EP Trade bot reports the failure.", f => Expect(f, 500, Error("Failed to cancel all orders due to internal error"))),
        ["orderbook_none_found_is_empty"] = Says(500, "AliceBlue could not send the order book: Failed to retrieve the order book"),
        ["orderbook_failed"] = Says(500, "AliceBlue could not send the order book: Failed to retrieve the order book."),
        ["orderbook_http_error"] = Says(500, "AliceBlue could not send the order book: no reason was given."),
        ["orderbook_not_json"] = Says(500, "AliceBlue could not send the order book: no reason was given."),
        ["tradebook_failed"] = Says(500, "AliceBlue could not send the trade book: Failed to retrieve the trade book."),
        ["positionbook"] = new("OpenAlgo leaves the symbol empty for a position that is not in the master contract. EP Trade bot shows AliceBlue's own name for it.", f =>
        {
            foreach (var row in f["expected"]!["body"]!["data"]!.AsArray().OfType<JsonObject>().Where(r => r["symbol"] is null)) row["symbol"] = "NOSUCH-EQ";
        }),
        ["positionbook_failed_to_retrieve_is_empty"] = Says(500, "AliceBlue could not send the positions: Failed to retrieve the position book."),
        ["positionbook_failed"] = Says(500, "AliceBlue could not send the positions: Failed to retrieve the position book."),
        ["positionbook_http_error"] = Says(500, "AliceBlue could not send the positions: no reason was given."),
        ["holdings_failed_to_retrieve_is_empty"] = Says(500, "AliceBlue could not send the holdings: Failed to retrieve holdings"),
        ["holdings_failed"] = Says(500, "AliceBlue could not send the holdings: Failed to retrieve holdings."),
        ["funds_text_that_is_not_a_number"] = new(Words + " The positions are not read once a figure is unreadable.", f =>
        {
            Expect(f, 500, Error("AliceBlue could not send the funds: a figure was not a number."));
            var sent = f["sent"]!.AsArray();
            foreach (var call in sent.OfType<JsonObject>().Where(c => c["path"]!.GetValue<string>().EndsWith("/positions", StringComparison.Ordinal)).ToList()) sent.Remove(call);
        }),
        ["funds_failed"] = Says(500, "AliceBlue could not send the funds: Failed to retrieve RMS limits."),
        ["funds_http_error"] = Says(500, "AliceBlue could not send the funds: no reason was given."),
        ["closeposition_book_failed"] = new("OpenAlgo answers that every position was squared off when the positions cannot be read. EP Trade bot says nothing was closed and to try again.", f => Expect(f, 500, Error("Your positions could not be read from aliceblue, so nothing was closed. Try again in a moment."))),
        ["closeposition"] = new("OpenAlgo sends an order with no symbol for a position it does not know and still reports every position squared off, and leaves the disclosed quantity empty on the orders it sends. EP Trade bot names the position it could not close and sends a disclosed quantity of zero.",
            f =>
            {
                Expect(f, 500, Error("1 position(s) could not be squared off and are still open: NOSUCH-EQ (NSE): NOSUCH-EQ on NSE is not in the AliceBlue master contract. Check the symbol, or download the master contract again."));
                foreach (var call in f["sent"]!.AsArray().OfType<JsonObject>().Where(c => c["json"] is JsonArray)) call["json"]![0]!["disclosedQuantity"] = "0";
            }),
        ["placesmartorder_book_failed_is_refused"] = new("OpenAlgo answers a position it could not read with a 500; EP Trade bot answers 503 and says to try again.",
            f => Expect(f, 503, Error("Your open position in SBIN could not be read from aliceblue, so no order was placed. Try again in a moment."))),
        ["placesmartorder_already_matched"] = new("OpenAlgo answers an already-matched smart order with a 500 whose message reads as a failure. EP Trade bot answers success and says the position already matches.", f => Expect(f, 200, new JsonObject { ["status"] = "success", ["message"] = "Positions Already Matched. No Action needed." })),
    };

    public static IEnumerable<object[]> Names() => ContractRunner.Names("aliceblue.fixtures.json");

    [PostgresTheory]
    [MemberData(nameof(Names))]
    public Task Matches_the_python_implementation(string name) => RunAsync(host, name, Deviations, output);
}
