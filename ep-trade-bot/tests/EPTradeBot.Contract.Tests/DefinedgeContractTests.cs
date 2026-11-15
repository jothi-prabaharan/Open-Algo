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

public sealed class DefinedgeDataHost() : DefinedgeFileHost("definedge_data.fixtures.json");

/// <summary>Definedge quotes, depth and several quotes (tools/contract-fixtures/definedge_data.fixtures.json), with the clock stopped at the recording.</summary>
public sealed class DefinedgeDataContractTests(DefinedgeDataHost host, ITestOutputHelper output) : IClassFixture<DefinedgeDataHost>
{
    private const string Words = "OpenAlgo puts the status Definedge answered or an internal error in the sentence. EP Trade bot says what Definedge did and what to do.";

    private static Deviation Says(int status, string message) => new(Words, f => Expect(f, status, Error(message)));

    public static readonly Dictionary<string, Deviation> Deviations = new()
    {
        ["quotes_status_not_success"] = Says(500, "Definedge could not give the quote. Try again in a moment."),
        ["quotes_http_error"] = Says(500, "Definedge could not give the quote. Try again in a moment."),
        ["quotes_rate_limited_every_time"] = Says(500, "Definedge could not give the quote. Try again in a moment."),
        ["depth_status_not_success"] = Says(500, "Definedge could not give the depth. Try again in a moment."),
        ["depth_http_error"] = Says(500, "Definedge could not give the depth. Try again in a moment."),
        ["multiquotes_a_failed_quote_is_an_error_entry"] = new("OpenAlgo puts the status code Definedge answered in the error of the quote. EP Trade bot says what happened.", f => f["expected"]!["body"]!["results"]![1]!["error"] = "Definedge answered 500 for this quote."),
        ["multiquotes_a_quote_with_another_status_is_an_error_entry"] = new("OpenAlgo puts Definedge's status word in the error of the quote. EP Trade bot says what happened.", f => f["expected"]!["body"]!["results"]![1]!["error"] = "Definedge could not give this quote."),
    };

    public static IEnumerable<object[]> Names() => ContractRunner.Names("definedge_data.fixtures.json");

    [PostgresTheory]
    [MemberData(nameof(Names))]
    public Task Matches_the_python_implementation(string name) => RunAsync(host, name, Deviations, output);
}

public sealed class MarginDefinedgeHost() : DefinedgeFileHost("margin_definedge.fixtures.json");

/// <summary>Definedge margin (tools/contract-fixtures/margin_definedge.fixtures.json).</summary>
public sealed class MarginDefinedgeContractTests(MarginDefinedgeHost host, ITestOutputHelper output) : IClassFixture<MarginDefinedgeHost>
{
    private const string Refused = "OpenAlgo answers 200 carrying a refusal. EP Trade bot answers with a failure status and the same words, so a refusal is never read as a margin figure.";
    private const string Unknown = "NOSUCH on NFO is not in the Definedge master contract, so the margin cannot be worked out. Check the symbol, or download the master contract again.";

    public static readonly Dictionary<string, Deviation> Deviations = new()
    {
        ["margin_text_that_is_not_a_number"] = new("OpenAlgo answers 200 carrying a parse failure. EP Trade bot answers with a failure status and says to try again.", f => Expect(f, 400, Error("Definedge sent a margin figure EP Trade bot could not read. Try again in a moment."))),
        ["margin_refused_with_a_message"] = new(Refused, f => Expect(f, 400, Error("Invalid basket"))),
        ["margin_refused_without_a_message"] = new(Refused, f => Expect(f, 400, Error("Failed to calculate margin"))),
        ["margin_another_status"] = new(Refused, f => Expect(f, 400, Error("Wait"))),
        ["margin_another_status_without_a_message"] = new(Refused, f => Expect(f, 400, Error("Unknown error from broker"))),
        ["margin_an_answer_that_is_not_json"] = new("Same status; EP Trade bot says to try again.", f => Expect(f, 500, Error("Definedge sent a margin answer EP Trade bot could not read. Try again in a moment."))),
        ["margin_unknown_symbol_alone"] = new("Same status; EP Trade bot names the symbol, where OpenAlgo says no symbol was valid.", f => Expect(f, 400, Error(Unknown))),
        ["margin_unknown_symbol_among_others"] = new("OpenAlgo leaves the position whose symbol it cannot find out of the calculation and works out the rest, which understates the margin. EP Trade bot refuses and names the symbol.", f => { Expect(f, 400, Error(Unknown)); f["sent"]!.AsArray().Clear(); }),
    };

    public static IEnumerable<object[]> Names() => ContractRunner.Names("margin_definedge.fixtures.json");

    [PostgresTheory]
    [MemberData(nameof(Names))]
    public Task Matches_the_python_implementation(string name) => RunAsync(host, name, Deviations, output);
}

public sealed class LoginDefinedgeHost() : DefinedgeFileHost("login_definedge.fixtures.json");

/// <summary>Definedge sign-in (tools/contract-fixtures/login_definedge.fixtures.json).</summary>
public sealed class LoginDefinedgeContractTests(LoginDefinedgeHost host, ITestOutputHelper output) : IClassFixture<LoginDefinedgeHost>
{
    private static Deviation Says(int status, string message) => new(LoginDeviations.Words, f => Expect(f, status, Error(message)));

    public static readonly Dictionary<string, Deviation> Deviations = new()
    {
        ["login_sends_the_code"] = Says(202, "OTP sent to your registered mobile"),
        ["login_sends_the_code_with_no_message"] = Says(202, "OTP has been sent successfully"),
        ["login_cannot_send_the_code"] = Says(502, "Definedge did not send a one-time code. Check the API token and secret on this server, then try again."),
        ["login_without_a_session_key"] = Says(502, "Definedge accepted the code but sent no session. Start the sign-in again."),
        ["login_refused_with_a_message"] = Says(400, "Definedge did not accept the one-time code: Invalid OTP"),
        ["login_refused_without_a_message"] = Says(400, "Definedge did not accept the one-time code. Check it, then try again."),
        ["login_refused_with_a_status"] = Says(400, "Definedge did not accept the one-time code. Check it, then try again."),
        ["login_answer_that_is_not_json"] = Says(502, "Definedge sent an answer that could not be read. Try again in a moment."),
    };

    public static IEnumerable<object[]> Names() => ContractRunner.Names("login_definedge.fixtures.json");

    [PostgresTheory]
    [MemberData(nameof(Names))]
    public Task Matches_the_python_implementation(string name) => ContractRunner.RunAsync(host, name, Deviations, output);
}

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
