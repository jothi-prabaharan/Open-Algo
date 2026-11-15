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

/// <summary>A host whose cases each start with no scrip remembered as unquotable, as every recorded case did.</summary>
public abstract class IndMoneyFreshHost(string file) : IndMoneyFileHost(file)
{
    public override Task PrepareAsync(JsonObject fixture)
    {
        EPTradeBot.Infrastructure.Brokers.IndMoney.IndMoneyPlugin.ForgetBadScrips();
        return Task.CompletedTask;
    }
}

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


public sealed class IndMoneyDataHost() : IndMoneyFreshHost("data_indmoney.fixtures.json");

/// <summary>IndMoney quotes, depth and several quotes (tools/contract-fixtures/data_indmoney.fixtures.json).</summary>
public sealed class IndMoneyDataContractTests(IndMoneyDataHost host, ITestOutputHelper output) : IClassFixture<IndMoneyDataHost>
{
    private const string Words = "OpenAlgo answers zeros, or zeros with an error of its own, when IndMoney could not give a quote or a depth. EP Trade bot says what IndMoney did and what to do.";

    private static Deviation Says(int status, string message) => new(Words, f => Expect(f, status, Error(message)));

    public static readonly Dictionary<string, Deviation> Deviations = new()
    {
        ["quote_everything_fails_is_zeros"] = Says(500, "IndMoney could not give a quote for SBIN on NSE. Try again in a moment."),
        ["quote_http_500"] = Says(500, "IndMoney could not give a quote for SBIN on NSE. Try again in a moment."),
        ["quote_not_json"] = Says(500, "IndMoney could not give a quote for SBIN on NSE. Try again in a moment."),
        ["depth_mkt_fails"] = Says(500, "IndMoney could not give the market depth for SBIN on NSE: Indmoney API HTTP Error 400: {\"status\":\"error\",\"message\":\"boom\"}"),
    };

    public static IEnumerable<object[]> Names() => ContractRunner.Names("data_indmoney.fixtures.json");

    [PostgresTheory]
    [MemberData(nameof(Names))]
    public Task Matches_the_python_implementation(string name) => RunAsync(host, name, Deviations, output);
}

public sealed class IndMoneyHistoryHost() : IndMoneyFreshHost("history_indmoney.fixtures.json");

/// <summary>IndMoney history (tools/contract-fixtures/history_indmoney.fixtures.json).</summary>
public sealed class IndMoneyHistoryContractTests(IndMoneyHistoryHost host, ITestOutputHelper output) : IClassFixture<IndMoneyHistoryHost>
{
    private const string Words = "OpenAlgo answers no candles when every history call fails. EP Trade bot says what IndMoney did and what to do.";

    private const string Failed = "IndMoney could not give candles for NSE:SBIN right now. Try again in a moment.";

    private static Deviation Says(int status, string message) => new(Words, f => Expect(f, status, Error(message)));

    public static readonly Dictionary<string, Deviation> Deviations = new()
    {
        ["history_every_chunk_failed_is_no_candles"] = Says(500, Failed),
        ["history_not_json_is_no_candles"] = Says(500, Failed),
        ["history_an_api_error_is_no_candles"] = Says(500, Failed),
    };

    public static IEnumerable<object[]> Names() => ContractRunner.Names("history_indmoney.fixtures.json");

    [PostgresTheory]
    [MemberData(nameof(Names))]
    public Task Matches_the_python_implementation(string name) => RunAsync(host, name, Deviations, output);
}

public sealed class IndMoneyMarginHost() : IndMoneyFileHost("margin_indmoney.fixtures.json");

/// <summary>IndMoney margin (tools/contract-fixtures/margin_indmoney.fixtures.json).</summary>
public sealed class IndMoneyMarginContractTests(IndMoneyMarginHost host, ITestOutputHelper output) : IClassFixture<IndMoneyMarginHost>
{
    private const string Words = "OpenAlgo answers a margin it could not work out with a fixed English sentence and a 500 or a 400. EP Trade bot says what IndMoney did and what to do, with a failure status.";
    private const string Unpriced = "None of the positions could be priced for margin. Check that the symbols are valid.";

    private static Deviation Says(int status, string message) => new(Words, f => Expect(f, status, Error(message)));

    private static Deviation SaysNothingSent(int status, string message) => new(Words, f => { Expect(f, status, Error(message)); f["sent"]!.AsArray().Clear(); });

    public static readonly Dictionary<string, Deviation> Deviations = new()
    {
        ["margin_every_leg_left_out"] = Says(400, Unpriced),
        ["margin_unknown_symbol"] = SaysNothingSent(400, "NOPE on NFO is not in the IndMoney master contract, so the margin cannot be worked out. Check the symbol, or download the master contract again."),
        ["margin_every_leg_failed"] = Says(400, "IndMoney could not work out the margin for 49500, 3045. Check the symbols, then try again."),
        ["margin_http_error_is_read_from_its_body"] = Says(400, "IndMoney could not work out the margin for 49500. Check the symbols, then try again."),
        ["margin_numbers_as_text"] = Says(400, "IndMoney could not work out the margin for 49500. Check the symbols, then try again."),
    };

    public static IEnumerable<object[]> Names() => ContractRunner.Names("margin_indmoney.fixtures.json");

    [PostgresTheory]
    [MemberData(nameof(Names))]
    public Task Matches_the_python_implementation(string name) => RunAsync(host, name, Deviations, output);
}

public sealed class LoginIndMoneyHost() : IndMoneyFileHost("login_indmoney.fixtures.json");

/// <summary>IndMoney sign-in with the MPIN and a one-time code (tools/contract-fixtures/login_indmoney.fixtures.json).</summary>
public sealed class LoginIndMoneyContractTests(LoginIndMoneyHost host, ITestOutputHelper output) : IClassFixture<LoginIndMoneyHost>
{
    private const string Unreadable = "IndMoney sent an answer that could not be read. Try again in a moment.";
    private const string Enter = "Enter the MPIN and the one-time code from your authenticator to sign in to IndMoney.";
    private const string NoToken = "IndMoney accepted the sign-in but sent no token. Start the sign-in again.";
    private const string Hint = " Check the Client ID saved on this server and your MPIN, then wait for a fresh code and never send one you already used. Five wrong codes in 15 minutes lock token requests for 15 minutes. If codes keep failing, check that this server's clock is right.";
    private const string Slow = " IndMoney allows one token request a minute. Wait a minute, then try again.";

    public static readonly Dictionary<string, Deviation> Deviations = new()
    {
        ["login_totp_no_token_in_the_answer"] = LoginDeviations.Refused(NoToken, 502),
        ["login_totp_an_empty_answer"] = LoginDeviations.Refused(NoToken, 502),
        ["login_totp_wrong_code"] = LoginDeviations.Refused("Invalid TOTP" + Hint),
        ["login_totp_forbidden_with_an_error_field"] = LoginDeviations.Refused("Account blocked" + Hint),
        ["login_totp_unauthorized_without_a_message"] = LoginDeviations.Refused("IndMoney did not accept the Client ID, the MPIN or the one-time code." + Hint),
        ["login_totp_throttled_with_a_message"] = LoginDeviations.Refused("Too many requests" + Slow),
        ["login_totp_throttled_without_a_message"] = LoginDeviations.Refused("IndMoney is limiting token requests." + Slow),
        ["login_totp_server_error_with_a_message"] = LoginDeviations.Refused("Internal error"),
        ["login_totp_server_error_without_a_message"] = LoginDeviations.Refused("IndMoney is not available right now. Try again shortly."),
        ["login_totp_bad_request_without_a_message"] = LoginDeviations.Refused("IndMoney did not give a token. Check the details and try again."),
        ["login_totp_answer_is_not_json_with_status_200"] = LoginDeviations.Refused(Unreadable, 502),
        ["login_totp_answer_is_not_json_with_status_401"] = LoginDeviations.Refused(Unreadable, 502),
        ["login_totp_answer_is_not_json_with_status_429"] = LoginDeviations.Refused(Unreadable, 502),
        ["login_totp_answer_is_not_json_with_status_502"] = LoginDeviations.Refused(Unreadable, 502),
        ["login_totp_answer_is_not_json_with_status_400"] = LoginDeviations.Refused(Unreadable, 502),
        ["login_totp_no_mpin"] = LoginDeviations.Refused(Enter),
        ["login_totp_no_code"] = LoginDeviations.Refused(Enter),
        ["login_totp_empty_mpin"] = LoginDeviations.Refused(Enter),
        ["login_manual_no_token"] = LoginDeviations.Refused(Enter),
    };

    public static IEnumerable<object[]> Names() => ContractRunner.Names("login_indmoney.fixtures.json");

    [PostgresTheory]
    [MemberData(nameof(Names))]
    public Task Matches_the_python_implementation(string name) => ContractRunner.RunAsync(host, name, Deviations, output);
}

public sealed class LoginManualIndMoneyHost() : IndMoneyFileHost("login_manual_indmoney.fixtures.json")
{
    public override IReadOnlyDictionary<string, string> Keys { get; } = new Dictionary<string, string> { ["ApiKey"] = "ind-client-id", ["ApiSecret"] = "pasted-token-1" };
}

/// <summary>IndMoney sign-in with a token pasted as the API secret (tools/contract-fixtures/login_manual_indmoney.fixtures.json).</summary>
public sealed class LoginManualIndMoneyContractTests(LoginManualIndMoneyHost host, ITestOutputHelper output) : IClassFixture<LoginManualIndMoneyHost>
{
    private static Deviation Rejected(string why) => LoginDeviations.Refused($"IndMoney did not accept the access token saved as the API secret ({why}). IndMoney tokens last 24 hours. Save a fresh token from the IndMoney access tokens page, or clear the API secret and sign in with your MPIN and a one-time code.");

    public static readonly Dictionary<string, Deviation> Deviations = new()
    {
        ["login_manual_token_rejected_with_401"] = Rejected("Token expired"),
        ["login_manual_token_rejected_with_403_and_an_error_type"] = Rejected("TokenException"),
        ["login_manual_token_rejected_without_a_message"] = Rejected("it was refused"),
        ["login_manual_token_rejected_with_a_body_that_is_a_list"] = Rejected("it was refused"),
        ["login_manual_token_rejected_with_a_body_that_is_not_json"] = Rejected("it was refused"),
    };

    public static IEnumerable<object[]> Names() => ContractRunner.Names("login_manual_indmoney.fixtures.json");

    [PostgresTheory]
    [MemberData(nameof(Names))]
    public Task Matches_the_python_implementation(string name) => ContractRunner.RunAsync(host, name, Deviations, output);
}
