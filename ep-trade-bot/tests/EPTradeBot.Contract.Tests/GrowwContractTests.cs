using System.Text.Json.Nodes;
using EPTradeBot.IntegrationTests;
using Xunit;
using Xunit.Abstractions;
using static EPTradeBot.Contract.Tests.ContractRunner;

namespace EPTradeBot.Contract.Tests;

public abstract class GrowwFileHost(string file) : BrokerContractHost
{
    public override string BrokerName => "groww";
    public override string FixtureFile => file;
    public override IReadOnlyDictionary<string, string> Keys { get; } = new Dictionary<string, string> { ["ApiKey"] = "groww-api-key", ["ApiSecret"] = "groww-api-secret" };
    public override IReadOnlyDictionary<(string, string), decimal> Prices { get; } = new Dictionary<(string, string), decimal>
    {
        [("NSE", "SBIN")] = 805m, [("NSE", "INFY")] = 1500m, [("BSE", "TATAMOTORS")] = 500m, [("NFO", "NIFTY25NOV26FUT")] = 24000m, [("BFO", "SENSEX26NOVFUT")] = 80000m, [("NFO", "NIFTY25NOV2624000CE")] = 100.5m,
    };
    public override IReadOnlyDictionary<string, string> VolatileFields { get; } = new Dictionary<string, string> { ["json.order_reference_id"] = @"^\d{8}-[0-9a-f]{8}$" };
    public override IReadOnlyDictionary<string, string> HarnessReplies { get; } = new Dictionary<string, string>
    {
        ["GET /v1/live-data/quote"] = """{"status":"SUCCESS","payload":{}}""",
        ["GET /v1/live-data/ltp"] = """{"status":"SUCCESS","payload":{}}""",
        ["GET /v1/positions/user"] = """{"status":"SUCCESS","payload":{"positions":[]}}""",
        ["GET /v1/order/list"] = """{"status":"SUCCESS","payload":{"order_list":[]}}""",
        ["POST /v1/order/cancel"] = """{"status":"SUCCESS","payload":{"order_status":"CANCELLATION_REQUESTED"}}""",
    };
    public override IReadOnlyDictionary<string, string> Settings { get; } = new Dictionary<string, string> { ["Brokers:RetryBackoffMs:groww"] = "0", ["Brokers:PacingMs:groww"] = "0", ["OpenAlgoApi:HistorySpacingMs"] = "0", ["Brokers:HistoryGapMs:groww"] = "0" };
}

public sealed class GrowwHost() : GrowwFileHost("groww.fixtures.json");

/// <summary>Groww orders, books, positions, holdings and funds (tools/contract-fixtures/groww.fixtures.json).</summary>
public sealed class GrowwContractTests(GrowwHost host, ITestOutputHelper output) : IClassFixture<GrowwHost>
{
    private const string Words = "OpenAlgo answers a failed read as an empty book, an unreadable answer to an order as a Python error, and a refused square-off as a success. EP Trade bot says what Groww did and what to do.";

    private static Deviation Says(int status, string message) => new(Words, f => Expect(f, status, Error(message)));

    private static readonly Deviation Matched = new("OpenAlgo answers an already-matched smart order with a 500 whose message reads as a failure. EP Trade bot answers success and says the position already matches.",
        f => Expect(f, 200, new JsonObject { ["status"] = "success", ["message"] = "Positions Already Matched. No Action needed." }));

    public static readonly Dictionary<string, Deviation> Deviations = new()
    {
        ["placesmartorder_cash_book_failed_is_refused"] = Says(503, "Your open position in SBIN could not be read from groww, so no order was placed. Try again in a moment."),
        ["placesmartorder_nothing_to_close"] = Matched,
        ["placesmartorder_already_matched"] = Matched,
        ["closeposition_book_failed"] = Says(500, "Your positions could not be read from groww, so nothing was closed. Try again in a moment."),
        ["openposition_cash_book_failed"] = Says(502, "Groww did not give the position book for the cash market: Down. Try again in a moment."),
        ["holdings_http_error"] = Says(500, "Groww did not give the holdings: HTTP 500. Try again in a moment."),
        ["placesmartorder_a_future"] = Matched,
        ["modifyorder_not_json"] = Says(502, "Groww sent an answer that could not be read, so the change may have been made. Check the order book on Groww before trying again."),
        ["closeposition_an_order_refused"] = Says(500, "1 position(s) could not be squared off and are still open: SBIN (NSE): No margin"),
        ["positionbook_not_json"] = Says(502, "Groww did not give the position book for the cash market: the answer could not be read. Try again in a moment."),
        ["holdings_not_json"] = Says(502, "Groww did not give the holdings: the answer could not be read. Try again in a moment."),
        ["placeorder_unknown_symbol"] = Says(400, "NOPE on NSE is not in the Groww master contract. Check the symbol, or download the master contract again."),
        ["cancelallorder_one_refused"] = new("OpenAlgo lists a refused cancel as an order id and a reason; EP Trade bot lists the order id.", f => Expect(f, 200, JsonNode.Parse("""{"status":"success","canceled_orders":["GM1","GM6","GF1"],"failed_cancellations":["GM4"],"message":"Canceled 3 orders. Failed to cancel 1 orders."}""")!.AsObject())),
        ["holdings_failure_status"] = Says(502, "Groww did not give the holdings: Bad. Try again in a moment."),
        ["closeposition_an_unlisted_symbol"] = Says(500, "1 position(s) could not be squared off and are still open: UNLISTED (NSE): There is no live price for UNLISTED yet, so the position limit cannot be checked. Try again once the price is showing."),
        ["cancelorder_not_json"] = Says(502, "Groww sent an answer that could not be read, so the order may have been cancelled. Check the order book on Groww."),
        ["placeorder_not_json"] = Says(502, "Groww sent an answer that could not be read, so the order may have been placed. Check the order book on Groww before trying again."),
    };

    public static IEnumerable<object[]> Names() => ContractRunner.Names("groww.fixtures.json");

    [PostgresTheory]
    [MemberData(nameof(Names))]
    public Task Matches_the_python_implementation(string name) => RunAsync(host, name, Deviations, output);
}

public sealed class MarginGrowwHost() : GrowwFileHost("margin_groww.fixtures.json");

/// <summary>Groww margin (tools/contract-fixtures/margin_groww.fixtures.json).</summary>
public sealed class MarginGrowwContractTests(MarginGrowwHost host, ITestOutputHelper output) : IClassFixture<MarginGrowwHost>
{
    private const string Words = "OpenAlgo answers a margin figure that is not a number as a failure to reach Groww, and a throttle as an empty reason. EP Trade bot says what Groww did and what to do.";

    private static Deviation Says(int status, string message) => new(Words, f => Expect(f, status, Error(message)));

    public static readonly Dictionary<string, Deviation> Deviations = new()
    {
        ["margin_unknown_symbol"] = Says(400, "NOPE on NFO is not in the Groww master contract, so the margin cannot be worked out. Check the symbol, or download the master contract again."),
        ["margin_a_leg_that_is_refused_spoils_the_basket"] = Says(400, "NOPE on NFO is not in the Groww master contract, so the margin cannot be worked out. Check the symbol, or download the master contract again."),
        ["margin_numbers_that_are_not_numbers"] = Says(502, "Groww sent margin figures that could not be read. Try again in a moment."),
    };

    public static IEnumerable<object[]> Names() => ContractRunner.Names("margin_groww.fixtures.json");

    [PostgresTheory]
    [MemberData(nameof(Names))]
    public Task Matches_the_python_implementation(string name) => RunAsync(host, name, Deviations, output);
}

public sealed class LoginGrowwHost() : GrowwFileHost("login_groww.fixtures.json");

/// <summary>Groww sign-in (tools/contract-fixtures/login_groww.fixtures.json).</summary>
public sealed class LoginGrowwContractTests(LoginGrowwHost host, ITestOutputHelper output) : IClassFixture<LoginGrowwHost>
{
    public static readonly Dictionary<string, Deviation> Deviations = new();

    public static IEnumerable<object[]> Names() => ContractRunner.Names("login_groww.fixtures.json");

    [PostgresTheory]
    [MemberData(nameof(Names))]
    public Task Matches_the_python_implementation(string name) => RunAsync(host, name, Deviations, output);
}

public sealed class DataGrowwHost() : GrowwFileHost("data_groww.fixtures.json");

/// <summary>Groww quotes, depth and several quotes (tools/contract-fixtures/data_groww.fixtures.json).</summary>
public sealed class DataGrowwContractTests(DataGrowwHost host, ITestOutputHelper output) : IClassFixture<DataGrowwHost>
{
    private const string Words = "OpenAlgo answers a depth it could not read as an empty one, a quote answer it cannot read as a Python error, and shows an error object beside a symbol. EP Trade bot says what Groww did and what to do.";

    private static Deviation Says(int status, string message) => new(Words, f => Expect(f, status, Error(message)));

    public static readonly Dictionary<string, Deviation> Deviations = new()
    {
        ["depth_server_error"] = Says(502, "Groww did not give the market depth for SBIN: it did not answer the request. Try again in a moment."),
        ["depth_refused"] = Says(502, "Groww did not give the market depth for SBIN: Invalid trading symbol. Try again in a moment."),
        ["depth_failure_status"] = Says(502, "Groww did not give the market depth for SBIN: x. Try again in a moment."),
        ["quotes_answer_is_not_json"] = Says(500, "Groww did not return a quote for SBIN: no response"),
        ["quotes_ohlc_null"] = new("OpenAlgo fails a quote whose open, high, low and close are null with a Python error. EP Trade bot gives the quote with those figures as zero.",
            f => Expect(f, 200, JsonNode.Parse("""{"status":"success","data":{"ltp":805.45,"open":0,"high":0,"low":0,"prev_close":0,"volume":1234567,"bid":805.4,"ask":805.5,"bid_qty":100,"ask_qty":110,"oi":0}}""")!.AsObject())),
        ["multiquotes_the_ohlc_answer_has_no_success"] = new("OpenAlgo puts Groww's error object beside the symbol; EP Trade bot puts its message.",
            f => Expect(f, 200, JsonNode.Parse("""{"status":"success","results":[{"symbol":"SBIN","exchange":"NSE","error":"x"}]}""")!.AsObject())),
    };

    public static IEnumerable<object[]> Names() => ContractRunner.Names("data_groww.fixtures.json");

    [PostgresTheory]
    [MemberData(nameof(Names))]
    public Task Matches_the_python_implementation(string name) => RunAsync(host, name, Deviations, output);
}

public sealed class HistoryGrowwHost() : GrowwFileHost("history_groww.fixtures.json");

/// <summary>Groww history (tools/contract-fixtures/history_groww.fixtures.json).</summary>
public sealed class HistoryGrowwContractTests(HistoryGrowwHost host, ITestOutputHelper output) : IClassFixture<HistoryGrowwHost>
{
    public static readonly Dictionary<string, Deviation> Deviations = new()
    {
        ["history_answer_is_not_json"] = new("OpenAlgo names the status of an answer it could not read; EP Trade bot says the answer could not be read.", f => Expect(f, 500, Error("Groww did not return history for SBIN: the answer could not be read"))),
    };

    public static IEnumerable<object[]> Names() => ContractRunner.Names("history_groww.fixtures.json");

    [PostgresTheory]
    [MemberData(nameof(Names))]
    public Task Matches_the_python_implementation(string name) => RunAsync(host, name, Deviations, output);
}
