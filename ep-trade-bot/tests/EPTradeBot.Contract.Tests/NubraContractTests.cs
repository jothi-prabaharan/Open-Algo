using System.Text.Json.Nodes;
using EPTradeBot.IntegrationTests;
using Xunit;
using Xunit.Abstractions;
using static EPTradeBot.Contract.Tests.ContractRunner;

namespace EPTradeBot.Contract.Tests;

public abstract class NubraFileHost(string file) : BrokerContractHost
{
    public override string BrokerName => "nubra";
    public override string FixtureFile => file;
    public override IReadOnlyDictionary<string, string> Keys { get; } = new Dictionary<string, string> { ["ApiKey"] = "9876543210", ["ApiSecret"] = "1234" };
    public override IReadOnlyDictionary<(string, string), decimal> Prices { get; } = new Dictionary<(string, string), decimal>
    {
        [("NSE", "SBIN")] = 805m, [("NSE", "INFY")] = 1500m, [("BSE", "TATAMOTORS")] = 500m, [("NFO", "NIFTY25NOV26FUT")] = 24000m, [("BFO", "SENSEX26NOVFUT")] = 80000m, [("MCX", "GOLDM25NOV26FUT")] = 75000m,
    };
    public override IReadOnlyDictionary<string, string> HarnessReplies { get; } = new Dictionary<string, string>
    {
        ["GET /sentinel/portfolio/positions"] = """{"portfolio":{"positions":[]}}""",
        ["GET /sentinel/orders"] = """{"orders":{"open":[]}}""",
        ["POST /sentinel/orders/cancel"] = "{}",
    };
    public override bool NumbersMayBeText => true;
    public override IReadOnlyDictionary<string, string> Settings { get; } = new Dictionary<string, string> { ["Brokers:RetryBackoffMs:nubra"] = "0" };
}

public sealed class NubraHost() : NubraFileHost("nubra.fixtures.json");

/// <summary>Nubra orders, books, positions and funds (tools/contract-fixtures/nubra.fixtures.json).</summary>
public sealed class NubraContractTests(NubraHost host, ITestOutputHelper output) : IClassFixture<NubraHost>
{
    private const string Words = "OpenAlgo answers a failed read with a bare message and status, a throttled or unreadable answer as a success or a Python error, and a refusal with generic text. EP Trade bot says what Nubra did and what to do.";

    private static Deviation Says(int status, string message) => new(Words, f => Expect(f, status, Error(message)));

    public static readonly Dictionary<string, Deviation> Deviations = new()
    {
        ["tradebook_session_expired"] = Says(401, "Nubra session expired or invalid (HTTP 440). Log in to Nubra again."),
        ["tradebook_error"] = Says(400, "Nubra did not give the trade book: Bad request"),
        ["modifyorder_not_json"] = Says(502, "Nubra sent an answer that could not be read, so the change may have been made. Check the order book on Nubra before trying again."),
        ["placeorder_reply_with_no_order_id"] = Says(502, "Nubra accepted the order but sent no order number, so it cannot be followed. Check the order book on Nubra before trying again."),
        ["orderbook_error"] = Says(400, "Nubra did not give the order book: Bad request"),
        ["placeorder_reply_with_orders_that_is_not_a_list"] = Says(502, "Nubra accepted the order but sent no order number, so it cannot be followed. Check the order book on Nubra before trying again."),
        ["cancelorder_empty_reply_is_success"] = Says(502, "Nubra sent an answer that could not be read, so the order may have been cancelled. Check the order book on Nubra."),
        ["placeorder_with_status_200_and_an_error"] = Says(400, "Order rejected"),
        ["cancelorder_empty_error_reply"] = Says(502, "Nubra sent an answer that could not be read, so the order may have been cancelled. Check the order book on Nubra."),
        ["placesmartorder_book_failed_is_refused"] = Says(503, "Your open position in SBIN could not be read from nubra, so no order was placed. Try again in a moment."),
        ["modifyorder_refused_with_no_reason"] = Says(400, "Nubra refused the change."),
        ["placeorder_unknown_symbol"] = Says(400, "NOPE on NSE is not in the Nubra master contract. Check the symbol, or download the master contract again."),
        ["placeorder_reply_with_no_orders"] = Says(502, "Nubra accepted the order but sent no order number, so it cannot be followed. Check the order book on Nubra before trying again."),
        ["modifyorder_empty_reply_is_success"] = Says(502, "Nubra sent an answer that could not be read, so the change may have been made. Check the order book on Nubra before trying again."),
        ["funds_session_expired"] = Says(401, "Nubra session expired or invalid (HTTP 440). Log in to Nubra again."),
        ["placeorder_not_json"] = Says(502, "Nubra sent an answer that could not be read, so the order may have been placed. Check the order book on Nubra before trying again."),
        ["cancelorder_refused_with_no_reason"] = Says(400, "Nubra refused to cancel the order."),
        ["placeorder_refused_with_no_reason"] = Says(400, "Nubra refused the order."),
        ["orderbook_http_error_with_no_body"] = Says(500, "Nubra did not give the order book. Try again in a moment."),
        ["closeposition_an_order_refused"] = Says(500, "1 position(s) could not be squared off and are still open: SBIN (NSE): No margin"),
        ["funds_not_json"] = Says(502, "Nubra sent the funds in a form that could not be read. Try again in a moment."),
        ["cancelorder_throttled_every_time"] = Says(429, "Nubra is limiting how fast orders can be cancelled, and this one was not. Wait a moment, then try again."),
        ["placeorder_throttled_every_time"] = Says(429, "Nubra is limiting how fast orders can be sent, and this one was not placed. Wait a moment, then try again."),
        ["funds_http_error"] = Says(500, "Nubra did not give the funds. Try again in a moment."),
        ["placeorder_reply_with_an_empty_body"] = Says(502, "Nubra accepted the order but sent no order number, so it cannot be followed. Check the order book on Nubra before trying again."),
        ["orderbook_session_expired"] = Says(401, "Nubra session expired or invalid (HTTP 440). Log in to Nubra again."),
        ["placeorder_stop_without_a_trigger_is_refused"] = Says(400, "SL order requires a non-zero trigger_price for SBIN on NSE."),
        ["closeposition_book_failed"] = Says(500, "Your positions could not be read from nubra, so nothing was closed. Try again in a moment."),
        ["closeposition"] = Says(500, "1 position(s) could not be squared off and are still open: NOSUCH (NSE): NOSUCH on NSE is not in the Nubra master contract. Check the symbol, or download the master contract again."),
        ["positionbook_error"] = Says(400, "Nubra did not give the position book: Bad request"),
        ["positionbook_session_expired"] = Says(401, "Nubra session expired or invalid (HTTP 440). Log in to Nubra again."),
        ["modifyorder_throttled_every_time"] = Says(429, "Nubra is limiting how fast changes can be sent, and this one was not made. Wait a moment, then try again."),
        ["placeorder_stop_market_without_a_trigger_is_refused"] = Says(400, "SL-M order requires a non-zero trigger_price for SBIN on NSE."),
        ["orderbook_throttled_every_time"] = Says(429, "Nubra is limiting how fast it is asked for the order book. Wait a moment, then try again."),
        ["cancelorder_not_json"] = Says(502, "Nubra sent an answer that could not be read, so the order may have been cancelled. Check the order book on Nubra."),
        ["orderbook_not_json"] = Says(502, "Nubra sent the order book in a form that could not be read. Try again in a moment."),
        ["holdings_session_expired"] = Says(401, "Nubra session expired or invalid (HTTP 440). Log in to Nubra again."),
        ["holdings_error"] = Says(400, "Nubra did not give the holdings: Bad request"),
        ["placesmartorder_already_matched"] = new("OpenAlgo answers an already-matched smart order with a 500 whose message reads as a failure. EP Trade bot answers success and says the position already matches.", f => Expect(f, 200, new JsonObject { ["status"] = "success", ["message"] = "Positions Already Matched. No Action needed." })),
    };

    public static IEnumerable<object[]> Names() => ContractRunner.Names("nubra.fixtures.json");

    [PostgresTheory]
    [MemberData(nameof(Names))]
    public Task Matches_the_python_implementation(string name) => RunAsync(host, name, Deviations, output);
}

public sealed class MarginNubraHost() : NubraFileHost("margin_nubra.fixtures.json");

/// <summary>Nubra margin (tools/contract-fixtures/margin_nubra.fixtures.json).</summary>
public sealed class MarginNubraContractTests(MarginNubraHost host, ITestOutputHelper output) : IClassFixture<MarginNubraHost>
{
    private const string Words = "OpenAlgo answers a margin Nubra refused with Nubra's words alone, or with a parse error of its own, and an expired session as an unreadable answer. EP Trade bot says what Nubra did and what to do.";

    private static Deviation Says(int status, string message) => new(Words, f => Expect(f, status, Error(message)));

    public static readonly Dictionary<string, Deviation> Deviations = new()
    {
        ["margin_session_expired"] = Says(401, "Nubra session expired or invalid (HTTP 440). Log in to Nubra again."),
        ["margin_unknown_symbol"] = Says(400, "NOPE on NFO is not in the Nubra master contract, so the margin cannot be worked out. Check the symbol, or download the master contract again."),
        ["margin_refused_with_an_error"] = Says(400, "Nubra could not work out the margin: Invalid order"),
        ["margin_refused_with_a_message"] = Says(400, "Nubra could not work out the margin: Bad leg"),
        ["margin_refused_with_no_reason"] = Says(400, "Nubra could not work out the margin: no reason was given."),
        ["margin_answer_is_not_json"] = Says(502, "Nubra sent an answer that could not be read. Try again in a moment."),
        ["margin_with_numbers_that_are_not_numbers"] = Says(502, "Nubra sent margin figures that could not be read. Try again in a moment."),
        ["margin_every_leg_left_out"] = Says(400, "None of the positions could be priced for margin. Check that the symbols are valid."),
    };

    public static IEnumerable<object[]> Names() => ContractRunner.Names("margin_nubra.fixtures.json");

    [PostgresTheory]
    [MemberData(nameof(Names))]
    public Task Matches_the_python_implementation(string name) => RunAsync(host, name, Deviations, output);
}
