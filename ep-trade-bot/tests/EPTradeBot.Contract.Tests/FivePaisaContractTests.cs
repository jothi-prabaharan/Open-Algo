using System.Text.Json.Nodes;
using EPTradeBot.IntegrationTests;
using Xunit.Abstractions;
using static EPTradeBot.Contract.Tests.ContractRunner;

namespace EPTradeBot.Contract.Tests;

public sealed class FivePaisaHost : BrokerContractHost
{
    public override string BrokerName => "fivepaisa";
    public override string FixtureFile => "fivepaisa.fixtures.json";
    public override bool NumbersMayBeText => true;
    public override IReadOnlyDictionary<string, string> Keys { get; } = new Dictionary<string, string> { ["ApiKey"] = "fp-app-key:::fp-user:::4001234", ["ApiSecret"] = "fp-secret" };
    public override IReadOnlyDictionary<(string, string), decimal> Prices { get; } = new Dictionary<(string, string), decimal>
    {
        [("NSE", "SBIN")] = 805m, [("NSE", "NOTICK")] = 100m, [("NSE", "INFY")] = 1500m, [("BSE", "TATAMOTORS")] = 500m, [("NFO", "NIFTY25NOV26FUT")] = 24000m, [("NFO", "NIFTY25NOV2624000CE")] = 100m,
        [("BFO", "SENSEX26NOVFUT")] = 80000m, [("MCX", "GOLDM25NOV26FUT")] = 75000m, [("CDS", "USDINR25NOV26FUT")] = 83.5m,
    };
    public override IReadOnlyDictionary<string, string> HarnessReplies { get; } = new Dictionary<string, string>
    {
        ["POST /VendorsAPI/Service1.svc/V2/NetPositionNetWise"] = """{"head":{"statusDescription":"Success","status":"0"},"body":{"Status":0,"NetPositionDetail":[]}}""",
    };
    public override IReadOnlyDictionary<string, string> Settings { get; } = new Dictionary<string, string> { ["Brokers:RetryBackoffMs:fivepaisa"] = "0", ["OpenAlgoApi:HistorySpacingMs"] = "0" };
}

/// <summary>5paisa against the Python implementation (tools/contract-fixtures/fivepaisa.fixtures.json).</summary>
public sealed class FivePaisaContractTests(FivePaisaHost host, ITestOutputHelper output) : IClassFixture<FivePaisaHost>
{
    private const string IdWords = "OpenAlgo answers with 5paisa's order number as a number. Every other broker's is text, so here it is text.";
    private const string Words = "OpenAlgo shows a failure as an empty list, an empty balance or a developer's sentence. EP Trade bot says what 5paisa did and what to do.";

    private static void IdAsText(JsonObject f)
    {
        if (f["expected"]!["body"] is JsonObject body && body["orderid"] is JsonValue id) body["orderid"] = id.ToString();
    }

    private static Deviation Says(int status, string message) => new(Words, f => Expect(f, status, Error(message)));

    private static Deviation Refusal(int status, string message) => new("OpenAlgo reports a refusal from 5paisa as done (its answer carries a failure in the head and a success status code). EP Trade bot reports the refusal.", f => Expect(f, status, Error(message)));

    public static readonly Dictionary<string, Deviation> Deviations = Build();

    private static Dictionary<string, Deviation> Build()
    {
        var map = new Dictionary<string, Deviation>
        {
            ["placeorder_unknown_symbol"] = new("OpenAlgo sends the order with no scrip code and reports it placed. The platform names the symbol and says what to do, and sends nothing.", f => { Expect(f, 400, Error("NOPE on NSE is not in the 5paisa master contract. Check the symbol, or download the master contract again.")); f["sent"]!.AsArray().Clear(); }),
            ["positionbook_failed_reads_as_flat"] = Says(500, "5paisa could not send the positions: no reason was given."),
            ["funds_no_margin_record"] = Says(500, "5paisa could not send the funds: No record found"),
            ["funds_http_error"] = Says(500, "5paisa could not send the funds. Try again in a moment."),
            ["placeorder_rejected_with_no_message"] = Says(400, "5paisa did not take the order and gave no reason. Check the order book on 5paisa."),
            ["placeorder_status_zero_with_no_order_id"] = Says(400, "5paisa did not take the order and gave no reason. Check the order book on 5paisa."),
            ["placeorder_http_error"] = new("OpenAlgo answers a failed call with a developer's sentence. EP Trade bot says the order may have been placed and to check the order book, because the call failed after it was sent.", f => Expect(f, 502, Error("5paisa could not confirm the order, so it may have been placed. Check the order book on 5paisa before trying again."))),
            ["modifyorder_refused"] = Refusal(400, "Order is not open"),
            ["modifyorder_refused_without_a_message"] = Refusal(400, "Failed to modify order"),
            ["cancelorder_refused"] = Refusal(400, "Already executed"),
            ["cancelorder_refused_without_a_message"] = Refusal(400, "Failed to cancel order"),
            ["cancelallorder"] = new("OpenAlgo counts every cancel as done (a refusal arrives with a success status code) and its order numbers are numbers. EP Trade bot counts only the cancels 5paisa accepted, with the numbers as text.",
                f =>
                {
                    var body = f["expected"]!["body"]!.AsObject();
                    body["canceled_orders"] = new JsonArray("1001", "1006");
                    body["failed_cancellations"] = new JsonArray("1002", "1009");
                    body["message"] = "Canceled 2 orders. Failed to cancel 2 orders.";
                }),
            ["placeorder_stop_market_with_no_tick_size"] = new("OpenAlgo fails with a developer's sentence. EP Trade bot says what is missing and what to do.", f => Expect(f, 400, Error("5paisa needs the tick size of NOTICK on NSE to place a stop-market order, and the master contract has none. Download the master contract again."))),
            ["placeorder_stop_market_with_a_trigger_too_low"] = new("OpenAlgo fails with a developer's sentence. EP Trade bot says what is wrong and what to do.", f => Expect(f, 400, Error("The trigger 0.05 for SBIN on NSE is too low to work out a stop-market limit price. Use a stop-limit order instead."))),
            ["holdings_empty"] = new("OpenAlgo fails with a developer's sentence when the account holds nothing. EP Trade bot shows an empty list of holdings.",
                f => Expect(f, 200, new JsonObject
                {
                    ["status"] = "success",
                    ["data"] = new JsonObject { ["holdings"] = new JsonArray(), ["statistics"] = new JsonObject { ["totalholdingvalue"] = 0.0, ["totalinvvalue"] = 0.0, ["totalprofitandloss"] = 0.0, ["totalpnlpercentage"] = 0.0 } },
                })),
            ["closeposition"] = new("OpenAlgo sends an order for a position whose scrip it does not know with no scrip code, and still reports every position squared off. EP Trade bot names the position it could not close.",
                f =>
                {
                    var sent = f["sent"]!.AsArray();
                    sent.RemoveAt(sent.Count - 1);
                    Expect(f, 500, Error("1 position(s) could not be squared off and are still open: x (N): x on N is not in the 5paisa master contract. Check the symbol, or download the master contract again."));
                }),
            ["orderbook_empty"] = new("OpenAlgo shows one blank order when 5paisa has none. EP Trade bot shows an empty book.", f => f["expected"]!["body"]!["data"]!["orders"] = new JsonArray()),
            ["placesmartorder_already_matched"] = new("OpenAlgo answers an already-matched smart order with a 500 whose message reads as a failure. EP Trade bot answers success and says the position already matches.", f => Expect(f, 200, new JsonObject { ["status"] = "success", ["message"] = "Positions Already Matched. No Action needed." })),
        };
        foreach (var name in ContractRunner.Names("fivepaisa.fixtures.json").Select(n => (string)n[0]).Where(n => n.StartsWith("placeorder", StringComparison.Ordinal) || n.StartsWith("placesmartorder", StringComparison.Ordinal)))
        {
            if (!map.ContainsKey(name)) map[name] = new Deviation(IdWords, IdAsText);
        }

        return map;
    }

    public static IEnumerable<object[]> Names() => ContractRunner.Names("fivepaisa.fixtures.json");

    [PostgresTheory]
    [MemberData(nameof(Names))]
    public Task Matches_the_python_implementation(string name) => RunAsync(host, name, Deviations, output);
}

public sealed class FivePaisaDataHost : BrokerContractHost
{
    public override string BrokerName => "fivepaisa";
    public override string FixtureFile => "fivepaisa_data.fixtures.json";
    public override bool NumbersMayBeText => true;
    public override IReadOnlyDictionary<string, string> Keys { get; } = new Dictionary<string, string> { ["ApiKey"] = "fp-app-key:::fp-user:::4001234", ["ApiSecret"] = "fp-secret" };
    public override IReadOnlyDictionary<(string, string), decimal> Prices { get; } = new Dictionary<(string, string), decimal>();
    public override IReadOnlyDictionary<string, string> HarnessReplies { get; } = new Dictionary<string, string>();
    public override IReadOnlyDictionary<string, string> Settings { get; } = new Dictionary<string, string> { ["Brokers:RetryBackoffMs:fivepaisa"] = "0", ["OpenAlgoApi:HistorySpacingMs"] = "0" };
}

/// <summary>5paisa quotes, depth and several quotes (tools/contract-fixtures/fivepaisa_data.fixtures.json).</summary>
public sealed class FivePaisaDataContractTests(FivePaisaDataHost host, ITestOutputHelper output) : IClassFixture<FivePaisaDataHost>
{
    private const string Words = "Same status. OpenAlgo words the failure for a developer (a library error, a call name). EP Trade bot says what 5paisa did and what to do.";

    private static Deviation Says(string message) => new(Words, f => Expect(f, 500, Error(message)));

    public static readonly Dictionary<string, Deviation> Deviations = new()
    {
        ["quotes_refused"] = Says("5paisa could not give the quote: Invalid scrip"),
        ["quotes_no_data"] = Says("5paisa sent no quote for SBIN on NSE."),
        ["quotes_http_error"] = Says("5paisa could not give the quote just now. Try again in a moment."),
        ["depth_snapshot_refused"] = Says("5paisa could not give the market depth: Invalid scrip"),
        ["depth_snapshot_has_no_data"] = Says("5paisa sent no data for SBIN on NSE."),
        ["depth_depth_refused"] = Says("5paisa could not give the market depth: No depth"),
        ["multiquotes_refused"] = Says("5paisa could not give the quotes: Bad request"),
        ["multiquotes_http_error"] = Says("5paisa could not give the quotes just now. Try again in a moment."),
    };

    public static IEnumerable<object[]> Names() => ContractRunner.Names("fivepaisa_data.fixtures.json");

    [PostgresTheory]
    [MemberData(nameof(Names))]
    public Task Matches_the_python_implementation(string name) => RunAsync(host, name, Deviations, output);
}

public sealed class FivePaisaHistoryHost : BrokerContractHost
{
    public override string BrokerName => "fivepaisa";
    public override string FixtureFile => "history_fivepaisa.fixtures.json";
    public override bool NumbersMayBeText => true;
    public override IReadOnlyDictionary<string, string> Keys { get; } = new Dictionary<string, string> { ["ApiKey"] = "fp-app-key:::fp-user:::4001234", ["ApiSecret"] = "fp-secret" };
    public override IReadOnlyDictionary<(string, string), decimal> Prices { get; } = new Dictionary<(string, string), decimal>();
    public override IReadOnlyDictionary<string, string> HarnessReplies { get; } = new Dictionary<string, string>();
    public override IReadOnlyDictionary<string, string> Settings { get; } = new Dictionary<string, string> { ["Brokers:RetryBackoffMs:fivepaisa"] = "0", ["OpenAlgoApi:HistorySpacingMs"] = "0" };
}

/// <summary>5paisa history (tools/contract-fixtures/history_fivepaisa.fixtures.json).</summary>
public sealed class FivePaisaHistoryContractTests(FivePaisaHistoryHost host, ITestOutputHelper output) : IClassFixture<FivePaisaHistoryHost>
{
    private const string Lacks = "OpenAlgo answers 500 with a developer's sentence. EP Trade bot answers 400, because the trader asked for something 5paisa does not offer.";

    public static readonly Dictionary<string, Deviation> Deviations = new()
    {
        ["history_interval_5paisa_lacks"] = new(Lacks, f => { Expect(f, 400, Error("Interval 3m is not supported by 5paisa. Use one of: 1m, 5m, 10m, 15m, 30m, 1h, D.")); f["sent"]!.AsArray().Clear(); }),
        ["history_weekly_is_not_offered"] = new(Lacks, f => { Expect(f, 400, Error("Interval W is not supported by 5paisa. Use one of: 1m, 5m, 10m, 15m, 30m, 1h, D.")); f["sent"]!.AsArray().Clear(); }),
    };

    public static IEnumerable<object[]> Names() => ContractRunner.Names("history_fivepaisa.fixtures.json");

    [PostgresTheory]
    [MemberData(nameof(Names))]
    public Task Matches_the_python_implementation(string name) => RunAsync(host, name, Deviations, output);
}
