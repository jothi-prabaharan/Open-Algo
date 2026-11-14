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

public abstract class FirstockFileHost(string file) : BrokerContractHost
{
    public override string BrokerName => "firstock";
    public override string FixtureFile => file;
    public override bool NumbersMayBeText => true;
    public override IReadOnlyDictionary<string, string> Keys { get; } = new Dictionary<string, string> { ["ApiKey"] = "AB1234_API", ["ApiSecret"] = "firstock-api-key" };
    public override IReadOnlyDictionary<(string, string), decimal> Prices { get; } = new Dictionary<(string, string), decimal>();
    public override IReadOnlyDictionary<string, string> HarnessReplies { get; } = new Dictionary<string, string>();
    public override IReadOnlyDictionary<string, string> Settings { get; } = new Dictionary<string, string> { ["Brokers:RetryBackoffMs:firstock"] = "0", ["OpenAlgoApi:HistorySpacingMs"] = "0", ["Brokers:HistoryGapMs:firstock"] = "0" };
}

public sealed class FirstockDataHost() : FirstockFileHost("firstock_data.fixtures.json");

public sealed class FirstockHistoryHost() : FirstockFileHost("history_firstock.fixtures.json");

public sealed class MarginFirstockHost() : FirstockFileHost("margin_firstock.fixtures.json");

public sealed class LoginFirstockHost() : FirstockFileHost("login_firstock.fixtures.json");

/// <summary>Firstock quotes, depth and several quotes (tools/contract-fixtures/firstock_data.fixtures.json).</summary>
public sealed class FirstockDataContractTests(FirstockDataHost host, ITestOutputHelper output) : IClassFixture<FirstockDataHost>
{
    private const string Words = "OpenAlgo answers a failed quote with success and an error object where the quote should be, and words a failure for a developer. EP Trade bot answers a failure and says what Firstock did and what to do.";

    private static Deviation Says(int status, string message) => new(Words, f => Expect(f, status, Error(message)));

    public static readonly Dictionary<string, Deviation> Deviations = new()
    {
        ["quotes_refused"] = Says(500, "Firstock could not give the quote: Invalid symbol"),
        ["quotes_refused_without_a_message"] = Says(500, "Firstock could not give the quote: no reason was given."),
        ["quotes_rate_limited_every_time"] = Says(500, "Firstock is limiting how often it can be asked. Wait a moment, then try again."),
        ["quotes_empty_answer"] = Says(500, "Firstock sent an answer that could not be read. Try again in a moment."),
        ["depth_refused"] = Says(500, "Firstock could not give the quote: Invalid symbol"),
        ["multiquotes_refused"] = Says(500, "Firstock could not give the quotes: Bad request"),
        ["multiquotes_refused_with_a_top_level_message"] = Says(500, "Firstock could not give the quotes: Bad request"),
    };

    public static IEnumerable<object[]> Names() => ContractRunner.Names("firstock_data.fixtures.json");

    [PostgresTheory]
    [MemberData(nameof(Names))]
    public Task Matches_the_python_implementation(string name) => RunAsync(host, name, Deviations, output);
}

/// <summary>Firstock history (tools/contract-fixtures/history_firstock.fixtures.json).</summary>
public sealed class FirstockHistoryContractTests(FirstockHistoryHost host, ITestOutputHelper output) : IClassFixture<FirstockHistoryHost>
{
    public static readonly Dictionary<string, Deviation> Deviations = new()
    {
        ["history_weekly_is_not_offered"] = new("OpenAlgo answers an interval Firstock does not offer with an empty list. EP Trade bot answers 400 and names the intervals Firstock offers, so a trader is never handed an empty series that looks like no data.",
            f => { Expect(f, 400, Error("Interval W is not supported by Firstock. Use one of: 1m, 3m, 5m, 10m, 15m, 30m, 1h, 2h, 4h, D.")); f["sent"]!.AsArray().Clear(); }),
    };

    public static IEnumerable<object[]> Names() => ContractRunner.Names("history_firstock.fixtures.json");

    [PostgresTheory]
    [MemberData(nameof(Names))]
    public Task Matches_the_python_implementation(string name) => RunAsync(host, name, Deviations, output);
}

/// <summary>Firstock margin (tools/contract-fixtures/margin_firstock.fixtures.json).</summary>
public sealed class MarginFirstockContractTests(MarginFirstockHost host, ITestOutputHelper output) : IClassFixture<MarginFirstockHost>
{
    private const string Refused = "OpenAlgo answers 200 carrying a refusal. EP Trade bot answers with a failure status and the same words, so a refusal is never read as a margin figure.";
    private const string Unknown = "NOSUCH on NFO is not in the Firstock master contract, so the margin cannot be worked out. Check the symbol, or download the master contract again.";

    public static readonly Dictionary<string, Deviation> Deviations = new()
    {
        ["margin_refused_with_a_message"] = new(Refused, f => Expect(f, 400, Error("Invalid basket"))),
        ["margin_refused_with_a_top_level_message"] = new(Refused, f => Expect(f, 400, Error("Bad basket"))),
        ["margin_refused_with_the_short_message"] = new(Refused, f => Expect(f, 400, Error("Short"))),
        ["margin_refused_without_a_message"] = new(Refused, f => Expect(f, 400, Error("Failed to calculate margin"))),
        ["margin_text_traded_margin"] = new("OpenAlgo answers 200 carrying a parse failure. EP Trade bot answers with a failure status and says to try again.", f => Expect(f, 400, Error("Firstock sent a margin figure EP Trade bot could not read. Try again in a moment."))),
        ["margin_an_answer_that_is_not_json"] = new("Same status; EP Trade bot says to try again.", f => Expect(f, 500, Error("Firstock sent a margin answer EP Trade bot could not read. Try again in a moment."))),
        ["margin_unknown_symbol_alone"] = new("Same status; EP Trade bot names the symbol, where OpenAlgo says no symbol was valid.", f => Expect(f, 400, Error(Unknown))),
        ["margin_unknown_symbol_among_others"] = new("OpenAlgo leaves the position whose symbol it cannot find out of the calculation and works out the rest, which understates the margin. EP Trade bot refuses and names the symbol.", f => { Expect(f, 400, Error(Unknown)); f["sent"]!.AsArray().Clear(); }),
    };

    public static IEnumerable<object[]> Names() => ContractRunner.Names("margin_firstock.fixtures.json");

    [PostgresTheory]
    [MemberData(nameof(Names))]
    public Task Matches_the_python_implementation(string name) => RunAsync(host, name, Deviations, output);
}

/// <summary>Firstock sign-in (tools/contract-fixtures/login_firstock.fixtures.json).</summary>
public sealed class LoginFirstockContractTests(LoginFirstockHost host, ITestOutputHelper output) : IClassFixture<LoginFirstockHost>
{
    private const string Generic = "Firstock did not accept the sign-in. Check the user id, password and one-time code, then try again.";

    public static readonly Dictionary<string, Deviation> Deviations = new()
    {
        ["login_refused_with_a_message"] = LoginDeviations.Refused("Firstock did not accept the sign-in: Invalid TOTP"),
        ["login_refused_naming_a_field"] = LoginDeviations.Refused("Firstock did not accept the sign-in: TOTP: TOTP is invalid"),
        ["login_refused_with_an_error_object_and_no_field"] = LoginDeviations.Refused("Firstock did not accept the sign-in: Bad"),
        ["login_refused_without_a_message"] = LoginDeviations.Refused(Generic),
        ["login_bad_request_with_a_message"] = LoginDeviations.Refused("Firstock did not accept the sign-in: userId is required"),
        ["login_bad_request_without_a_message"] = LoginDeviations.Refused(Generic),
        ["login_unauthorized"] = LoginDeviations.Refused("Firstock did not accept the user id, the password or the API key. Check them, then try again."),
        ["login_success_without_a_session_key"] = new(LoginDeviations.Words, f => ContractRunner.Expect(f, 502, ContractRunner.Error("Firstock accepted the sign-in but sent no session key. Start the sign-in again."))),
        ["login_other_http_error"] = new(LoginDeviations.Words, f => ContractRunner.Expect(f, 502, ContractRunner.Error("Firstock could not sign you in right now. Try again in a minute."))),
    };

    public static IEnumerable<object[]> Names() => ContractRunner.Names("login_firstock.fixtures.json");

    [PostgresTheory]
    [MemberData(nameof(Names))]
    public Task Matches_the_python_implementation(string name) => ContractRunner.RunAsync(host, name, Deviations, output);
}
