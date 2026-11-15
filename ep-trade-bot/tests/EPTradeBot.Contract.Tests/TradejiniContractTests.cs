using System.Text.Json.Nodes;
using EPTradeBot.Core.Streaming.Feeds;
using EPTradeBot.IntegrationTests;
using Xunit.Abstractions;
using static EPTradeBot.Contract.Tests.ContractRunner;

namespace EPTradeBot.Contract.Tests;

public abstract class TradejiniFileHost(string file) : BrokerContractHost
{
    public override string BrokerName => "tradejini";
    public override string FixtureFile => file;
    public override IReadOnlyDictionary<string, string> Keys { get; } = new Dictionary<string, string> { ["ApiKey"] = "tj-app-api-key" };
    public override IReadOnlyDictionary<(string, string), decimal> Prices { get; } = new Dictionary<(string, string), decimal>
    {
        [("NSE", "SBIN")] = 805m, [("NSE", "INFY")] = 1500m, [("BSE", "TATAMOTORS")] = 500m, [("NFO", "NIFTY25NOV26FUT")] = 24000m, [("BFO", "SENSEX26NOVFUT")] = 80000m, [("MCX", "GOLDM25NOV26FUT")] = 75000m, [("CDS", "USDINR25NOV26FUT")] = 83.5m,
    };
    public override IReadOnlyDictionary<string, string> HarnessReplies { get; } = new Dictionary<string, string>
    {
        ["GET /v2/api/oms/positions"] = """{"s":"no-data"}""",
        ["DELETE /v2/api/oms/cancel-order"] = """{"s":"ok","d":{"orderId":"X"}}""",
    };
    public override bool NumbersMayBeText => true;
    public override IReadOnlyDictionary<string, string> Settings { get; } = new Dictionary<string, string> { ["Brokers:RetryBackoffMs:tradejini"] = "0", ["OpenAlgoApi:HistorySpacingMs"] = "0", ["Brokers:HistoryGapMs:tradejini"] = "0" };
}

public sealed class TradejiniHost() : TradejiniFileHost("tradejini.fixtures.json");

/// <summary>Tradejini orders, books, positions and funds (tools/contract-fixtures/tradejini.fixtures.json).</summary>
public sealed class TradejiniContractTests(TradejiniHost host, ITestOutputHelper output) : IClassFixture<TradejiniHost>
{
    private const string Words = "OpenAlgo shows a failed read as an empty list, an empty balance or a generic sentence. EP Trade bot says what Tradejini did and what to do.";

    private static Deviation Says(int status, string message) => new(Words, f => Expect(f, status, Error(message)));

    private const string NoNumber = "Tradejini accepted the order but sent no order number, so it cannot be followed. Check the order book on Tradejini before trying again.";

    private const string Missing = "NOPE on NSE is not in the Tradejini master contract. Check the symbol, or download the master contract again.";

    private static Deviation Refused(int status, string message) => new("OpenAlgo answers a refusal from Tradejini as an order placed with no number (or a generic sentence). EP Trade bot passes Tradejini's reason on.", f => Expect(f, status, Error(message)));

    private static Deviation NoNumberCase() => new("OpenAlgo answers success with no order number when Tradejini accepts an order and sends none. EP Trade bot says it may have been placed and to check the order book.", f => Expect(f, 502, Error(NoNumber)));

    /// <summary>OpenAlgo cancels each order and reports the failures as objects; EP Trade bot names them, and an order Tradejini calls modified is still cancellable.</summary>
    private static Deviation Cancelled(string[] done, string[] failed) => new("EP Trade bot reports the orders it could not cancel by number.", f =>
    {
        var sent = f["sent"]!.AsArray();
        Expect(f, 200, new JsonObject
        {
            ["status"] = "success", ["canceled_orders"] = new JsonArray(done.Select(i => (JsonNode)i).ToArray()), ["failed_cancellations"] = new JsonArray(failed.Select(i => (JsonNode)i).ToArray()),
            ["message"] = $"Canceled {done.Length} orders. Failed to cancel {failed.Length} orders.",
        });
        _ = sent;
    });

    public static readonly Dictionary<string, Deviation> Deviations = new()
    {
        ["placeorder_success_without_an_order_id"] = NoNumberCase(),
        ["placeorder_success_with_an_empty_order_id"] = NoNumberCase(),
        ["placeorder_ok_without_d"] = NoNumberCase(),
        ["placeorder_refused_with_status_200"] = Refused(400, "RMS: insufficient funds"),
        ["placeorder_refused_with_a_message_in_d"] = Refused(400, "Inner reason"),
        ["placeorder_refused_without_a_message"] = Refused(400, "Tradejini refused the order."),
        ["placeorder_refused_with_status_400"] = Refused(400, "Invalid quantity"),
        ["placeorder_refused_with_status_401"] = Refused(401, "Unauthorized"),
        ["placeorder_refused_status_500_not_json"] = Refused(500, "Tradejini refused the order."),
        ["placeorder_not_json"] = new("OpenAlgo answers an internal error when Tradejini's reply is not JSON. EP Trade bot says the order may have been placed.", f => Expect(f, 502, Error("Tradejini could not confirm the order, so it may have been placed. Check the order book on Tradejini before trying again."))),
        ["placeorder_unknown_symbol"] = new("OpenAlgo sends the order with no symbol id and reports it placed. The platform names the symbol and says what to do, and sends nothing.", f => { Expect(f, 400, Error(Missing)); f["sent"]!.AsArray().Clear(); }),
        ["modifyorder_unknown_symbol"] = new("OpenAlgo gives a generic sentence. EP Trade bot names the symbol and says what to do.", f => Expect(f, 400, Error(Missing))),
        ["modifyorder_refused_with_status_200"] = Refused(400, "Order is not open"),
        ["modifyorder_refused_with_status_400"] = Refused(400, "Bad price"),
        ["modifyorder_not_json"] = new("OpenAlgo answers an internal error when Tradejini's reply is not JSON. EP Trade bot answers a refusal that says what was asked.", f => Expect(f, 400, Error("Failed to modify order"))),
        ["cancelorder_refused_with_status_200"] = Refused(400, "Order already complete"),
        ["cancelorder_refused_with_status_404"] = Refused(404, "No such order"),
        ["cancelorder_not_json"] = new("Same.", f => Expect(f, 400, Error("Failed to cancel order"))),
        ["cancelallorder_one_refused"] = Cancelled(["TJ1", "TJ4", "TJ5"], ["TJ2"]),
        ["cancelallorder_book_failed"] = new("OpenAlgo cancels nothing and reports success when the order book cannot be read. EP Trade bot reports the failure.", f => Expect(f, 500, Error("Failed to cancel all orders due to internal error"))),
        ["orderbook_failed"] = Says(500, "Tradejini could not send the order book: Session expired"),
        ["orderbook_not_json"] = Says(500, "Tradejini could not send the order book: no reason was given."),
        ["orderbook_http_error"] = Says(500, "Tradejini could not send the order book: down"),
        ["orderbook_row_that_cannot_be_read"] = Says(500, "Tradejini sent a limit price that could not be read as a number."),
        ["tradebook_failed"] = Says(500, "Tradejini could not send the trade book: Session expired"),
        ["tradebook_not_json"] = Says(500, "Tradejini could not send the trade book: no reason was given."),
        ["tradebook_http_error"] = Says(500, "Tradejini could not send the trade book: no reason was given."),
        ["tradebook_row_that_cannot_be_read"] = Says(500, "Tradejini sent a fill price that could not be read as a number."),
        ["positionbook_failed"] = Says(500, "Tradejini could not send the position book: Session expired"),
        ["positionbook_not_json"] = Says(500, "Tradejini could not send the position book: no reason was given."),
        ["positionbook_http_error"] = Says(500, "Tradejini could not send the position book: no reason was given."),
        ["positionbook_row_that_cannot_be_read"] = Says(500, "Tradejini sent an average price that could not be read as a number."),
        ["holdings_failed"] = Says(500, "Tradejini could not send the holdings: Session expired"),
        ["holdings_http_error"] = Says(500, "Tradejini could not send the holdings: no reason was given."),
        ["funds_failed"] = Says(500, "Tradejini could not send the funds: Session expired"),
        ["funds_http_error"] = Says(500, "Tradejini could not send the funds: no reason was given."),
        ["funds_text_values"] = Says(500, "Tradejini sent a funds figure that could not be read as a number."),
        ["funds_d_that_is_text"] = Says(500, "Tradejini sent funds that could not be read."),
        ["funds_empty_list"] = Says(500, "Tradejini sent no funds figures."),
        ["closeposition"] = new("OpenAlgo skips a position whose symbol it cannot find and still reports every position squared off. EP Trade bot names the position it could not close and sends no order for it.",
            f =>
            {
                Expect(f, 500, Error("1 position(s) could not be squared off and are still open: NOSUCH (NSE): NOSUCH on NSE is not in the Tradejini master contract. Check the symbol, or download the master contract again."));
                // OpenAlgo sends an order for the position it cannot name too, with no symbol id (the text None).
                var sentCalls = f["sent"]!.AsArray();
                foreach (var orphan in sentCalls.OfType<JsonObject>().Where(c => c["form"]?["symId"]?.GetValue<string>() == "None").ToList()) sentCalls.Remove(orphan);
                // An order that carries no price goes with the prices as numbers (0.0); OpenAlgo's square-off leaves them out.
                foreach (var call in f["sent"]!.AsArray().OfType<JsonObject>().Where(c => c["method"]!.GetValue<string>() == "POST"))
                {
                    call["form"]!["limitPrice"] = "0.0";
                    call["form"]!["trigPrice"] = "0.0";
                }
            }),
        ["closeposition_book_failed"] = new("OpenAlgo answers a generic failure when the positions cannot be read. EP Trade bot says nothing was closed and to try again.", f => Expect(f, 500, Error("Your positions could not be read from tradejini, so nothing was closed. Try again in a moment."))),
        ["closeposition_nothing_open"] = new("OpenAlgo answers an error with no status when nothing is open. EP Trade bot answers that every position is squared off.", f => Expect(f, 200, new JsonObject { ["status"] = "success", ["message"] = "All Open Positions Squared Off" })),
        ["placesmartorder_book_failed_is_refused"] = new("OpenAlgo answers a position it could not read with a 500; EP Trade bot answers 503 and says to try again.",
            f => Expect(f, 503, Error("Your open position in SBIN could not be read from tradejini, so no order was placed. Try again in a moment."))),
        ["placesmartorder_already_matched"] = new("OpenAlgo answers an already-matched smart order with a 500 whose message reads as a failure. EP Trade bot answers success and says the position already matches.", f => Expect(f, 200, new JsonObject { ["status"] = "success", ["message"] = "Positions Already Matched. No Action needed." })),
    };

    public static IEnumerable<object[]> Names() => ContractRunner.Names("tradejini.fixtures.json");

    [PostgresTheory]
    [MemberData(nameof(Names))]
    public Task Matches_the_python_implementation(string name) => RunAsync(host, name, Deviations, output);
}

public sealed class LoginTradejiniHost() : TradejiniFileHost("login_tradejini.fixtures.json");

/// <summary>Tradejini sign-in (tools/contract-fixtures/login_tradejini.fixtures.json).</summary>
public sealed class LoginTradejiniContractTests(LoginTradejiniHost host, ITestOutputHelper output) : IClassFixture<LoginTradejiniHost>
{
    private const string Unreadable = "Tradejini sent an answer that could not be read. Try again in a moment.";
    private const string Enter = "Enter the password and the one-time code to sign in to Tradejini.";
    private const string Unauthorized = "Tradejini did not accept the sign-in. Check, in order: the password is your CubePlus login PIN, not your account password; this server is the static IP whitelisted for your app in the Tradejini developer portal; the API key saved here is your app's API key; and the one-time code is current. Tradejini gives the same answer for all four.";

    public static readonly Dictionary<string, Deviation> Deviations = new()
    {
        ["login_with_a_token_type_that_is_not_bearer"] = LoginDeviations.Refused("Tradejini signed you in with a session of a kind this server cannot use (mac). Try again, and tell Tradejini if it keeps happening.", 502),
        ["login_with_no_access_token"] = LoginDeviations.Refused("Tradejini accepted the sign-in but sent no session. Start the sign-in again.", 502),
        ["login_refused_with_a_message"] = LoginDeviations.Refused("Tradejini did not accept the sign-in: Invalid two factor code"),
        ["login_refused_with_a_message_field"] = LoginDeviations.Refused("Tradejini did not accept the sign-in: Account locked"),
        ["login_refused_without_a_message"] = LoginDeviations.Refused("Tradejini did not accept the sign-in. Check your password and the one-time code, then try again."),
        ["login_refused_as_unauthorized"] = LoginDeviations.Refused(Unauthorized),
        ["login_refused_as_unauthorized_without_a_message"] = LoginDeviations.Refused(Unauthorized),
        ["login_with_an_answer_that_is_not_json"] = LoginDeviations.Refused(Unreadable, 502),
        ["login_with_an_error_that_is_not_json"] = LoginDeviations.Refused(Unreadable, 502),
        ["login_with_no_password"] = LoginDeviations.Refused(Enter),
        ["login_with_no_code"] = LoginDeviations.Refused(Enter),
        ["login_with_an_empty_password"] = LoginDeviations.Refused(Enter),
    };

    public static IEnumerable<object[]> Names() => ContractRunner.Names("login_tradejini.fixtures.json");

    [PostgresTheory]
    [MemberData(nameof(Names))]
    public Task Matches_the_python_implementation(string name) => ContractRunner.RunAsync(host, name, Deviations, output);
}

public sealed class HistoryTradejiniHost() : TradejiniFileHost("history_tradejini.fixtures.json");

/// <summary>Tradejini history (tools/contract-fixtures/history_tradejini.fixtures.json).</summary>
public sealed class HistoryTradejiniContractTests(HistoryTradejiniHost host, ITestOutputHelper output) : IClassFixture<HistoryTradejiniHost>
{
    private const string Words = "OpenAlgo answers no candles when the history call fails. EP Trade bot says what Tradejini did and what to do.";

    private const string Failed = "Tradejini could not give candles for NSE:SBIN right now. Try again in a moment.";

    private static Deviation Says(int status, string message) => new(Words, f => Expect(f, status, Error(message)));

    private static Deviation Unsupported(string interval) => new("OpenAlgo sends any interval it is given to Tradejini, which answers no candles for one it does not serve. EP Trade bot names the intervals Tradejini serves and sends nothing.",
        f => { Expect(f, 400, Error($"Interval {interval} is not supported by Tradejini. Use one of: 1m, 5m, 15m, 30m.")); f["sent"]!.AsArray().Clear(); });

    public static readonly Dictionary<string, Deviation> Deviations = new()
    {
        ["history_an_interval_with_no_translation_is_sent_as_it_is"] = Unsupported("1h"),
        ["history_daily"] = Unsupported("D"),
        ["history_error_with_a_message"] = Says(500, Failed),
        ["history_http_error"] = Says(500, Failed),
        ["history_http_401"] = Says(500, Failed),
        ["history_not_json"] = Says(500, Failed),
    };

    public static IEnumerable<object[]> Names() => ContractRunner.Names("history_tradejini.fixtures.json");

    [PostgresTheory]
    [MemberData(nameof(Names))]
    public Task Matches_the_python_implementation(string name) => RunAsync(host, name, Deviations, output);
}

/// <summary>Tradejini's stream against the Python adapter and client (tools/contract-fixtures/feed_tradejini.fixtures.json).</summary>
public sealed class TradejiniFeedContractTests
{
    private const string File = "feed_tradejini.fixtures.json";

    public static IEnumerable<object[]> Names() => FeedRunner.Names(File);

    [Theory]
    [MemberData(nameof(Names))]
    public void Matches_the_python_adapter(string name) => FeedRunner.Run(File, name, () => new EPTradeBot.Core.Streaming.Feeds.TradejiniFeedCodec());
}
