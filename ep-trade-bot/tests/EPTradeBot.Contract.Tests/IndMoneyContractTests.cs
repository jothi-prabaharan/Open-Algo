using System.Text.Json.Nodes;
using EPTradeBot.IntegrationTests;
using Xunit.Abstractions;
using static EPTradeBot.Contract.Tests.ContractRunner;

namespace EPTradeBot.Contract.Tests;

public abstract class IndMoneyFileHost(string file) : BrokerContractHost
{
    public override string BrokerName => "indmoney";
    public override string FixtureFile => file;
    public override IReadOnlyDictionary<string, string> Keys { get; } = new Dictionary<string, string> { ["ApiKey"] = "ind-client-id" };
    public override IReadOnlyDictionary<(string, string), decimal> Prices { get; } = new Dictionary<(string, string), decimal>
    {
        [("NSE", "SBIN")] = 805m, [("NSE", "INFY")] = 1500m, [("BSE", "TATAMOTORS")] = 500m, [("NFO", "NIFTY25NOV26FUT")] = 24000m, [("BFO", "SENSEX26NOVFUT")] = 80000m, [("MCX", "GOLDM25NOV26FUT")] = 75000m,
    };
    public override IReadOnlyDictionary<string, string> HarnessReplies { get; } = new Dictionary<string, string>
    {
        ["GET /portfolio/positions"] = """{"status":"success","data":[]}""",
        ["POST /order/cancel"] = """{"status":"success","data":{}}""",
        ["POST /smart/order/cancel"] = """{"status":"success","data":{}}""",
        ["GET /market/quotes/ltp"] = """{"status":"success","data":{}}""",
    };
    public override bool NumbersMayBeText => true;
    public override IReadOnlyDictionary<string, string> Settings { get; } = new Dictionary<string, string> { ["Brokers:RetryBackoffMs:indmoney"] = "0", ["OpenAlgoApi:HistorySpacingMs"] = "0", ["Brokers:HistoryGapMs:indmoney"] = "0" };
}

public sealed class IndMoneyHost() : IndMoneyFileHost("indmoney.fixtures.json");

/// <summary>IndMoney orders, books, positions and funds (tools/contract-fixtures/indmoney.fixtures.json).</summary>
public sealed class IndMoneyContractTests(IndMoneyHost host, ITestOutputHelper output) : IClassFixture<IndMoneyHost>
{
    private const string Words = "OpenAlgo shows a failed read as an empty book, zeros or a status of its own and answers a refusal from IndMoney as success. EP Trade bot says what IndMoney did and what to do.";

    private static Deviation Says(int status, string message) => new(Words, f => Expect(f, status, Error(message)));

    private const string NoNumber = "IndMoney accepted the order but sent no order number, so it cannot be followed. Check the order book on IndMoney before trying again.";

    private const string Missing = "NOPE on NSE is not in the IndMoney master contract. Check the symbol, or download the master contract again.";

    private static Deviation Unsent(int status, string message) => new("OpenAlgo answers an order it could not build with an internal error. EP Trade bot names the cause and sends nothing.", f => { Expect(f, status, Error(message)); f["sent"]!.AsArray().Clear(); });

    public static readonly Dictionary<string, Deviation> Deviations = new()
    {
        ["placeorder_reply_with_no_order_id"] = Says(502, NoNumber),
        ["placeorder_reply_with_data_that_is_not_an_object"] = Says(502, NoNumber),
        ["placeorder_no_order_number_in_the_answer_is_read_as_placed"] = Says(502, "IndMoney placed the order but sent no order number, so it cannot be followed. Check the order book on IndMoney before trying again."),
        ["placeorder_refused_with_failure_and_a_message"] = Says(400, "RMS: insufficient funds"),
        ["placeorder_refused_with_error_and_a_message"] = Says(400, "Insufficient margin"),
        ["placeorder_refused_with_http_500"] = Says(500, "IndMoney refused the order."),
        ["placeorder_not_json"] = Says(502, "IndMoney could not confirm the order, so it may have been placed. Check the order book on IndMoney before trying again."),
        ["placeorder_unknown_symbol"] = Unsent(400, Missing),
        ["placeorder_mcx_is_refused"] = Unsent(400, "IndMoney takes orders on the NSE and BSE segments only; MCX cannot be traded here."),
        ["placeorder_stop_on_bse_is_refused"] = Unsent(400, "IndMoney takes stop orders on the NSE only; BSE was asked for TATAMOTORS."),
        ["placeorder_stop_without_a_trigger_is_refused"] = Unsent(400, "A trigger price is required for a SL order on SBIN."),
        ["modifyorder_refused_with_failure"] = Says(400, "Order is not open"),
        ["modifyorder_refused_with_error"] = Says(400, "Bad price"),
        ["modifyorder_refused_without_a_message"] = Says(400, "Failed to modify order"),
        ["modifyorder_not_json"] = Says(400, "Failed to modify order"),
        ["cancelorder_refused_with_failure"] = Says(400, "Order already complete"),
        ["cancelorder_refused_with_error"] = Says(400, "No such order"),
        ["cancelorder_not_json"] = Says(400, "Failed to cancel order"),
        ["cancelallorder_one_refused"] = new("OpenAlgo counts an order IndMoney refused with status 200 as cancelled. EP Trade bot names it as one it could not cancel.", f =>
            Expect(f, 200, new JsonObject { ["status"] = "success", ["canceled_orders"] = new JsonArray("EQ-1", "DRV-2", "GTT-4"), ["failed_cancellations"] = new JsonArray("EQ-5"), ["message"] = "Canceled 3 orders. Failed to cancel 1 orders." })),
        ["orderbook_failed"] = Says(500, "IndMoney could not send the order book: Session expired."),
        ["orderbook_failed_with_a_message"] = Says(500, "IndMoney could not send the order book: Token invalid."),
        ["orderbook_http_error"] = Says(500, "IndMoney could not send the order book: HTTP 500."),
        ["tradebook_equity_failed_derivative_read"] = Says(500, "IndMoney could not send the trade book: Session expired."),
        ["tradebook_with_the_order_book_failed"] = Says(500, "IndMoney could not send the order book: Session expired."),
        ["tradebook_both_failed"] = Says(500, "IndMoney could not send the trade book: Session expired."),
        ["positionbook_one_query_failed"] = new(Words, f =>
        {
            Expect(f, 500, Error("IndMoney could not send the derivative intraday positions: Session expired."));
            // OpenAlgo prices the positions it did read; a book with a query missing is not given.
            var sent = f["sent"]!.AsArray();
            foreach (var ltp in sent.OfType<JsonObject>().Where(c => c["path"]!.GetValue<string>().EndsWith("/market/quotes/ltp", StringComparison.Ordinal)).ToList()) sent.Remove(ltp);
        }),
        ["positionbook_all_failed"] = Says(500, "IndMoney could not send the derivative margin positions: Session expired."),
        ["holdings_failed"] = Says(500, "IndMoney could not send the holdings: Session expired."),
        ["funds_failed"] = Says(500, "IndMoney could not send the funds: Session expired"),
        ["funds_http_error"] = Says(500, "IndMoney could not send the funds: HTTP 500"),
        ["funds_not_json"] = Says(500, "IndMoney could not send the funds: no reason was given."),
        ["funds_without_data"] = Says(500, "IndMoney could not send the funds: no reason was given."),
        ["closeposition_book_failed"] = new("OpenAlgo answers that every position was squared off when the positions cannot be read. EP Trade bot says nothing was closed and to try again.", f => Expect(f, 500, Error("Your positions could not be read from indmoney, so nothing was closed. Try again in a moment."))),
        ["placesmartorder_book_failed_is_refused"] = new("OpenAlgo answers a position it could not read with a 500; EP Trade bot answers 503 and says to try again.", f => Expect(f, 503, Error("Your open position in SBIN could not be read from indmoney, so no order was placed. Try again in a moment."))),
        ["placesmartorder_already_matched"] = new("OpenAlgo answers an already-matched smart order with a 500 whose message reads as a failure. EP Trade bot answers success and says the position already matches.", f => Expect(f, 200, new JsonObject { ["status"] = "success", ["message"] = "Positions Already Matched. No Action needed." })),
        ["closeposition"] = new("OpenAlgo skips a position whose symbol it cannot find and still reports every position squared off. EP Trade bot names the position it could not close.", f =>
            Expect(f, 500, Error("1 position(s) could not be squared off and are still open: NOSUCH (NSE): NOSUCH on NSE is not in the IndMoney master contract. Check the symbol, or download the master contract again."))),
    };

    public static IEnumerable<object[]> Names() => ContractRunner.Names("indmoney.fixtures.json");

    [PostgresTheory]
    [MemberData(nameof(Names))]
    public Task Matches_the_python_implementation(string name) => RunAsync(host, name, Deviations, output);
}
