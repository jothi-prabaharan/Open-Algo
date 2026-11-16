using System.Text.Json.Nodes;
using EPTradeBot.IntegrationTests;
using Xunit;
using Xunit.Abstractions;
using static EPTradeBot.Contract.Tests.ContractRunner;

namespace EPTradeBot.Contract.Tests;

public abstract class MotilalFileHost(string file) : BrokerContractHost
{
    public override string BrokerName => "motilal";
    public override string FixtureFile => file;
    public override IReadOnlyDictionary<string, string> Keys { get; } = new Dictionary<string, string> { ["ApiKey"] = "motilal-api-key", ["ApiSecret"] = "motilal-api-secret" };
    public override IReadOnlyDictionary<(string, string), decimal> Prices { get; } = new Dictionary<(string, string), decimal>
    {
        [("NSE", "SBIN")] = 805m, [("NSE", "INFY")] = 1500m, [("BSE", "TATAMOTORS")] = 500m, [("NFO", "NIFTY25NOV26FUT")] = 24000m, [("BFO", "SENSEX26NOVFUT")] = 80000m, [("NFO", "NIFTY25NOV2624000CE")] = 100.5m,
        [("MCX", "GOLDM25NOV26FUT")] = 75000m, [("CDS", "USDINR25NOV26FUT")] = 83.5m, [("NFO", "NOLOTFUT")] = 100m, [("NSE", "NOLOTEQ")] = 100m,
    };
    public override IReadOnlyDictionary<string, string> HarnessReplies { get; } = new Dictionary<string, string>
    {
        ["POST /rest/book/v4/getposition"] = """{"status":"SUCCESS","data":[]}""",
        ["POST /rest/report/v3/getltpdata"] = """{"status":"SUCCESS","data":{}}""",
    };
}

public sealed class MotilalHost() : MotilalFileHost("motilal.fixtures.json");

/// <summary>A host whose cases each start with nothing learned (no dealer login, no index exchange spelling), as every recorded case did.</summary>
public abstract class MotilalFreshHost(string file) : MotilalFileHost(file)
{
    public override Task PrepareAsync(JsonObject fixture)
    {
        EPTradeBot.Infrastructure.Brokers.Motilal.MotilalPlugin.ForgetLearned();
        return Task.CompletedTask;
    }
}

public sealed class DataMotilalHost() : MotilalFreshHost("data_motilal.fixtures.json");

/// <summary>Motilal quotes and today's daily candle (tools/contract-fixtures/data_motilal.fixtures.json).</summary>
public sealed class DataMotilalContractTests(DataMotilalHost host, ITestOutputHelper output) : IClassFixture<DataMotilalHost>
{
    private const string Words = "OpenAlgo answers every failed quote or history read with a 500 and a Python-style message. EP Trade bot says what Motilal did and what to do, with a status that tells a refusal from an unreachable broker.";

    public static readonly Dictionary<string, Deviation> Deviations = new()
    {
        ["quote_index_other_error_is_not_retried"] = new(Words, f => Expect(f, 502, JsonNode.Parse("""{"status":"error","message":"Motilal Oswal did not give the quote for NIFTY: Token expired (errorcode: MO8002) Try again in a moment."}""")!.AsObject())),
        ["history_weekly_is_refused"] = new(Words, f => Expect(f, 400, JsonNode.Parse("""{"status":"error","message":"Interval \u0027W\u0027 is not available from Motilal Oswal: it gives no history, only today\u0027s daily candle (interval D)."}""")!.AsObject())),
        ["quote_refused"] = new(Words, f => Expect(f, 502, JsonNode.Parse("""{"status":"error","message":"Motilal Oswal did not give the quote for SBIN: Token expired (errorcode: MO8002) Try again in a moment."}""")!.AsObject())),
        ["quote_null_data"] = new(Words, f => Expect(f, 502, JsonNode.Parse("""{"status":"error","message":"Motilal Oswal sent no quote for SBIN. Try again in a moment."}""")!.AsObject())),
        ["quote_mcx_index_is_not_supported"] = new(Words, f => Expect(f, 400, JsonNode.Parse("""{"status":"error","message":"Motilal Oswal gives no quote for CRUDEIDX: it can quote NSE and BSE indices only."}""")!.AsObject())),
        ["quote_not_json"] = new(Words, f => Expect(f, 502, JsonNode.Parse("""{"status":"error","message":"Motilal Oswal did not give the quote for SBIN: Motilal Oswal sent an answer that could not be read (HTTP 200). Try again in a moment."}""")!.AsObject())),
        ["quote_dealer_login_refused_again"] = new(Words, f => Expect(f, 502, JsonNode.Parse("""{"status":"error","message":"Motilal Oswal did not give the quote for SBIN: Please provide client code (errorcode: MO1062) Try again in a moment."}""")!.AsObject())),
        ["quote_index_both_field_names_refused"] = new(Words, f => Expect(f, 502, JsonNode.Parse("""{"status":"error","message":"Motilal Oswal did not give the quote for NIFTY: Invalid Exchange Input Parameter (errorcode: MO1051) Try again in a moment."}""")!.AsObject())),
        ["quote_no_data"] = new(Words, f => Expect(f, 502, JsonNode.Parse("""{"status":"error","message":"Motilal Oswal sent no quote for SBIN. Try again in a moment."}""")!.AsObject())),
        ["history_intraday_is_refused"] = new(Words, f => Expect(f, 400, JsonNode.Parse("""{"status":"error","message":"Interval \u00275m\u0027 is not available from Motilal Oswal: it gives no history, only today\u0027s daily candle (interval D)."}""")!.AsObject())),
        ["history_quote_refused"] = new(Words, f => Expect(f, 502, JsonNode.Parse("""{"status":"error","message":"Motilal Oswal did not give the quote for SBIN: Token expired (errorcode: MO8002) Try again in a moment."}""")!.AsObject())),
        ["quote_index_no_rows"] = new(Words, f => Expect(f, 502, JsonNode.Parse("""{"status":"error","message":"Motilal Oswal sent no quote for NIFTY. Try again in a moment."}""")!.AsObject())),
        ["quote_message_without_provide_is_not_a_dealer_login"] = new(Words, f => Expect(f, 502, JsonNode.Parse("""{"status":"error","message":"Motilal Oswal did not give the quote for SBIN: Invalid client code (errorcode: MO2222) Try again in a moment."}""")!.AsObject())),
        ["quote_http_error"] = new(Words, f => Expect(f, 500, JsonNode.Parse("""{"status":"error","message":"Motilal Oswal did not give the quote for SBIN: Motilal Oswal sent an answer that could not be read (HTTP 500). Try again in a moment."}""")!.AsObject())),
    };

    public static IEnumerable<object[]> Names() => ContractRunner.Names("data_motilal.fixtures.json");

    [PostgresTheory]
    [MemberData(nameof(Names))]
    public Task Matches_the_python_implementation(string name) => RunAsync(host, name, Deviations, output);
}

/// <summary>Motilal orders, books, positions, holdings and funds (tools/contract-fixtures/motilal.fixtures.json).</summary>
public sealed class MotilalContractTests(MotilalHost host, ITestOutputHelper output) : IClassFixture<MotilalHost>
{
    private const string RA = "Python reports a refused order, change or cancel as a success (HTTP 200 with an empty order id, or a cancel counted as done); EP Trade bot says it was refused and why.";
    private const string RB = "Python answers a book or funds read that failed or could not be read as an empty book (or empty funds); EP Trade bot says that the read failed and why.";
    private const string RC = "Python answers an unreadable or empty reply as an internal error or a Python error; EP Trade bot says what Motilal did and what to do, with its own status.";
    private const string RD = "The platform or EP Trade bot words this refusal for a trader where Python gives a developer message, a skip or a different status.";
    private const string RF = "Python reports the order book message (the word Success) as the failure; EP Trade bot says the order could not be found.";

    public static readonly Dictionary<string, Deviation> Deviations = new()
    {
        ["cancelallorder_one_refused"] = new(RA, f => Expect(f, 200, JsonNode.Parse("""{"status":"success","canceled_orders":["2611030000001","2611030000006","2611030000007"],"failed_cancellations":["2611030000004"],"message":"Canceled 3 orders. Failed to cancel 1 orders."}""")!.AsObject())),
        ["placeorder_status_in_lower_case"] = new(RA, f => Expect(f, 400, JsonNode.Parse("""{"status":"error","message":"Failed to place order"}""")!.AsObject())),
        ["cancelorder_refused_with_a_message_and_code"] = new(RA, f => Expect(f, 400, JsonNode.Parse("""{"status":"error","message":"Order already complete (errorcode: MO1100)"}""")!.AsObject())),
        ["placeorder_refused_with_a_message_and_code"] = new(RA, f => Expect(f, 400, JsonNode.Parse("""{"status":"error","message":"Insufficient funds"}""")!.AsObject())),
        ["placeorder_success_with_no_order_id"] = new(RA, f => Expect(f, 502, JsonNode.Parse("""{"status":"error","message":"Motilal Oswal accepted the order but sent no order number, so it cannot be followed. Check the order book on Motilal Oswal before trying again."}""")!.AsObject())),
        ["modifyorder_refused_with_no_message"] = new(RA, f => Expect(f, 400, JsonNode.Parse("""{"status":"error","message":"Motilal API returned FAILURE"}""")!.AsObject())),
        ["modifyorder_refused_with_a_message_and_code"] = new(RA, f => Expect(f, 400, JsonNode.Parse("""{"status":"error","message":"Order is not open (errorcode: MO1089)"}""")!.AsObject())),
        ["placeorder_refused_with_no_message"] = new(RA, f => Expect(f, 400, JsonNode.Parse("""{"status":"error","message":"Failed to place order"}""")!.AsObject())),
        ["placeorder_refused_with_no_code"] = new(RA, f => Expect(f, 400, JsonNode.Parse("""{"status":"error","message":"Bad quantity"}""")!.AsObject())),
        ["cancelorder_refused_with_no_message"] = new(RA, f => Expect(f, 400, JsonNode.Parse("""{"status":"error","message":"Motilal API returned FAILURE"}""")!.AsObject())),
        ["tradebook_http_error"] = new(RB, f => Expect(f, 500, JsonNode.Parse("""{"status":"error","message":"Motilal Oswal could not send the trade book: Motilal Oswal sent an answer that could not be read (HTTP 500)."}""")!.AsObject())),
        ["funds_failed"] = new(RB, f => Expect(f, 500, JsonNode.Parse("""{"status":"error","message":"Motilal Oswal could not send the funds: Token expired (errorcode: MO8002)"}""")!.AsObject())),
        ["funds_empty_rows"] = new(RB, f => Expect(f, 500, JsonNode.Parse("""{"status":"error","message":"Motilal Oswal could not send the funds: Success"}""")!.AsObject())),
        ["cancelallorder_book_failed"] = new(RB, f => Expect(f, 500, JsonNode.Parse("""{"status":"error","message":"Failed to cancel all orders due to internal error"}""")!.AsObject())),
        ["orderbook_not_json"] = new(RB, f => Expect(f, 500, JsonNode.Parse("""{"status":"error","message":"Motilal Oswal could not send the order book: Motilal Oswal sent an answer that could not be read (HTTP 200)."}""")!.AsObject())),
        ["orderbook_http_error"] = new(RB, f => Expect(f, 500, JsonNode.Parse("""{"status":"error","message":"Motilal Oswal could not send the order book: Motilal Oswal sent an answer that could not be read (HTTP 500)."}""")!.AsObject())),
        ["positionbook_not_json"] = new(RB, f => Expect(f, 500, JsonNode.Parse("""{"status":"error","message":"Motilal Oswal could not send the positions: Motilal Oswal sent an answer that could not be read (HTTP 200)."}""")!.AsObject())),
        ["funds_null_data"] = new(RB, f => Expect(f, 500, JsonNode.Parse("""{"status":"error","message":"Motilal Oswal could not send the funds: Success"}""")!.AsObject())),
        ["tradebook_not_json"] = new(RB, f => Expect(f, 500, JsonNode.Parse("""{"status":"error","message":"Motilal Oswal could not send the trade book: Motilal Oswal sent an answer that could not be read (HTTP 200)."}""")!.AsObject())),
        ["cancelorder_not_json"] = new(RC, f => Expect(f, 502, JsonNode.Parse("""{"status":"error","message":"Motilal Oswal sent an answer that could not be read, so the order may have been cancelled. Check the order book on Motilal Oswal before trying again."}""")!.AsObject())),
        ["cancelorder_http_error_with_no_body"] = new(RC, f => Expect(f, 502, JsonNode.Parse("""{"status":"error","message":"Motilal Oswal sent an answer that could not be read, so the order may have been cancelled. Check the order book on Motilal Oswal before trying again."}""")!.AsObject())),
        ["holdings_not_json"] = new(RC, f => Expect(f, 500, JsonNode.Parse("""{"status":"error","message":"Motilal Oswal could not send the holdings: Motilal Oswal sent an answer that could not be read (HTTP 200)."}""")!.AsObject())),
        ["modifyorder_not_json"] = new(RC, f => Expect(f, 502, JsonNode.Parse("""{"status":"error","message":"Motilal Oswal sent an answer that could not be read, so the change may have been made. Check the order book on Motilal Oswal before trying again."}""")!.AsObject())),
        ["placeorder_not_json"] = new(RC, f => Expect(f, 502, JsonNode.Parse("""{"status":"error","message":"Motilal Oswal could not confirm the order, so it may have been placed. Check the order book on Motilal Oswal before trying again."}""")!.AsObject())),
        ["funds_not_json"] = new(RC, f => Expect(f, 500, JsonNode.Parse("""{"status":"error","message":"Motilal Oswal could not send the funds: Motilal Oswal sent an answer that could not be read (HTTP 200)."}""")!.AsObject())),
        ["placeorder_http_error_with_no_body"] = new(RC, f => Expect(f, 502, JsonNode.Parse("""{"status":"error","message":"Motilal Oswal could not confirm the order, so it may have been placed. Check the order book on Motilal Oswal before trying again."}""")!.AsObject())),
        ["modifyorder_http_error"] = new(RC, f => Expect(f, 502, JsonNode.Parse("""{"status":"error","message":"Motilal Oswal sent an answer that could not be read, so the change may have been made. Check the order book on Motilal Oswal before trying again."}""")!.AsObject())),
        ["funds_http_error"] = new(RC, f => Expect(f, 500, JsonNode.Parse("""{"status":"error","message":"Motilal Oswal could not send the funds: Motilal Oswal sent an answer that could not be read (HTTP 500)."}""")!.AsObject())),
        ["closeposition_book_failed"] = new(RD, f => Expect(f, 500, JsonNode.Parse("""{"status":"error","message":"Your positions could not be read from motilal, so nothing was closed. Try again in a moment."}""")!.AsObject())),
        ["placesmartorder_book_failed_is_refused"] = new(RD, f => Expect(f, 503, JsonNode.Parse("""{"status":"error","message":"Your open position in SBIN could not be read from motilal, so no order was placed. Try again in a moment."}""")!.AsObject())),
        ["placeorder_unknown_symbol"] = new(RD, f => Expect(f, 400, JsonNode.Parse("""{"status":"error","message":"NOPE on NSE is not in the Motilal Oswal master contract. Check the symbol, or download the master contract again."}""")!.AsObject())),
        ["closeposition_an_unlisted_symbol"] = new(RD, f => Expect(f, 500, JsonNode.Parse("""{"status":"error","message":"1 position(s) could not be squared off and are still open: UNLISTED (NSE): UNLISTED on NSE is not in the Motilal Oswal master contract. Check the symbol, or download the master contract again."}""")!.AsObject())),
        ["closeposition_an_order_refused"] = new(RD, f => Expect(f, 500, JsonNode.Parse("""{"status":"error","message":"1 position(s) could not be squared off and are still open: SBIN (NSE): No margin"}""")!.AsObject())),
        ["modifyorder_derivative_with_no_lot_size"] = new(RD, f => Expect(f, 400, JsonNode.Parse("""{"status":"error","message":"The lot size of NOLOTFUT on NFO is not in the master contract, so the order was not sent: a lot of 1 would multiply the order by the real lot size. Download the master contract again, then retry."}""")!.AsObject())),
        ["placeorder_derivative_with_no_lot_size_is_refused"] = new(RD, f => Expect(f, 400, JsonNode.Parse("""{"status":"error","message":"The lot size of NOLOTFUT on NFO is not in the master contract, so the order was not sent: a lot of 1 would multiply the order by the real lot size. Download the master contract again, then retry."}""")!.AsObject())),
        ["modifyorder_book_empty"] = new(RF, f => Expect(f, 500, JsonNode.Parse("""{"status":"error","message":"Failed to fetch order book"}""")!.AsObject())),
    };

    public static IEnumerable<object[]> Names() => ContractRunner.Names("motilal.fixtures.json");

    [PostgresTheory]
    [MemberData(nameof(Names))]
    public Task Matches_the_python_implementation(string name) => RunAsync(host, name, Deviations, output);
}

public sealed class LoginMotilalHost() : MotilalFileHost("login_motilal.fixtures.json");

/// <summary>Motilal sign-in (tools/contract-fixtures/login_motilal.fixtures.json).</summary>
public sealed class LoginMotilalContractTests(LoginMotilalHost host, ITestOutputHelper output) : IClassFixture<LoginMotilalHost>
{
    private const string RO = "OpenAlgo tells a trader whose login still waits for an SMS or email code that OTP is not yet supported by OpenAlgo; EP Trade bot names itself and says what to turn on.";
    private const string RH = "OpenAlgo puts the HTTP status of a refused or unreadable sign-in in the message, or crashes on an answer that is not an object; EP Trade bot says what to do without the status.";

    public static readonly Dictionary<string, Deviation> Deviations = new()
    {
        ["login_without_the_authenticator_code"] = new("OpenAlgo asks Motilal to send a code by SMS or email when no authenticator code is typed, then refuses the token it gets back; EP Trade bot asks for the authenticator code before it calls Motilal.", null, 400),
        ["login_flag_n_is_refused"] = new(RO, f => Expect(f, 400, JsonNode.Parse("""{"status":"error","message":"Motilal Oswal wants a code sent to you by SMS or email, which is not available here. Turn on an authenticator app with Motilal Oswal and sign in with its code."}""")!.AsObject())),
        ["login_http_error"] = new(RH, f => Expect(f, 400, JsonNode.Parse("""{"status":"error","message":"Motilal Oswal did not accept the sign-in. Check your details, then try again."}""")!.AsObject())),
        ["login_flag_zero_is_refused"] = new(RO, f => Expect(f, 400, JsonNode.Parse("""{"status":"error","message":"Motilal Oswal wants a code sent to you by SMS or email, which is not available here. Turn on an authenticator app with Motilal Oswal and sign in with its code."}""")!.AsObject())),
        ["login_flag_no_is_refused"] = new(RO, f => Expect(f, 400, JsonNode.Parse("""{"status":"error","message":"Motilal Oswal wants a code sent to you by SMS or email, which is not available here. Turn on an authenticator app with Motilal Oswal and sign in with its code."}""")!.AsObject())),
        ["login_answer_that_is_not_json"] = new(RH, f => Expect(f, 502, JsonNode.Parse("""{"status":"error","message":"Motilal Oswal sent an answer that could not be read. Try again in a moment."}""")!.AsObject())),
        ["login_token_waiting_for_its_code_is_refused"] = new(RO, f => Expect(f, 400, JsonNode.Parse("""{"status":"error","message":"Motilal Oswal wants a code sent to you by SMS or email, which is not available here. Turn on an authenticator app with Motilal Oswal and sign in with its code."}""")!.AsObject())),
        ["login_server_error"] = new(RH, f => Expect(f, 400, JsonNode.Parse("""{"status":"error","message":"Motilal Oswal did not accept the sign-in. Check your details, then try again."}""")!.AsObject())),
        ["login_without_the_authenticator_code"] = new(RH, f => Expect(f, 400, JsonNode.Parse("""{"status":"error","message":"Enter the one-time code from your authenticator to sign in to Motilal Oswal. Signing in with a code sent by SMS or email is not available here."}""")!.AsObject())),
    };

    public static IEnumerable<object[]> Names() => ContractRunner.Names("login_motilal.fixtures.json");

    [PostgresTheory]
    [MemberData(nameof(Names))]
    public Task Matches_the_python_implementation(string name) => RunAsync(host, name, Deviations, output);
}
