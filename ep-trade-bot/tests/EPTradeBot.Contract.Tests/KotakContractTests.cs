using System.Text.Json.Nodes;
using EPTradeBot.IntegrationTests;
using Xunit.Abstractions;
using static EPTradeBot.Contract.Tests.ContractRunner;

namespace EPTradeBot.Contract.Tests;

public sealed class KotakHost : BrokerContractHost
{
    public override string BrokerName => "kotak";
    public override string FixtureFile => "kotak.fixtures.json";
    public override bool NumbersMayBeText => true;
    public override IReadOnlyDictionary<string, string> Keys { get; } = new Dictionary<string, string> { ["ApiKey"] = "UCC123", ["ApiSecret"] = "access-token" };
    public override IReadOnlyDictionary<(string, string), decimal> Prices { get; } = new Dictionary<(string, string), decimal>
    {
        [("NSE", "SBIN")] = 805m, [("NSE", "INFY")] = 1500m, [("NSE", "NOTICK")] = 100m, [("BSE", "TATAMOTORS")] = 500m, [("NFO", "NIFTY25NOV26FUT")] = 24000m, [("NFO", "NIFTY25NOV2624000CE")] = 100m,
        [("BFO", "SENSEX26NOVFUT")] = 80000m, [("MCX", "GOLDM25NOV26FUT")] = 75000m, [("CDS", "USDINR25NOV26FUT")] = 83.5m,
    };
    public override IReadOnlyDictionary<string, string> HarnessReplies { get; } = new Dictionary<string, string> { ["GET /quick/user/positions"] = """{"stat":"Ok","data":[]}""" };
    public override IReadOnlyDictionary<string, string> Settings { get; } = new Dictionary<string, string> { ["Brokers:RetryBackoffMs:kotak"] = "0", ["OpenAlgoApi:HistorySpacingMs"] = "0", ["Brokers:HistoryGapMs:kotak"] = "0" };

    /// <summary>A fresh client order id on every order.</summary>
    public override IReadOnlyDictionary<string, string> VolatileFields { get; } = new Dictionary<string, string> { ["json.ig"] = "^openalgo-[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$" };
}

/// <summary>Kotak Neo against the Python implementation (tools/contract-fixtures/kotak.fixtures.json).</summary>
public sealed class KotakContractTests(KotakHost host, ITestOutputHelper output) : IClassFixture<KotakHost>
{
    private const string Words = "OpenAlgo shows a failed read as an empty list or an empty balance. EP Trade bot says what Kotak Neo did and what to do.";

    private static Deviation Says(int status, string message) => new(Words, f => Expect(f, status, Error(message)));

    private static Deviation Unsendable(string message) => new("OpenAlgo cannot build the order and answers with a generic server error. EP Trade bot says why the order was not sent and what to do, and sends nothing.", f => { Expect(f, 400, Error(message)); f["sent"]!.AsArray().Clear(); });

    public static readonly Dictionary<string, Deviation> Deviations = new()
    {
        ["placeorder_refused"] = new("OpenAlgo answers a refused order with a generic sentence and drops Kotak's reason. EP Trade bot passes Kotak's own words on.", f => Expect(f, 400, Error("RMS: Margin Exceeds"))),
        ["placeorder_success_without_an_order_number"] = new("OpenAlgo fails with a server error when Kotak accepts an order and sends no number. EP Trade bot refuses and tells the trader to check the order book, because an order with no number cannot be followed or cancelled.",
            f => Expect(f, 502, Error("Kotak Neo accepted the order but sent no order number, so it cannot be followed. Check the order book on Kotak Neo before trying again."))),
        ["placeorder_not_json"] = new("OpenAlgo fails with a server error when the call fails after the order was sent. EP Trade bot says the order may have been placed and to check the order book.",
            f => Expect(f, 502, Error("Kotak Neo could not confirm the order, so it may have been placed. Check the order book on Kotak Neo before trying again."))),
        ["placeorder_unknown_symbol"] = new("OpenAlgo sends the order with no symbol and reports it placed. The platform names the symbol and says what to do, and sends nothing.",
            f => { Expect(f, 400, Error("NOPE on NSE is not in the Kotak Neo master contract. Check the symbol, or download the master contract again.")); f["sent"]!.AsArray().Clear(); }),
        ["placeorder_stop_market_without_a_trigger"] = Unsendable("A stop-market order needs a trigger price above zero. Enter one, then try again."),
        ["placeorder_stop_market_without_a_tick_size"] = Unsendable("The tick size of NOTICK on NSE is not in the Kotak Neo master contract, so the limit price of a stop-market order cannot be worked out. Download the master contract again, then try again."),
        ["placeorder_stop_market_sell_too_low"] = Unsendable("The trigger price 0.05 is too low to work out a limit price for a stop-market sell of SBIN. Raise the trigger price, then try again."),
        ["modifyorder_stop_market_without_a_trigger"] = new("OpenAlgo words the refusal for a developer. EP Trade bot says what to do.", f => Expect(f, 400, Error("A stop-market order needs a trigger price above zero. Enter one, then try again."))),
        ["orderbook_failed"] = Says(500, "Kotak Neo could not send the order book: Session expired"),
        ["orderbook_without_data"] = new("OpenAlgo shows one blank order when Kotak sends no order list. EP Trade bot shows none.",
            f => Expect(f, 200, new JsonObject { ["status"] = "success", ["data"] = new JsonObject { ["orders"] = new JsonArray(), ["statistics"] = new JsonObject { ["total_buy_orders"] = 0, ["total_completed_orders"] = 0, ["total_open_orders"] = 0, ["total_rejected_orders"] = 0, ["total_sell_orders"] = 0 } } })),
        ["tradebook_failed"] = Says(500, "Kotak Neo could not send the trade book: Session expired"),
        ["positionbook_failed"] = Says(500, "Kotak Neo could not send the positions: Session expired"),
        ["holdings_failed"] = Says(500, "Kotak Neo could not send the holdings: Session expired"),
        ["funds_failed"] = Says(500, "Kotak Neo could not send the funds: Session expired"),
        ["funds_http_error"] = Says(500, "Kotak Neo could not send the funds: no reason was given."),
        ["closeposition_book_failed"] = new("Same refusal; EP Trade bot says nothing was closed and to try again.", f => Expect(f, 500, Error("Your positions could not be read from kotak, so nothing was closed. Try again in a moment."))),
        ["closeposition"] = new("OpenAlgo sends an order with no symbol for a position it does not know and still reports every position squared off. EP Trade bot names the position it could not close and sends no order for it.",
            f =>
            {
                Expect(f, 500, Error("1 position(s) could not be squared off and are still open: NOSUCH-EQ (NSE): NOSUCH-EQ on NSE is not in the Kotak Neo master contract. Check the symbol, or download the master contract again."));
                var sent = f["sent"]!.AsArray();
                foreach (var order in sent.OfType<JsonObject>().Where(s => s["json"] is JsonObject j && j["ts"] is null).ToList()) sent.Remove(order);
            }),
        ["placesmartorder_already_matched"] = new("OpenAlgo answers an already-matched smart order with a 500 whose message reads as a failure. EP Trade bot answers success and says the position already matches.", f => Expect(f, 200, new JsonObject { ["status"] = "success", ["message"] = "Positions Already Matched. No Action needed." })),
    };

    public static IEnumerable<object[]> Names() => ContractRunner.Names("kotak.fixtures.json");

    [PostgresTheory]
    [MemberData(nameof(Names))]
    public Task Matches_the_python_implementation(string name) => RunAsync(host, name, Deviations, output);
}
