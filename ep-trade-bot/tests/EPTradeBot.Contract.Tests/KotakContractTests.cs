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

public abstract class KotakFileHost(string file) : BrokerContractHost
{
    public override string BrokerName => "kotak";
    public override string FixtureFile => file;
    public override bool NumbersMayBeText => true;
    public override IReadOnlyDictionary<string, string> Keys { get; } = new Dictionary<string, string> { ["ApiKey"] = "UCC123", ["ApiSecret"] = "access-token" };
    public override IReadOnlyDictionary<(string, string), decimal> Prices { get; } = new Dictionary<(string, string), decimal>();
    public override IReadOnlyDictionary<string, string> HarnessReplies { get; } = new Dictionary<string, string>();
    public override IReadOnlyDictionary<string, string> Settings { get; } = new Dictionary<string, string> { ["Brokers:RetryBackoffMs:kotak"] = "0", ["OpenAlgoApi:HistorySpacingMs"] = "0", ["Brokers:HistoryGapMs:kotak"] = "0" };
}

public sealed class KotakDataHost() : KotakFileHost("kotak_data.fixtures.json");

/// <summary>Kotak Neo quotes, depth and several quotes (tools/contract-fixtures/kotak_data.fixtures.json).</summary>
public sealed class KotakDataContractTests(KotakDataHost host, ITestOutputHelper output) : IClassFixture<KotakDataHost>
{
    private const string Words = "OpenAlgo shows a failed depth as an empty book, fails a quote with a developer's sentence and leaks Kotak's address into an error. EP Trade bot says what Kotak Neo did and what to do.";

    private static Deviation Says(int status, string message) => new(Words, f => Expect(f, status, Error(message)));

    private static readonly Deviation Quote = Says(500, "Kotak Neo could not give the quote. Try again in a moment.");
    private static readonly Deviation Depth = Says(500, "Kotak Neo could not give the depth. Try again in a moment.");

    public static readonly Dictionary<string, Deviation> Deviations = new()
    {
        ["quotes_neo_error"] = Quote,
        ["quotes_empty_answer"] = Quote,
        ["quotes_http_error"] = Quote,
        ["quotes_rate_limited_every_time"] = Quote,
        ["quotes_index_every_spelling_fails"] = Quote,
        ["depth_neo_error"] = Depth,
        ["depth_index_every_spelling_fails"] = Depth,
        ["multiquotes_neo_error"] = Says(500, "Kotak Neo could not give the quotes. Try again in a moment."),
        ["multiquotes_a_failed_batch_leaves_error_entries"] = new("OpenAlgo puts Kotak's address and its raw answer into the error of every instrument in the failed batch. EP Trade bot says what happened and what to do.",
            f => { foreach (var row in f["expected"]!["body"]!["results"]!.AsArray().OfType<JsonObject>().Where(r => r["error"]?.GetValue<string>().StartsWith("API Error", StringComparison.Ordinal) == true)) row["error"] = "Kotak Neo could not give this quote. Try again in a moment."; }),
    };

    public static IEnumerable<object[]> Names() => ContractRunner.Names("kotak_data.fixtures.json");

    [PostgresTheory]
    [MemberData(nameof(Names))]
    public Task Matches_the_python_implementation(string name) => RunAsync(host, name, Deviations, output);
}

public sealed class MarginKotakHost() : KotakFileHost("margin_kotak.fixtures.json");

/// <summary>Kotak Neo margin (tools/contract-fixtures/margin_kotak.fixtures.json).</summary>
public sealed class MarginKotakContractTests(MarginKotakHost host, ITestOutputHelper output) : IClassFixture<MarginKotakHost>
{
    private const string Refused = "OpenAlgo answers 200 carrying a refusal. EP Trade bot answers with a failure status and the same words, so a refusal is never read as a margin figure.";
    private const string Unknown = "NOSUCH on NFO is not in the Kotak Neo master contract, so the margin cannot be worked out. Check the symbol, or download the master contract again.";

    public static readonly Dictionary<string, Deviation> Deviations = new()
    {
        ["margin_text_amount_with_a_comma"] = new("OpenAlgo answers 200 carrying a parse failure. EP Trade bot answers with a failure status and says to try again.", f => Expect(f, 400, Error("Kotak Neo sent a margin figure EP Trade bot could not read. Try again in a moment."))),
        ["margin_refused_with_a_message"] = new(Refused, f => Expect(f, 400, Error("Invalid order"))),
        ["margin_refused_without_a_message"] = new(Refused, f => Expect(f, 400, Error("Failed to calculate margin"))),
        ["margin_an_answer_that_is_not_json"] = new("Same status; EP Trade bot says to try again.", f => Expect(f, 500, Error("Kotak Neo sent a margin answer EP Trade bot could not read. Try again in a moment."))),
        ["margin_a_failed_leg_is_left_out_of_the_total"] = new("OpenAlgo leaves a leg Kotak refuses out of the total, which understates the margin. EP Trade bot refuses with Kotak's words.", f => Expect(f, 400, Error("Invalid order"))),
        ["margin_every_leg_failed"] = new("OpenAlgo answers a margin of zero when every leg is refused. EP Trade bot refuses with Kotak's words.", f => Expect(f, 400, Error("Invalid order"))),
        ["margin_unknown_symbol_alone"] = new("Same status; EP Trade bot names the symbol, where OpenAlgo says no symbol was valid.", f => Expect(f, 400, Error(Unknown))),
        ["margin_unknown_symbol_among_others"] = new("OpenAlgo leaves the position whose symbol it cannot find out of the calculation and works out the rest, which understates the margin. EP Trade bot refuses and names the symbol.", f => { Expect(f, 400, Error(Unknown)); f["sent"]!.AsArray().Clear(); }),
    };

    public static IEnumerable<object[]> Names() => ContractRunner.Names("margin_kotak.fixtures.json");

    [PostgresTheory]
    [MemberData(nameof(Names))]
    public Task Matches_the_python_implementation(string name) => RunAsync(host, name, Deviations, output);
}

public sealed class KotakHistoryHost() : KotakFileHost("history_kotak.fixtures.json");

/// <summary>Kotak Neo history (tools/contract-fixtures/history_kotak.fixtures.json), with the clock stopped at the recording.</summary>
public sealed class KotakHistoryContractTests(KotakHistoryHost host, ITestOutputHelper output) : IClassFixture<KotakHistoryHost>
{
    private const string NoCode = "Same status. OpenAlgo puts the symbol, the dates, the names it tried and Kotak's own words in the sentence, which a trader cannot act on. EP Trade bot says to try again in a moment.";

    private static Deviation Down(string symbol) => new(NoCode, f => Expect(f, 500, Error($"Kotak Neo could not return candles for {symbol} right now. Try again in a moment.")));

    private static Deviation QuoteOnly(string exchange) => new("OpenAlgo answers 500. EP Trade bot answers 400, because the trader asked for something Kotak Neo does not offer, and says so.",
        f => { Expect(f, 400, Error($"Kotak Neo serves historical data for NSE, BSE, NFO, BFO, NSE_INDEX and BSE_INDEX only. {exchange} is quote-only.")); f["sent"]!.AsArray().Clear(); });

    public static readonly Dictionary<string, Deviation> Deviations = new()
    {
        ["history_an_error_fault_fails_the_pull"] = Down("NSE:SBIN"),
        ["history_a_failed_chunk_fails_the_pull"] = Down("NSE:SBIN"),
        ["history_a_server_error_fails_the_pull"] = Down("NSE:SBIN"),
        ["history_an_answer_that_is_not_json"] = Down("NSE:SBIN"),
        ["history_an_answer_that_is_a_list"] = Down("NSE:SBIN"),
        ["history_a_status_that_is_not_success"] = Down("NSE:SBIN"),
        ["history_rate_limited_every_time"] = Down("NSE:SBIN"),
        ["history_index_tries_every_spelling"] = Down("NSE_INDEX:MIDCPNIFTY"),
        ["history_mcx_is_quote_only"] = QuoteOnly("MCX"),
        ["history_currency_is_quote_only"] = QuoteOnly("CDS"),
        ["history_start_after_end"] = new("OpenAlgo answers 500 with a developer's sentence. EP Trade bot answers 400 and says what to correct.", f => { Expect(f, 400, Error("The start date 2026-11-03 is after the end date 2026-11-02. Correct the dates, then try again.")); f["sent"]!.AsArray().Clear(); }),
        ["history_interval_kotak_lacks"] = new("OpenAlgo answers 500. EP Trade bot answers 400 and names the intervals Kotak Neo offers.", f => { Expect(f, 400, Error("Interval 2m is not supported by Kotak Neo. Use one of: 1m, 3m, 5m, 10m, 15m, 30m, 1h, D, W.")); f["sent"]!.AsArray().Clear(); }),
    };

    public static IEnumerable<object[]> Names() => ContractRunner.Names("history_kotak.fixtures.json");

    [PostgresTheory]
    [MemberData(nameof(Names))]
    public Task Matches_the_python_implementation(string name) => RunAsync(host, name, Deviations, output);
}

public sealed class LoginKotakHost() : KotakFileHost("login_kotak.fixtures.json");

/// <summary>Kotak Neo sign-in (tools/contract-fixtures/login_kotak.fixtures.json).</summary>
public sealed class LoginKotakContractTests(LoginKotakHost host, ITestOutputHelper output) : IClassFixture<LoginKotakHost>
{
    private const string Unreadable = "Kotak Neo sent an answer that could not be read. Try again in a moment.";

    public static readonly Dictionary<string, Deviation> Deviations = new()
    {
        ["login_without_a_data_centre"] = new("OpenAlgo stores the text None as the data centre when Kotak sends null. EP Trade bot stores none.", f => f["stored"]!["auth_token"] = "trade-token:::trade-sid:::https://cis.kotaksecurities.com:::access-token:::"),
        ["login_without_a_base_address"] = LoginDeviations.Refused("Kotak Neo accepted the sign-in but did not send the address of your account, so no call could be made. Start the sign-in again.", 502),
        ["login_base_address_missing"] = LoginDeviations.Refused("Kotak Neo accepted the sign-in but did not send the address of your account, so no call could be made. Start the sign-in again.", 502),
        ["login_totp_refused_with_a_message"] = LoginDeviations.Refused("Kotak Neo did not accept the one-time code: Invalid TOTP"),
        ["login_totp_refused_with_the_short_message"] = LoginDeviations.Refused("Kotak Neo did not accept the one-time code: Bad request"),
        ["login_totp_refused_without_a_message"] = LoginDeviations.Refused("Kotak Neo did not accept the one-time code. Check it, then try again."),
        ["login_totp_status_not_success"] = LoginDeviations.Refused("Kotak Neo did not accept the one-time code. Check it, then try again."),
        ["login_totp_answer_without_a_token"] = LoginDeviations.Refused("Kotak Neo accepted the one-time code but sent no session. Start the sign-in again.", 502),
        ["login_totp_answer_that_is_not_json"] = LoginDeviations.Refused(Unreadable, 502),
        ["login_totp_answer_data_is_text"] = LoginDeviations.Refused(Unreadable, 502),
        ["login_mpin_refused_with_a_message"] = LoginDeviations.Refused("Kotak Neo did not accept the MPIN: Invalid MPIN"),
        ["login_mpin_refused_with_the_short_message"] = LoginDeviations.Refused("Kotak Neo did not accept the MPIN: Bad pin"),
        ["login_mpin_refused_without_a_message"] = LoginDeviations.Refused("Kotak Neo did not accept the MPIN. Check it, then try again."),
        ["login_mpin_status_not_success"] = LoginDeviations.Refused("Kotak Neo did not accept the MPIN. Check it, then try again."),
        ["login_mpin_answer_without_a_token"] = LoginDeviations.Refused("Kotak Neo accepted the MPIN but sent no session. Start the sign-in again.", 502),
        ["login_mpin_answer_that_is_not_json"] = LoginDeviations.Refused(Unreadable, 502),
    };

    public static IEnumerable<object[]> Names() => ContractRunner.Names("login_kotak.fixtures.json");

    [PostgresTheory]
    [MemberData(nameof(Names))]
    public Task Matches_the_python_implementation(string name) => ContractRunner.RunAsync(host, name, Deviations, output);
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
