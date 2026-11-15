using System.Text.Json.Nodes;
using EPTradeBot.IntegrationTests;
using Xunit;
using Xunit.Abstractions;
using static EPTradeBot.Contract.Tests.ContractRunner;

namespace EPTradeBot.Contract.Tests;

public abstract class PocketfulFileHost(string file) : BrokerContractHost
{
    public override string BrokerName => "pocketful";
    public override string FixtureFile => file;
    public override IReadOnlyDictionary<string, string> Keys { get; } = new Dictionary<string, string> { ["ApiKey"] = "pf-client-key", ["ApiSecret"] = "pf-client-secret", ["RedirectUrl"] = "http://127.0.0.1:5000/pocketful/callback" };
    public override IReadOnlyDictionary<(string, string), decimal> Prices { get; } = new Dictionary<(string, string), decimal>
    {
        [("NSE", "SBIN")] = 805m, [("NSE", "INFY")] = 1500m, [("BSE", "TATAMOTORS")] = 500m, [("NFO", "NIFTY25NOV26FUT")] = 24000m, [("BFO", "SENSEX26NOVFUT")] = 80000m, [("MCX", "GOLDM25NOV26FUT")] = 75000m,
    };
    public override IReadOnlyDictionary<string, string> HarnessReplies { get; } = new Dictionary<string, string>
    {
        ["GET /api/v1/user/trading_info"] = """{"status":"success","data":{"client_id":"PF100"}}""",
        ["GET /api/v1/positions"] = """{"status":"success","data":[]}""",
    };
    public override IReadOnlySet<string> IgnoredReads { get; } = new HashSet<string> { "/api/v1/user/trading_info" };
    public override bool NumbersMayBeText => true;
}

public sealed class PocketfulHost() : PocketfulFileHost("pocketful.fixtures.json");

/// <summary>Pocketful orders, books, positions and funds (tools/contract-fixtures/pocketful.fixtures.json).</summary>
public sealed class PocketfulContractTests(PocketfulHost host, ITestOutputHelper output) : IClassFixture<PocketfulHost>
{
    private const string Words = "OpenAlgo shows a failed read as an empty book, an empty answer or a Python error of its own, and answers a refusal from Pocketful with a status and text of its own. EP Trade bot says what Pocketful did and what to do.";
    private const string NoNumber = "Pocketful accepted the order but sent no order number, so it cannot be followed. Check the order book on Pocketful before trying again.";
    private const string Missing = "NOPE on NSE is not in the Pocketful master contract. Check the symbol, or download the master contract again.";

    private static Deviation Says(int status, string message) => new(Words, f => Expect(f, status, Error(message)));

    private static Deviation Unsent(int status, string message) => new("OpenAlgo sends an order for a symbol it cannot find with no instrument. EP Trade bot names the cause and sends nothing.", f => { Expect(f, status, Error(message)); f["sent"]!.AsArray().Clear(); });

    public static readonly Dictionary<string, Deviation> Deviations = new()
    {
        ["placeorder_reply_with_no_order_id"] = Says(502, NoNumber),
        ["placeorder_reply_with_data_that_is_a_list"] = Says(502, NoNumber),
        ["placeorder_refused_with_error_and_a_message"] = Says(400, "RMS: insufficient funds"),
        ["placeorder_refused_without_a_message"] = Says(400, "Pocketful refused the order."),
        ["placeorder_refused_with_http_400"] = Says(400, "Invalid quantity"),
        ["placeorder_refused_with_http_500"] = Says(500, "Pocketful refused the order."),
        ["placeorder_not_json"] = Says(502, "Pocketful could not confirm the order, so it may have been placed. Check the order book on Pocketful before trying again."),
        ["placeorder_unknown_symbol"] = Unsent(400, Missing),
        ["modifyorder_unknown_symbol"] = Unsent(400, Missing),
        ["modifyorder_refused_with_http_400"] = Says(400, "Invalid"),
        ["modifyorder_not_json"] = Says(502, "Pocketful sent an answer that could not be read, so the change may have been made. Check the order book on Pocketful before trying again."),
        ["cancelorder_refused_with_http_404"] = Says(404, "Not found"),
        ["cancelorder_not_json"] = Says(502, "Pocketful sent an answer that could not be read, so the order may have been cancelled. Check the order book on Pocketful."),
        ["cancelallorder_book_failed"] = Says(500, "Failed to cancel all orders due to internal error"),
        ["cancelallorder_book_with_http_error"] = Says(500, "Failed to cancel all orders due to internal error"),
        ["orderbook_http_error"] = Says(500, "Pocketful did not give the completed orders (no reason was given). Try again in a moment."),
        ["orderbook_not_json"] = Says(502, "Pocketful did not give the completed orders (no reason was given). Try again in a moment."),
        ["orderbook_completed_failed"] = Says(502, "Pocketful did not give the completed orders (Session expired). Try again in a moment."),
        ["orderbook_pending_failed"] = Says(502, "Pocketful did not give the pending orders (Session expired). Try again in a moment."),
        ["orderbook_both_failed"] = Says(502, "Pocketful did not give the completed orders (Session expired). Try again in a moment."),
        ["tradebook_http_error"] = Says(500, "Pocketful did not give the trade book (no reason was given). Try again in a moment."),
        ["tradebook_not_json"] = Says(502, "Pocketful sent the trade book in a form that could not be read. Try again in a moment."),
        ["positionbook_http_error"] = Says(500, "Pocketful did not give the position book (no reason was given). Try again in a moment."),
        ["positionbook_failed"] = Says(502, "Pocketful did not give the position book (Session expired). Try again in a moment."),
        ["holdings_failed"] = Says(502, "Pocketful did not give the holdings (Session expired). Try again in a moment."),
        ["holdings_html_answer"] = Says(502, "Pocketful sent the holdings in a form that could not be read. Try again in a moment."),
        ["funds_failed"] = Says(502, "Pocketful did not give the funds (Session expired). Try again in a moment."),
        ["funds_http_error"] = Says(500, "Pocketful did not give the funds (no reason was given). Try again in a moment."),
        ["funds_not_json"] = Says(502, "Pocketful did not give the funds (no reason was given). Try again in a moment."),
        ["funds_value_that_is_not_a_number"] = Says(502, "Pocketful sent funds that could not be read. Try again in a moment."),
        ["closeposition_book_failed"] = new("OpenAlgo answers a failed position read with a bare message. EP Trade bot says nothing was closed and to try again.", f => Expect(f, 500, Error("Your positions could not be read from pocketful, so nothing was closed. Try again in a moment."))),
        ["closeposition"] = new("OpenAlgo sends an order with no instrument for a position whose symbol it cannot find and still reports every position squared off. EP Trade bot names the position it could not close.", f =>
        {
            Expect(f, 500, Error("1 position(s) could not be squared off and are still open: NOSUCH-EQ (NSE): NOSUCH-EQ on NSE is not in the Pocketful master contract. Check the symbol, or download the master contract again."));
            // The order OpenAlgo sent with no instrument is not sent.
            var sent = f["sent"]!.AsArray();
            sent.RemoveAt(sent.Count - 1);
        }),
        ["placesmartorder_book_failed_is_refused"] = new("OpenAlgo answers a position it could not read with a 500; EP Trade bot answers 503 and says to try again.", f => Expect(f, 503, Error("Your open position in SBIN could not be read from pocketful, so no order was placed. Try again in a moment."))),
        ["placesmartorder_already_matched"] = new("OpenAlgo answers an already-matched smart order with a 500 whose message reads as a failure. EP Trade bot answers success and says the position already matches.", f => Expect(f, 200, new JsonObject { ["status"] = "success", ["message"] = "Positions Already Matched. No Action needed." })),
    };

    public static IEnumerable<object[]> Names() => ContractRunner.Names("pocketful.fixtures.json");

    [PostgresTheory]
    [MemberData(nameof(Names))]
    public Task Matches_the_python_implementation(string name) => RunAsync(host, name, Deviations, output);
}

public sealed class MarginPocketfulHost() : PocketfulFileHost("margin_pocketful.fixtures.json");

/// <summary>Pocketful has no margin calculator (tools/contract-fixtures/margin_pocketful.fixtures.json).</summary>
public sealed class MarginPocketfulContractTests(MarginPocketfulHost host, ITestOutputHelper output) : IClassFixture<MarginPocketfulHost>
{
    private static Deviation NoMargin => new("Pocketful has no margin calculator; OpenAlgo answers 501 in its own words. EP Trade bot answers 501 in the platform's.", f => Expect(f, 501, Error("pocketful does not support margin calculation in EP Trade bot yet.")));

    public static readonly Dictionary<string, Deviation> Deviations = new()
    {
        ["margin_one_future"] = NoMargin, ["margin_several_legs"] = NoMargin, ["margin_unknown_symbol"] = NoMargin,
    };

    public static IEnumerable<object[]> Names() => ContractRunner.Names("margin_pocketful.fixtures.json");

    [PostgresTheory]
    [MemberData(nameof(Names))]
    public Task Matches_the_python_implementation(string name) => RunAsync(host, name, Deviations, output);
}

public sealed class HistoryPocketfulHost() : PocketfulFileHost("history_pocketful.fixtures.json");

/// <summary>Pocketful has no history (tools/contract-fixtures/history_pocketful.fixtures.json).</summary>
public sealed class HistoryPocketfulContractTests(HistoryPocketfulHost host, ITestOutputHelper output) : IClassFixture<HistoryPocketfulHost>
{
    private static Deviation NoHistory => new("Pocketful has no history; OpenAlgo answers one row that says so as if it were a candle. EP Trade bot answers that history is not available.", f => Expect(f, 501, Error("pocketful does not support historical candles in EP Trade bot yet.")));

    public static readonly Dictionary<string, Deviation> Deviations = new()
    {
        ["history_one_minute"] = NoHistory, ["history_daily"] = NoHistory, ["history_unknown_symbol"] = NoHistory,
    };

    public static IEnumerable<object[]> Names() => ContractRunner.Names("history_pocketful.fixtures.json");

    [PostgresTheory]
    [MemberData(nameof(Names))]
    public Task Matches_the_python_implementation(string name) => RunAsync(host, name, Deviations, output);
}

public sealed class LoginPocketfulHost() : PocketfulFileHost("login_pocketful.fixtures.json");

/// <summary>Pocketful sign-in (tools/contract-fixtures/login_pocketful.fixtures.json).</summary>
public sealed class LoginPocketfulContractTests(LoginPocketfulHost host, ITestOutputHelper output) : IClassFixture<LoginPocketfulHost>
{
    private const string NoClient = "Pocketful signed you in but would not say which client you are";

    public static readonly Dictionary<string, Deviation> Deviations = new()
    {
        ["login_refused_with_a_message"] = LoginDeviations.Refused("Pocketful did not accept the sign-in: Invalid authorization code"),
        ["login_refused_without_a_message"] = LoginDeviations.Refused("Pocketful did not accept the sign-in. Start it again from the beginning."),
        ["login_refused_with_an_answer_that_is_not_json"] = LoginDeviations.Refused("Pocketful did not accept the sign-in. Start it again from the beginning."),
        ["login_refused_with_a_server_error"] = LoginDeviations.Refused("Pocketful did not accept the sign-in: boom"),
        ["login_no_access_token_in_the_answer"] = LoginDeviations.Refused("Pocketful accepted the sign-in but sent no access token. Start the sign-in again.", 502),
        ["login_empty_access_token"] = LoginDeviations.Refused("Pocketful accepted the sign-in but sent no access token. Start the sign-in again.", 502),
        ["login_the_client_id_is_refused"] = LoginDeviations.Refused(NoClient + " (Session expired). Start the sign-in again.", 502),
        ["login_the_client_id_is_refused_without_a_message"] = LoginDeviations.Refused(NoClient + " (no reason was given). Start the sign-in again.", 502),
        ["login_the_client_id_is_missing"] = LoginDeviations.Refused("Pocketful signed you in but sent no client id. Start the sign-in again.", 502),
        ["login_the_client_id_answer_has_no_data"] = LoginDeviations.Refused("Pocketful signed you in but sent no client id. Start the sign-in again.", 502),
        ["login_the_client_id_read_fails_with_http_401"] = LoginDeviations.Refused(NoClient + " (Unauthorized). Start the sign-in again.", 502),
        ["login_the_client_id_read_fails_with_http_500"] = LoginDeviations.Refused(NoClient + " (no reason was given). Start the sign-in again.", 502),
    };

    public static IEnumerable<object[]> Names() => ContractRunner.Names("login_pocketful.fixtures.json");

    [PostgresTheory]
    [MemberData(nameof(Names))]
    public Task Matches_the_python_implementation(string name) => ContractRunner.RunAsync(host, name, Deviations, output);
}
