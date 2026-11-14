using System.Text.Json.Nodes;
using EPTradeBot.IntegrationTests;
using Xunit.Abstractions;
using static EPTradeBot.Contract.Tests.ContractRunner;

namespace EPTradeBot.Contract.Tests;

public sealed class FirstockHost : BrokerContractHost
{
    public override string BrokerName => "firstock";
    public override string FixtureFile => "firstock.fixtures.json";
    public override bool NumbersMayBeText => true;
    public override IReadOnlyDictionary<string, string> Keys { get; } = new Dictionary<string, string> { ["ApiKey"] = "AB1234_API", ["ApiSecret"] = "firstock-api-key" };
    public override IReadOnlyDictionary<(string, string), decimal> Prices { get; } = new Dictionary<(string, string), decimal>
    {
        [("NSE", "SBIN")] = 805m, [("NSE", "INFY")] = 1500m, [("NSE", "M&M")] = 2900m, [("NSE", "NOTICK")] = 100m, [("BSE", "TATAMOTORS")] = 500m, [("NFO", "NIFTY25NOV26FUT")] = 24000m, [("NFO", "NIFTY25NOV2624000CE")] = 100m,
        [("BFO", "SENSEX26NOVFUT")] = 80000m, [("MCX", "GOLDM25NOV26FUT")] = 75000m, [("CDS", "USDINR25NOV26FUT")] = 83.5m,
    };
    public override IReadOnlyDictionary<string, string> HarnessReplies { get; } = new Dictionary<string, string> { ["POST /V1/positionBook"] = """{"status":"success","data":[]}""" };
    public override IReadOnlyDictionary<string, string> Settings { get; } = new Dictionary<string, string> { ["Brokers:RetryBackoffMs:firstock"] = "0", ["OpenAlgoApi:HistorySpacingMs"] = "0", ["Brokers:HistoryGapMs:firstock"] = "0" };
}

/// <summary>Firstock against the Python implementation (tools/contract-fixtures/firstock.fixtures.json).</summary>
public sealed class FirstockContractTests(FirstockHost host, ITestOutputHelper output) : IClassFixture<FirstockHost>
{
    private const string Words = "OpenAlgo shows a failed read as an empty list or an empty balance, or words a failure for a developer. EP Trade bot says what Firstock did and what to do.";

    private static Deviation Says(int status, string message) => new(Words, f => Expect(f, status, Error(message)));

    public static readonly Dictionary<string, Deviation> Deviations = new()
    {
        ["placeorder_refused"] = new("OpenAlgo answers a refused order with a generic sentence and drops Firstock's reason. EP Trade bot passes Firstock's own words on.", f => Expect(f, 400, Error("Insufficient funds"))),
        ["placeorder_success_without_an_order_number"] = new("OpenAlgo reports success with no order id. EP Trade bot refuses and tells the trader to check the order book, because an order with no number cannot be followed or cancelled.",
            f => Expect(f, 502, Error("Firstock accepted the order but sent no order number, so it cannot be followed. Check the order book on Firstock before trying again."))),
        ["placeorder_not_json"] = new("OpenAlgo answers a failed call with a developer's sentence. EP Trade bot says the order may have been placed and to check the order book, because the call failed after it was sent.",
            f => Expect(f, 502, Error("Firstock could not confirm the order, so it may have been placed. Check the order book on Firstock before trying again."))),
        ["placeorder_unknown_symbol"] = new("OpenAlgo sends the order with no symbol and reports it placed. The platform names the symbol and says what to do, and sends nothing.",
            f => { Expect(f, 400, Error("NOPE on NSE is not in the Firstock master contract. Check the symbol, or download the master contract again.")); f["sent"]!.AsArray().Clear(); }),
        ["orderbook_failed"] = Says(500, "Firstock could not send the order book: Session expired"),
        ["tradebook_failed"] = Says(500, "Firstock could not send the trade book: Session expired"),
        ["positionbook_failed"] = Says(500, "Firstock could not send the positions: Session expired"),
        ["holdings_failed"] = Says(500, "Firstock could not send the holdings: Session expired"),
        ["funds_failed"] = Says(500, "Firstock could not send the funds: Session expired"),
        ["funds_http_error"] = Says(500, "Firstock could not send the funds: no reason was given."),
        ["cancelallorder_book_failed"] = new("OpenAlgo cancels nothing and reports success when the order book cannot be read. EP Trade bot reports the failure.", f => Expect(f, 500, Error("Failed to cancel all orders due to internal error"))),
        ["closeposition_book_failed"] = new("Same refusal; EP Trade bot says nothing was closed and to try again.", f => Expect(f, 500, Error("Your positions could not be read from firstock, so nothing was closed. Try again in a moment."))),
        ["positionbook_one_position_as_an_object"] = new("OpenAlgo fails when Firstock answers a single position as an object rather than a list. EP Trade bot reads it as a list of one.",
            f => Expect(f, 200, new JsonObject
            {
                ["status"] = "success",
                ["data"] = new JsonArray(new JsonObject
                {
                    ["average_price"] = "800.00", ["day_buy_amount"] = "8000.00", ["day_buy_average_price"] = "800.00", ["day_buy_quantity"] = "10", ["day_sell_amount"] = "0.00", ["day_sell_average_price"] = "0.00",
                    ["day_sell_quantity"] = "0", ["exchange"] = "NSE", ["last_price"] = "0.00", ["pnl"] = "0.00", ["product"] = "MIS", ["quantity"] = "10", ["realized_pnl"] = "0.00", ["symbol"] = "SBIN", ["unrealized_pnl"] = "0.00",
                }),
            })),
        ["orderbook"] = new("OpenAlgo words a stop order that has not triggered as trigger_pending, which the platform's own cancel-all and the order book's buttons do not recognise, so such an order would be left standing. EP Trade bot words it as every other broker does, trigger pending.",
            f => { foreach (var row in f["expected"]!["body"]!["data"]!["orders"]!.AsArray().OfType<JsonObject>().Where(r => r["order_status"]!.GetValue<string>() == "trigger_pending")) row["order_status"] = "trigger pending"; }),
        ["closeposition"] = new("OpenAlgo skips a position whose symbol it does not know and still reports every position squared off. EP Trade bot names the position it could not close.",
            f => Expect(f, 500, Error("1 position(s) could not be squared off and are still open: NOSUCH-EQ (NSE): NOSUCH-EQ on NSE is not in the Firstock master contract. Check the symbol, or download the master contract again."))),
        ["holdings_with_a_list_of_exchanges"] = new("OpenAlgo asks for a last price for each holding and then ignores it. EP Trade bot does not ask.", f => f["sent"]!.AsArray().RemoveAt(1)),
        ["placesmartorder_already_matched"] = new("OpenAlgo answers an already-matched smart order with a 500 whose message reads as a failure. EP Trade bot answers success and says the position already matches.", f => Expect(f, 200, new JsonObject { ["status"] = "success", ["message"] = "Positions Already Matched. No Action needed." })),
    };

    public static IEnumerable<object[]> Names() => ContractRunner.Names("firstock.fixtures.json");

    [PostgresTheory]
    [MemberData(nameof(Names))]
    public Task Matches_the_python_implementation(string name) => RunAsync(host, name, Deviations, output);
}
