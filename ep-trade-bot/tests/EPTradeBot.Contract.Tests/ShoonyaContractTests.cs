using System.Text.Json.Nodes;
using EPTradeBot.IntegrationTests;
using Xunit.Abstractions;
using static EPTradeBot.Contract.Tests.ContractRunner;

namespace EPTradeBot.Contract.Tests;

public abstract class ShoonyaBaseHost : BrokerContractHost
{
    public override bool NumbersMayBeText => true;

    public override IReadOnlyDictionary<string, string> Keys { get; } = new Dictionary<string, string> { ["ApiKey"] = "FA12345:::shoonya-client-id", ["ApiSecret"] = "shoonya-secret" };

    public override IReadOnlyDictionary<(string, string), decimal> Prices { get; } = new Dictionary<(string, string), decimal>
    {
        [("NSE", "SBIN")] = 805m, [("NSE", "INFY")] = 1500m, [("NFO", "NIFTY25NOV26FUT")] = 24000m, [("NFO", "NIFTY25NOV2624000CE")] = 120m, [("MCX", "CRUDEOIL")] = 6500m, [("NSE", "M&M")] = 2800m,
    };

    public override IReadOnlyDictionary<string, string> HarnessReplies { get; } = new Dictionary<string, string>
    {
        ["POST /NorenWClientAPI/PositionBook"] = """{"stat":"Not_Ok","emsg":"no data"}""",
    };
}

public sealed class ShoonyaHost : ShoonyaBaseHost
{
    public override string BrokerName => "shoonya";
    public override string FixtureFile => "shoonya.fixtures.json";
}

/// <summary>Shoonya against the Python implementation (tools/contract-fixtures/shoonya.fixtures.json).</summary>
public sealed class ShoonyaContractTests(ShoonyaHost host, ITestOutputHelper output) : IClassFixture<ShoonyaHost>
{
    public static readonly Dictionary<string, Deviation> Deviations = For("Shoonya");

    public static Dictionary<string, Deviation> For(string display)
    {
        var internalError = $"OpenAlgo fails with an internal error when {display} answers an empty book with its \"no data\" refusal (a dictionary, where a book is a list). EP Trade bot treats it as an empty book.";
        return new Dictionary<string, Deviation>
        {
            ["placeorder_unknown_symbol"] = new("OpenAlgo sends the order with no symbol for a symbol missing from the master contract. EP Trade bot refuses before sending anything.", Status: 400),
            ["placeorder_broker_refuses"] = new(
                $"OpenAlgo answers success with no order id when {display} refuses an order. EP Trade bot refuses with {display}'s reason.",
                f => Expect(f, 500, Error("Rejected : Insufficient funds"))),
            ["modifyorder_refused"] = new(
                $"OpenAlgo answers success when {display} refuses a modify. EP Trade bot refuses with {display}'s reason.",
                f => Expect(f, 500, Error("Rejected : Insufficient funds"))),
            ["cancelorder_refused"] = new(
                $"OpenAlgo answers success when {display} refuses a cancel (it reads a message field {display} does not send). EP Trade bot refuses with {display}'s reason.",
                f => Expect(f, 500, Error("Order not found"))),
            ["cancelallorder_book_empty"] = new(internalError, f => Expect(f, 200, new JsonObject
            {
                ["status"] = "success", ["canceled_orders"] = new JsonArray(), ["failed_cancellations"] = new JsonArray(), ["message"] = "Canceled 0 orders. Failed to cancel 0 orders.",
            })),
            ["closeposition_nothing_open"] = new(internalError, f => Expect(f, 200, new JsonObject { ["status"] = "success", ["message"] = "All Open Positions Squared Off" })),
            ["funds_refused"] = new(
                $"OpenAlgo shows an empty balance when {display} says the session has lapsed. EP Trade bot says so, so nothing is mistaken for a balance.",
                f => Expect(f, 403, Error($"{display} says the session has expired. Log in to {display} again."))),
            ["closeposition"] = new(
                "A position whose symbol is not in the master contract (OLD-EQ) cannot be closed by symbol; OpenAlgo sends the order with no symbol at all. EP Trade bot says which position is still open.",
                f =>
                {
                    Expect(f, 500, Error($"1 position(s) could not be squared off and are still open: OLD-EQ (NSE): OLD-EQ on NSE is not in the {display} master contract. Check the symbol, or download the master contract again."));
                    var sent = f["sent"]!.AsArray();
                    foreach (var x in sent.OfType<JsonObject>().Where(x => x["json"] is JsonObject j && (j.ContainsKey("tsym") && j["tsym"] is null || j.ContainsKey("token") && j["token"] is null)).ToList()) sent.Remove(x);
                }),
        };
    }

    public static IEnumerable<object[]> Names() => ContractRunner.Names("shoonya.fixtures.json");

    [PostgresTheory]
    [MemberData(nameof(Names))]
    public Task Matches_the_python_implementation(string name) => RunAsync(host, name, Deviations, output);
}

public sealed class ShoonyaDataHost : ShoonyaBaseHost
{
    public override string BrokerName => "shoonya";
    public override string FixtureFile => "shoonya_data.fixtures.json";
}

/// <summary>Shoonya quotes, depth and several quotes (tools/contract-fixtures/shoonya_data.fixtures.json).</summary>
public sealed class ShoonyaDataContractTests(ShoonyaDataHost host, ITestOutputHelper output) : IClassFixture<ShoonyaDataHost>
{
    private const string Words = "Same outcome; the reason is worded for the trader.";

    private static Action<JsonObject> Refuse(int status, string message) => f => Expect(f, status, Error(message));

    public static readonly Dictionary<string, Deviation> Deviations = For("Shoonya");

    public static Dictionary<string, Deviation> For(string display) => new()
    {
        ["quotes_refused"] = new(Words, Refuse(500, $"{display} could not send the quote: Error Occurred : 5 \"no data\"")),
        ["quotes_session_expired"] = new("OpenAlgo reports a lapsed session as a quote failure with an internal status. EP Trade bot says the session has expired.", Refuse(403, $"{display} says the session has expired. Log in to {display} again.")),
        ["quotes_other_instruments_answer_every_time"] = new(Words, Refuse(500, $"{display} sent the quote of another instrument (NSE|26000) on all 3 tries. Try again in a moment.")),
        ["quotes_same_token_other_exchange"] = new(Words, Refuse(500, $"{display} sent the quote of another instrument (BSE|3045) on all 3 tries. Try again in a moment.")),
        ["depth_refused"] = new(Words, Refuse(500, $"{display} could not send the quote: no data")),
        ["multiquotes_one_refused"] = new(Words, f => f["expected"]!["body"]!["results"]![1]!["error"] = $"{display} could not send the quote: no data"),
        ["multiquotes_other_instrument_every_time"] = new(Words, f => f["expected"]!["body"]!["results"]![0]!["error"] = $"{display} sent the quote of another instrument (NSE|26000) on all 3 tries. Try again in a moment."),
    };

    public static IEnumerable<object[]> Names() => ContractRunner.Names("shoonya_data.fixtures.json");

    [PostgresTheory]
    [MemberData(nameof(Names))]
    public Task Matches_the_python_implementation(string name) => RunAsync(host, name, Deviations, output);
}

public sealed class ZebuHost : ShoonyaBaseHost
{
    public override string BrokerName => "zebu";
    public override string FixtureFile => "zebu.fixtures.json";
    public override IReadOnlyDictionary<string, string> Keys { get; } = new Dictionary<string, string> { ["ApiKey"] = "FA12345:::zebu-client-id", ["ApiSecret"] = "zebu-secret" };
}

/// <summary>Zebu against the Python implementation (tools/contract-fixtures/zebu.fixtures.json).</summary>
public sealed class ZebuContractTests(ZebuHost host, ITestOutputHelper output) : IClassFixture<ZebuHost>
{
    public static readonly Dictionary<string, Deviation> Deviations = ShoonyaContractTests.For("Zebu");

    public static IEnumerable<object[]> Names() => ContractRunner.Names("zebu.fixtures.json");

    [PostgresTheory]
    [MemberData(nameof(Names))]
    public Task Matches_the_python_implementation(string name) => RunAsync(host, name, Deviations, output);
}

public sealed class ZebuDataHost : ShoonyaBaseHost
{
    public override string BrokerName => "zebu";
    public override string FixtureFile => "zebu_data.fixtures.json";
    public override IReadOnlyDictionary<string, string> Keys { get; } = new Dictionary<string, string> { ["ApiKey"] = "FA12345:::zebu-client-id", ["ApiSecret"] = "zebu-secret" };
}

/// <summary>Zebu quotes, depth and several quotes (tools/contract-fixtures/zebu_data.fixtures.json).</summary>
public sealed class ZebuDataContractTests(ZebuDataHost host, ITestOutputHelper output) : IClassFixture<ZebuDataHost>
{
    public static readonly Dictionary<string, Deviation> Deviations = Build();

    private static Dictionary<string, Deviation> Build()
    {
        const string unchecked_ = "OpenAlgo's Zebu plugin takes whatever quote comes back, even another instrument's, so a trader could see a wrong price. EP Trade bot checks it is for the instrument asked about, as it does for Shoonya, and asks again.";
        var d = ShoonyaDataContractTests.For("Zebu");
        d["quotes_other_instruments_answer_is_asked_again"] = new(unchecked_, f =>
            f["expected"] = BrokerContractHost.Load("zebu_data.fixtures.json")["fixtures"]!.AsArray().OfType<JsonObject>().Single(x => x["name"]!.GetValue<string>() == "quotes_equity")["expected"]!.DeepClone());
        d["multiquotes_other_instrument_every_time"] = new(unchecked_, f =>
        {
            var row = f["expected"]!["body"]!["results"]![0]!.AsObject();
            row.Remove("data");
            row["error"] = "Zebu sent the quote of another instrument (NSE|26000) on all 3 tries. Try again in a moment.";
        });
        return d;
    }

    public static IEnumerable<object[]> Names() => ContractRunner.Names("zebu_data.fixtures.json");

    [PostgresTheory]
    [MemberData(nameof(Names))]
    public Task Matches_the_python_implementation(string name) => RunAsync(host, name, Deviations, output);
}

public sealed class ShoonyaHistoryHost : ShoonyaBaseHost
{
    public override string BrokerName => "shoonya";
    public override string FixtureFile => "history_shoonya.fixtures.json";
    public override IReadOnlyDictionary<string, string> Settings { get; } = new Dictionary<string, string> { ["OpenAlgoApi:HistorySpacingMs"] = "0" };
}

/// <summary>Shoonya history (tools/contract-fixtures/history_shoonya.fixtures.json), run with the clock stopped at the recording.</summary>
public sealed class ShoonyaHistoryContractTests(ShoonyaHistoryHost host, ITestOutputHelper output) : IClassFixture<ShoonyaHistoryHost>
{
    private const string Words = "Same outcome; the reason is worded for the trader.";

    public static readonly Dictionary<string, Deviation> Deviations = new()
    {
        ["history_interval_shoonya_lacks"] = new(
            "OpenAlgo answers 500 with \"Unsupported interval '20m'\". EP Trade bot answers 400 and names what Shoonya offers.",
            f => Expect(f, 400, Error("Shoonya does not offer 20m candles. Choose one of: 1m, 3m, 5m, 10m, 15m, 30m, 1h, 2h, 4h, D."))),
        ["history_every_chunk_fails"] = new(Words, f => Expect(f, 500, Error("Shoonya did not send the candles for SBIN between 2026-10-20 and 2026-11-01. Try again in a moment."))),
        ["history_chunk_answers_with_something_else"] = new(Words, f => Expect(f, 500, Error("Shoonya did not send the candles for SBIN between 2026-11-02 and 2026-11-02. Try again in a moment."))),
        ["history_session_expired"] = new(Words, f => Expect(f, 403, Error("Shoonya says the session has expired. Log in to Shoonya again."))),
    };

    public static IEnumerable<object[]> Names() => ContractRunner.Names("history_shoonya.fixtures.json");

    [PostgresTheory]
    [MemberData(nameof(Names))]
    public Task Matches_the_python_implementation(string name) => RunAsync(host, name, Deviations, output);
}

public sealed class ZebuHistoryHost : ShoonyaBaseHost
{
    public override string BrokerName => "zebu";
    public override string FixtureFile => "history_zebu.fixtures.json";
    public override IReadOnlyDictionary<string, string> Keys { get; } = new Dictionary<string, string> { ["ApiKey"] = "FA12345:::zebu-client-id", ["ApiSecret"] = "zebu-secret" };
    public override IReadOnlyDictionary<string, string> Settings { get; } = new Dictionary<string, string> { ["OpenAlgoApi:HistorySpacingMs"] = "0" };
}

/// <summary>Zebu history (tools/contract-fixtures/history_zebu.fixtures.json), run with the clock stopped at the recording.</summary>
public sealed class ZebuHistoryContractTests(ZebuHistoryHost host, ITestOutputHelper output) : IClassFixture<ZebuHistoryHost>
{
    private const string Chart = "OpenAlgo's Zebu plugin fails with an internal error when the chart answers with an error or with something that is not a list of bars. EP Trade bot says Zebu did not send the candles (and, for a daily range, still builds today's bar from a quote).";
    private const string Repair = "OpenAlgo's Zebu plugin passes every bar on as received, so a bar whose range leaves out its own open or close, a volume below zero or two bars with one timestamp reach the chart, which rejects the whole series. EP Trade bot repairs and de-duplicates as it does for Shoonya.";

    public static readonly Dictionary<string, Deviation> Deviations = new()
    {
        ["history_intraday_answers_with_an_error"] = new(Chart, f => Expect(f, 500, Error("Zebu did not send the candles: Error Occurred : 5 \"no data\""))),
        ["history_daily_chart_answers_with_an_error"] = new(Chart, f => Expect(f, 500, Error("Zebu did not send the candles: Error Occurred : 5 \"no data\""))),
        ["history_daily_chart_fails_so_the_quote_is_all_there_is"] = new(Chart, f => Expect(f, 200, new JsonObject
        {
            ["status"] = "success",
            ["data"] = new JsonArray(new JsonObject { ["timestamp"] = 1794508200, ["open"] = 802.45, ["high"] = 809.45, ["low"] = 800.45, ["close"] = 805.45, ["volume"] = 1234567.0, ["oi"] = 0.0 }),
        })),
        ["history_zero_and_unreadable_bars_are_left_out"] = new(Repair, f =>
        {
            var rows = f["expected"]!["body"]!["data"]!.AsArray();
            rows[0]!["high"] = 803.0;
            rows[0]!["low"] = 801.0;
            rows[1]!["high"] = 806.0;
            rows[1]!["volume"] = 0.0;
        }),
        ["history_duplicate_timestamps_are_kept"] = new(Repair, f => f["expected"]!["body"]!["data"]!.AsArray().RemoveAt(0)),
        ["history_interval_zebu_lacks"] = new(
            "OpenAlgo answers 500 with \"Unsupported interval '20m'\". EP Trade bot answers 400 and names what Zebu offers.",
            f => Expect(f, 400, Error("Zebu does not offer 20m candles. Choose one of: 1m, 3m, 5m, 10m, 15m, 30m, 1h, 2h, 4h, D."))),
    };

    public static IEnumerable<object[]> Names() => ContractRunner.Names("history_zebu.fixtures.json");

    [PostgresTheory]
    [MemberData(nameof(Names))]
    public Task Matches_the_python_implementation(string name) => RunAsync(host, name, Deviations, output);
}

public sealed class FlattradeHost : ShoonyaBaseHost
{
    public override string BrokerName => "flattrade";
    public override string FixtureFile => "flattrade.fixtures.json";
    public override IReadOnlyDictionary<string, string> Keys { get; } = new Dictionary<string, string> { ["ApiKey"] = "FA12345:::flattrade-api-key", ["ApiSecret"] = "flattrade-secret" };
    public override IReadOnlyDictionary<string, string> Settings { get; } = new Dictionary<string, string> { ["Brokers:RetryBackoffMs:flattrade"] = "0" };
    public override IReadOnlyDictionary<string, string> HarnessReplies { get; } = new Dictionary<string, string>
    {
        ["POST /PiConnectAPI/PositionBook"] = """{"stat":"Not_Ok","emsg":"no data"}""",
    };
}

/// <summary>Flattrade against the Python implementation (tools/contract-fixtures/flattrade.fixtures.json).</summary>
public sealed class FlattradeContractTests(FlattradeHost host, ITestOutputHelper output) : IClassFixture<FlattradeHost>
{
    public static readonly Dictionary<string, Deviation> Deviations = Build();

    private static Dictionary<string, Deviation> Build()
    {
        var d = ShoonyaContractTests.For("Flattrade");
        d["placeorder_rate_limited_is_not_asked_again"] = new(
            "OpenAlgo answers success with no order id when Flattrade turns an order away. EP Trade bot refuses with Flattrade's reason (and, like OpenAlgo, never sends the order twice).",
            f => Expect(f, 500, Error("Invalid Input : Order Recieved 11 in a current second exceeds Limit 10 for user")));
        return d;
    }

    public static IEnumerable<object[]> Names() => ContractRunner.Names("flattrade.fixtures.json");

    [PostgresTheory]
    [MemberData(nameof(Names))]
    public Task Matches_the_python_implementation(string name) => RunAsync(host, name, Deviations, output);
}

public sealed class FlattradeDataHost : ShoonyaBaseHost
{
    public override string BrokerName => "flattrade";
    public override string FixtureFile => "flattrade_data.fixtures.json";
    public override IReadOnlyDictionary<string, string> Keys { get; } = new Dictionary<string, string> { ["ApiKey"] = "FA12345:::flattrade-api-key", ["ApiSecret"] = "flattrade-secret" };
}

/// <summary>Flattrade quotes, depth and several quotes (tools/contract-fixtures/flattrade_data.fixtures.json).</summary>
public sealed class FlattradeDataContractTests(FlattradeDataHost host, ITestOutputHelper output) : IClassFixture<FlattradeDataHost>
{
    public static readonly Dictionary<string, Deviation> Deviations = Build();

    private static Dictionary<string, Deviation> Build()
    {
        const string unchecked_ = "OpenAlgo's Flattrade plugin takes whatever quote comes back, even another instrument's, so a trader could see a wrong price. EP Trade bot checks it is for the instrument asked about, as it does for Shoonya, and asks again.";
        var d = ShoonyaDataContractTests.For("Flattrade");
        JsonNode Quotes(string name) => BrokerContractHost.Load("flattrade_data.fixtures.json")["fixtures"]!.AsArray().OfType<JsonObject>().Single(x => x["name"]!.GetValue<string>() == "quotes_equity")["expected"]!.DeepClone();
        d["quotes_other_instruments_answer_is_asked_again"] = new(unchecked_, f => f["expected"] = Quotes("quotes_equity"));
        d["multiquotes_other_instrument_every_time"] = new(unchecked_, f =>
        {
            var row = f["expected"]!["body"]!["results"]![0]!.AsObject();
            row.Remove("data");
            row["error"] = "Flattrade sent the quote of another instrument (NSE|26000) on all 3 tries. Try again in a moment.";
        });
        return d;
    }

    public static IEnumerable<object[]> Names() => ContractRunner.Names("flattrade_data.fixtures.json");

    [PostgresTheory]
    [MemberData(nameof(Names))]
    public Task Matches_the_python_implementation(string name) => RunAsync(host, name, Deviations, output);
}

public sealed class FlattradeHistoryHost : ShoonyaBaseHost
{
    public override string BrokerName => "flattrade";
    public override string FixtureFile => "history_flattrade.fixtures.json";
    public override IReadOnlyDictionary<string, string> Keys { get; } = new Dictionary<string, string> { ["ApiKey"] = "FA12345:::flattrade-api-key", ["ApiSecret"] = "flattrade-secret" };
    public override IReadOnlyDictionary<string, string> Settings { get; } = new Dictionary<string, string> { ["OpenAlgoApi:HistorySpacingMs"] = "0", ["Brokers:RetryBackoffMs:flattrade"] = "0" };
}

/// <summary>Flattrade history (tools/contract-fixtures/history_flattrade.fixtures.json), run with the clock stopped at the recording.</summary>
public sealed class FlattradeHistoryContractTests(FlattradeHistoryHost host, ITestOutputHelper output) : IClassFixture<FlattradeHistoryHost>
{
    private const string Chart = "OpenAlgo's Flattrade plugin fails with an internal error when the chart answers with an error or with something that is not a list of bars. EP Trade bot says Flattrade did not send the candles.";
    private const string Midnight = "OpenAlgo stamps today's candle 5 hours 30 minutes after midnight India time (it adds the offset to a time already in India time), so the bar lands on the wrong day boundary. EP Trade bot stamps it at midnight India time, as the chart's own daily bars are.";
    private const long TodayBar = 1794508200;

    private static Deviation Fails(string reason, string message, int status = 500) => new(reason, f => Expect(f, status, Error(message)));

    public static readonly Dictionary<string, Deviation> Deviations = new()
    {
        ["history_intraday_answers_with_an_error"] = Fails(Chart, "Flattrade did not send the candles: Error Occurred : 5 \"no data\""),
        ["history_intraday_answers_with_a_bare_ok"] = Fails(Chart, "Flattrade did not send the candles: no reason was given."),
        ["history_daily_chart_answers_with_an_error"] = Fails(Chart, "Flattrade did not send the candles: Error Occurred : 5 \"no data\""),
        ["history_daily_chart_answers_with_an_error_today_in_range"] = Fails(Chart, "Flattrade did not send the candles: Error Occurred : 5 \"no data\""),
        ["history_daily_chart_fails_and_today_is_out_of_range"] = Fails(Chart, "Flattrade did not send the candles. Try again in a moment."),
        ["history_session_expired"] = Fails("OpenAlgo reports the expired session as an internal error. EP Trade bot tells the trader to log in to Flattrade again.", "Flattrade says the session has expired. Log in to Flattrade again.", 403),
        ["history_interval_flattrade_lacks"] = Fails(
            "OpenAlgo answers 500 with \"Unsupported interval '4h'\". EP Trade bot answers 400 and names what Flattrade offers.",
            "Flattrade does not offer 4h candles. Choose one of: 1m, 3m, 5m, 10m, 15m, 30m, 1h, 2h, D.", 400),
        ["history_daily_with_todays_candle_from_a_quote"] = new(Midnight, f => f["expected"]!["body"]!["data"]![2]!["timestamp"] = TodayBar),
        ["history_daily_chart_fails_so_the_quote_is_all_there_is"] = new(
            "OpenAlgo fails with an internal error when the daily chart cannot be reached, even though a quote is there to build today's bar from. EP Trade bot still builds it. " + Midnight,
            f => Expect(f, 200, new JsonObject
            {
                ["status"] = "success",
                ["data"] = new JsonArray(new JsonObject { ["timestamp"] = TodayBar, ["open"] = 802.45, ["high"] = 809.45, ["low"] = 800.45, ["close"] = 805.45, ["volume"] = 1234567.0, ["oi"] = 0.0 }),
            })),
        ["history_a_row_that_is_not_json_text"] = new(
            "OpenAlgo fails the whole request when one row is text that is not a bar. EP Trade bot leaves that row out and sends the rest.",
            f => Expect(f, 200, new JsonObject { ["status"] = "success", ["data"] = new JsonArray(new JsonObject { ["timestamp"] = 1793591100, ["open"] = 800.0, ["high"] = 802.5, ["low"] = 799.0, ["close"] = 801.25, ["volume"] = 120000.0, ["oi"] = 0.0 }) })),
        ["history_duplicate_timestamps_are_kept"] = new(
            "OpenAlgo passes two bars with one timestamp on, and a chart client rejects a series that repeats one. EP Trade bot keeps one bar per timestamp (the later).",
            f => f["expected"]!["body"]!["data"]!.AsArray().RemoveAt(0)),
    };

    public static IEnumerable<object[]> Names() => ContractRunner.Names("history_flattrade.fixtures.json");

    [PostgresTheory]
    [MemberData(nameof(Names))]
    public Task Matches_the_python_implementation(string name) => RunAsync(host, name, Deviations, output);
}

public sealed class TradesmartHost : ShoonyaBaseHost
{
    public override string BrokerName => "tradesmart";
    public override string FixtureFile => "tradesmart.fixtures.json";
    public override IReadOnlyDictionary<string, string> Keys { get; } = new Dictionary<string, string> { ["ApiKey"] = "FA12345:::tradesmart-api-key", ["ApiSecret"] = "tradesmart-secret" };
    public override IReadOnlyDictionary<string, string> HarnessReplies { get; } = new Dictionary<string, string>
    {
        ["POST /NorenWClientAPIv2/PositionBook"] = """{"stat":"Not_Ok","emsg":"no data"}""",
    };
}

/// <summary>TradeSmart (the Noren v2 API) against the Python implementation (tools/contract-fixtures/tradesmart.fixtures.json).</summary>
public sealed class TradesmartContractTests(TradesmartHost host, ITestOutputHelper output) : IClassFixture<TradesmartHost>
{
    public static readonly Dictionary<string, Deviation> Deviations = Build();

    private static Dictionary<string, Deviation> Build()
    {
        var d = ShoonyaContractTests.For("TradeSmart");
        d["placeorder_rate_limited_is_not_asked_again"] = new(
            "OpenAlgo answers success with no order id when TradeSmart turns an order away. EP Trade bot refuses with TradeSmart's reason (and never sends the order twice).",
            f => Expect(f, 500, Error("Invalid Input : Order Recieved 11 in a current second exceeds Limit 10 for user")));
        d["orderbook_garbled_average_price"] = new(
            "OpenAlgo fails the whole order book with an internal error when one order's average price is not a number. EP Trade bot shows that order at the price it was placed at and the rest of the book as usual.",
            f => Expect(f, 200, JsonNode.Parse("""{"status":"success","data":{"orders":[{"symbol":"SBIN","exchange":"NSE","action":"BUY","quantity":"10","price":"700.00","trigger_price":0,"pricetype":"LIMIT","product":"MIS","orderid":"2006","order_status":"open","timestamp":"10:20:06 03-11-2026"}],"statistics":{"total_buy_orders":1,"total_sell_orders":0,"total_completed_orders":0,"total_open_orders":1,"total_rejected_orders":0}}}""")!.AsObject()));
        return d;
    }

    public static IEnumerable<object[]> Names() => ContractRunner.Names("tradesmart.fixtures.json");

    [PostgresTheory]
    [MemberData(nameof(Names))]
    public Task Matches_the_python_implementation(string name) => RunAsync(host, name, Deviations, output);
}

public sealed class TradesmartDataHost : ShoonyaBaseHost
{
    public override string BrokerName => "tradesmart";
    public override string FixtureFile => "tradesmart_data.fixtures.json";
    public override IReadOnlyDictionary<string, string> Keys { get; } = new Dictionary<string, string> { ["ApiKey"] = "FA12345:::tradesmart-api-key", ["ApiSecret"] = "tradesmart-secret" };
}

/// <summary>TradeSmart quotes, depth and several quotes (tools/contract-fixtures/tradesmart_data.fixtures.json).</summary>
public sealed class TradesmartDataContractTests(TradesmartDataHost host, ITestOutputHelper output) : IClassFixture<TradesmartDataHost>
{
    public static readonly Dictionary<string, Deviation> Deviations = Build();

    private static Dictionary<string, Deviation> Build()
    {
        const string unchecked_ = "OpenAlgo's TradeSmart plugin takes whatever quote comes back, even another instrument's, so a trader could see a wrong price. EP Trade bot checks it is for the instrument asked about, as it does for Shoonya, and asks again.";
        var d = ShoonyaDataContractTests.For("TradeSmart");
        JsonNode Quotes() => BrokerContractHost.Load("tradesmart_data.fixtures.json")["fixtures"]!.AsArray().OfType<JsonObject>().Single(x => x["name"]!.GetValue<string>() == "quotes_equity")["expected"]!.DeepClone();
        d["quotes_other_instruments_answer_is_asked_again"] = new(unchecked_, f => f["expected"] = Quotes());
        d["multiquotes_other_instrument_every_time"] = new(unchecked_, f =>
        {
            var row = f["expected"]!["body"]!["results"]![0]!.AsObject();
            row.Remove("data");
            row["error"] = "TradeSmart sent the quote of another instrument (NSE|26000) on all 3 tries. Try again in a moment.";
        });
        return d;
    }

    public static IEnumerable<object[]> Names() => ContractRunner.Names("tradesmart_data.fixtures.json");

    [PostgresTheory]
    [MemberData(nameof(Names))]
    public Task Matches_the_python_implementation(string name) => RunAsync(host, name, Deviations, output);
}

public sealed class TradesmartHistoryHost : ShoonyaBaseHost
{
    public override string BrokerName => "tradesmart";
    public override string FixtureFile => "history_tradesmart.fixtures.json";
    public override IReadOnlyDictionary<string, string> Keys { get; } = new Dictionary<string, string> { ["ApiKey"] = "FA12345:::tradesmart-api-key", ["ApiSecret"] = "tradesmart-secret" };
    public override IReadOnlyDictionary<string, string> Settings { get; } = new Dictionary<string, string> { ["OpenAlgoApi:HistorySpacingMs"] = "0" };
}

/// <summary>TradeSmart history (tools/contract-fixtures/history_tradesmart.fixtures.json), run with the clock stopped at the recording.</summary>
public sealed class TradesmartHistoryContractTests(TradesmartHistoryHost host, ITestOutputHelper output) : IClassFixture<TradesmartHistoryHost>
{
    private const string Chart = "OpenAlgo's TradeSmart plugin fails with an internal error when the chart answers with something that is not a list of bars (and, for a bare success, with a parsing error). EP Trade bot says TradeSmart did not send the candles.";
    private const string Midnight = "OpenAlgo stamps today's candle 5 hours 30 minutes after midnight India time (it adds the offset to a time already in India time), so the bar lands on the wrong day boundary. EP Trade bot stamps it at midnight India time, as the chart's own daily bars are.";
    private const long TodayBar = 1794508200;

    private static Deviation Fails(string reason, string message, int status = 500) => new(reason, f => Expect(f, status, Error(message)));

    public static readonly Dictionary<string, Deviation> Deviations = new()
    {
        ["history_intraday_answers_with_a_bare_ok"] = Fails(Chart, "TradeSmart did not send the candles: no reason was given."),
        ["history_session_expired"] = Fails("OpenAlgo reports the expired session as an internal error. EP Trade bot tells the trader to log in to TradeSmart again.", "TradeSmart says the session has expired. Log in to TradeSmart again.", 403),
        ["history_interval_tradesmart_lacks"] = Fails(
            "OpenAlgo answers 500 with \"Unsupported interval '4h'\". EP Trade bot answers 400 and names what TradeSmart offers.",
            "TradeSmart does not offer 4h candles. Choose one of: 1m, 3m, 5m, 10m, 15m, 30m, 1h, 2h, D.", 400),
        ["history_daily_chart_fails_and_today_is_out_of_range"] = Fails(Chart, "TradeSmart did not send the candles. Try again in a moment."),
        ["history_daily_with_todays_candle_from_a_quote"] = new(Midnight, f => f["expected"]!["body"]!["data"]![2]!["timestamp"] = TodayBar),
        ["history_daily_chart_fails_so_the_quote_is_all_there_is"] = new(
            "OpenAlgo fails with an internal error when the daily chart cannot be reached, even though a quote is there to build today's bar from. EP Trade bot still builds it. " + Midnight,
            f => Expect(f, 200, new JsonObject
            {
                ["status"] = "success",
                ["data"] = new JsonArray(new JsonObject { ["timestamp"] = TodayBar, ["open"] = 802.45, ["high"] = 809.45, ["low"] = 800.45, ["close"] = 805.45, ["volume"] = 1234567.0, ["oi"] = 0.0 }),
            })),
        ["history_a_row_that_is_not_json_text"] = new(
            "OpenAlgo fails the whole request when one row is text that is not a bar. EP Trade bot leaves that row out and sends the rest.",
            f => Expect(f, 200, new JsonObject { ["status"] = "success", ["data"] = new JsonArray(new JsonObject { ["timestamp"] = 1793591100, ["open"] = 800.0, ["high"] = 802.5, ["low"] = 799.0, ["close"] = 801.25, ["volume"] = 120000.0, ["oi"] = 0.0 }) })),
    };

    public static IEnumerable<object[]> Names() => ContractRunner.Names("history_tradesmart.fixtures.json");

    [PostgresTheory]
    [MemberData(nameof(Names))]
    public Task Matches_the_python_implementation(string name) => RunAsync(host, name, Deviations, output);
}
