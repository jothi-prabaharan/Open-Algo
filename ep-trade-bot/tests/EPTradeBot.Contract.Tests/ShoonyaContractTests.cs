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
