using System.Text.Json.Nodes;
using EPTradeBot.IntegrationTests;
using Xunit.Abstractions;
using static EPTradeBot.Contract.Tests.ContractRunner;

namespace EPTradeBot.Contract.Tests;

public sealed class HdfcSkyHost : BrokerContractHost
{
    public override string BrokerName => "hdfcsky";
    public override string FixtureFile => "hdfcsky.fixtures.json";
    public override bool NumbersMayBeText => true;
    public override IReadOnlyDictionary<string, string> Keys { get; } = new Dictionary<string, string> { ["ApiKey"] = "sky-api-key", ["ApiSecret"] = "sky-api-secret" };
    public override IReadOnlyDictionary<(string, string), decimal> Prices { get; } = new Dictionary<(string, string), decimal>
    {
        [("NSE", "SBIN")] = 805m, [("NSE", "INFY")] = 1500m, [("BSE", "TATAMOTORS")] = 500m, [("NFO", "NIFTY25NOV26FUT")] = 24000m, [("NFO", "NIFTY25NOV2624000CE")] = 100m, [("NFO", "NIFTY25NOV2624000PE")] = 100m,
        [("NFO", "RELIANCE25NOV26FUT")] = 2900m, [("BFO", "SENSEX26NOVFUT")] = 80000m, [("MCX", "GOLDM25NOV26FUT")] = 75000m, [("CDS", "USDINR25NOV26FUT")] = 83.5m,
    };
    public override IReadOnlyDictionary<string, string> HarnessReplies { get; } = new Dictionary<string, string> { ["GET /oapi/v1/positions"] = """{"status":"success","data":[]}""" };
    public override IReadOnlyDictionary<string, string> VolatileFields { get; } = new Dictionary<string, string> { ["json.user_order_id"] = @"^\d{1,9}$" };
    public override IReadOnlyDictionary<string, string> Settings { get; } = new Dictionary<string, string> { ["Brokers:RetryBackoffMs:hdfcsky"] = "0" };
}

/// <summary>HDFC Sky against the Python implementation (tools/contract-fixtures/hdfcsky.fixtures.json).</summary>
public sealed class HdfcSkyContractTests(HdfcSkyHost host, ITestOutputHelper output) : IClassFixture<HdfcSkyHost>
{
    private const string Empty = "OpenAlgo shows an empty book (or an empty balance) when HDFC Sky answers with an expired-session or error body, so a trader reads a dead session as no trades, no positions and no money. EP Trade bot reports the refusal.";

    private static Deviation Refused(string what, string why) => new(Empty, f => Expect(f, 500, Error($"HDFC Sky could not send the {what}: {why}")));

    /// <summary>OpenAlgo's cancel-all reads only the pending half of the book; EP Trade bot reads the shared order book, which is both halves (a second read, for the completed orders).</summary>
    private static readonly Deviation BothHalves = new("OpenAlgo's cancel-all reads only the pending half of the order book. EP Trade bot's shared cancel-all reads the whole book (pending, then completed) and cancels the cancellable orders in it, so the same orders are cancelled with one more read.",
        f =>
        {
            var sent = f["sent"]!.AsArray();
            var second = (JsonObject)sent[0]!.DeepClone();
            foreach (var pair in second["query"]!.AsArray()) if (pair![0]!.GetValue<string>() == "type") pair[1] = "completed";
            sent.Insert(1, second);
        });

    public static readonly Dictionary<string, Deviation> Deviations = new()
    {
        ["tradebook_expired_session"] = Refused("trade book", "invalid credentials"),
        ["positionbook_expired_session"] = Refused("positions", "invalid credentials"),
        ["holdings_expired_session"] = Refused("holdings", "invalid credentials"),
        ["funds_expired_session"] = Refused("funds", "invalid credentials"),
        ["funds_error_status"] = Refused("funds", "x"),
        ["placeorder_success_without_an_order_id"] = new("OpenAlgo answers success with no order id when HDFC Sky says success without one. EP Trade bot refuses, so a trader is never told an order was placed that cannot be found.", f => Expect(f, 400, Error("Failed to place order"))),
        ["placeorder_unknown_symbol"] = new("OpenAlgo answers 500 with an internal error. The platform names the symbol and says what to do.", f => { Expect(f, 400, Error("NOPE on NSE is not in the HDFC Sky master contract. Check the symbol, or download the master contract again.")); f["sent"]!.AsArray().Clear(); }),
        ["placeorder_without_a_strategy_tag"] = new("OpenAlgo accepts an order with an empty strategy name. EP Trade bot requires one on every order, so each order can be traced to what sent it; nothing reaches the broker.", Status: 400),
        ["closeposition_an_order_is_refused"] = new("OpenAlgo reports all positions squared off even when the broker refused the orders. EP Trade bot names the positions that are still open.", f => Expect(f, 500, Error("2 position(s) could not be squared off and are still open: SBIN (NSE): Insufficient funds; NIFTY25NOV26FUT (NFO): Insufficient funds"))),
        ["cancelallorder"] = BothHalves,
        ["cancelallorder_book_empty"] = BothHalves,
        ["cancelallorder_book_error"] = BothHalves,
        ["cancelallorder_only_cancellable_statuses"] = BothHalves,
    };

    public static IEnumerable<object[]> Names() => ContractRunner.Names("hdfcsky.fixtures.json");

    [PostgresTheory]
    [MemberData(nameof(Names))]
    public Task Matches_the_python_implementation(string name) => RunAsync(host, name, Deviations, output);
}

public sealed class HdfcSkyDataHost : BrokerContractHost
{
    public override string BrokerName => "hdfcsky";
    public override string FixtureFile => "hdfcsky_data.fixtures.json";
    public override bool NumbersMayBeText => true;
    public override IReadOnlyDictionary<string, string> Keys { get; } = new Dictionary<string, string> { ["ApiKey"] = "sky-api-key", ["ApiSecret"] = "sky-api-secret" };
    public override IReadOnlyDictionary<(string, string), decimal> Prices { get; } = new Dictionary<(string, string), decimal>();
    public override IReadOnlyDictionary<string, string> HarnessReplies { get; } = new Dictionary<string, string>();
    public override IReadOnlyDictionary<string, string> Settings { get; } = new Dictionary<string, string> { ["Brokers:RetryBackoffMs:hdfcsky"] = "0" };
}

/// <summary>HDFC Sky quotes, depth and several quotes (tools/contract-fixtures/hdfcsky_data.fixtures.json), with the clock stopped at the recording. Both sides leave the feed-only fields at zero.</summary>
public sealed class HdfcSkyDataContractTests(HdfcSkyDataHost host, ITestOutputHelper output) : IClassFixture<HdfcSkyDataHost>
{
    public static readonly Dictionary<string, Deviation> Deviations = new();

    public static IEnumerable<object[]> Names() => ContractRunner.Names("hdfcsky_data.fixtures.json");

    [PostgresTheory]
    [MemberData(nameof(Names))]
    public Task Matches_the_python_implementation(string name) => RunAsync(host, name, Deviations, output);
}

public sealed class HdfcSkyHistoryHost : BrokerContractHost
{
    public override string BrokerName => "hdfcsky";
    public override string FixtureFile => "history_hdfcsky.fixtures.json";
    public override bool NumbersMayBeText => true;
    public override IReadOnlyDictionary<string, string> Keys { get; } = new Dictionary<string, string> { ["ApiKey"] = "sky-api-key", ["ApiSecret"] = "sky-api-secret" };
    public override IReadOnlyDictionary<(string, string), decimal> Prices { get; } = new Dictionary<(string, string), decimal>();
    public override IReadOnlyDictionary<string, string> HarnessReplies { get; } = new Dictionary<string, string>();
    public override IReadOnlyDictionary<string, string> Settings { get; } = new Dictionary<string, string> { ["OpenAlgoApi:HistorySpacingMs"] = "0", ["Brokers:RetryBackoffMs:hdfcsky"] = "0", ["Brokers:HistoryGapMs:hdfcsky"] = "0" };
}

/// <summary>HDFC Sky history (tools/contract-fixtures/history_hdfcsky.fixtures.json), with the clock stopped at the recording.</summary>
public sealed class HdfcSkyHistoryContractTests(HdfcSkyHistoryHost host, ITestOutputHelper output) : IClassFixture<HdfcSkyHistoryHost>
{
    private const string NoCode = "Same status; OpenAlgo puts the chart service's HTTP status or its internals in the sentence, which a trader cannot act on. EP Trade bot says to try again in a moment.";

    private static Deviation Down(string symbol) => new(NoCode, f => Expect(f, 500, Error($"HDFC Sky could not return candles for {symbol} right now. Try again in a moment.")));

    public static readonly Dictionary<string, Deviation> Deviations = new()
    {
        ["history_every_chunk_failing_is_an_error"] = Down("NSE:SBIN"),
        ["history_a_chart_refusal_with_a_message"] = Down("NSE:SBIN"),
        ["history_a_chart_error_code_with_a_message"] = Down("NSE:SBIN"),
        ["history_a_chart_error_code_without_a_message"] = Down("NSE:SBIN"),
        ["history_rate_limited_every_time"] = Down("NSE:SBIN"),
        ["history_interval_hdfc_sky_lacks"] = new("OpenAlgo answers 500 with the same sentence. EP Trade bot answers 400, because the trader asked for something HDFC Sky does not offer.", f => { Expect(f, 400, Error("Interval 2m is not supported by HDFC Sky. Use one of: 1m, 3m, 5m, 10m, 15m, 30m, 1h, D, W, M.")); f["sent"]!.AsArray().Clear(); }),
    };

    public static IEnumerable<object[]> Names() => ContractRunner.Names("history_hdfcsky.fixtures.json");

    [PostgresTheory]
    [MemberData(nameof(Names))]
    public Task Matches_the_python_implementation(string name) => RunAsync(host, name, Deviations, output);
}

public sealed class HdfcSkyMarginHost : BrokerContractHost
{
    public override string BrokerName => "hdfcsky";
    public override string FixtureFile => "margin_hdfcsky.fixtures.json";
    public override bool NumbersMayBeText => true;
    public override IReadOnlyDictionary<string, string> Keys { get; } = new Dictionary<string, string> { ["ApiKey"] = "sky-api-key", ["ApiSecret"] = "sky-api-secret" };
    public override IReadOnlyDictionary<(string, string), decimal> Prices { get; } = new Dictionary<(string, string), decimal>();
    public override IReadOnlyDictionary<string, string> HarnessReplies { get; } = new Dictionary<string, string>();
    public override IReadOnlyDictionary<string, string> Settings { get; } = new Dictionary<string, string> { ["Brokers:RetryBackoffMs:hdfcsky"] = "0" };
}

/// <summary>HDFC Sky margin (tools/contract-fixtures/margin_hdfcsky.fixtures.json).</summary>
public sealed class HdfcSkyMarginContractTests(HdfcSkyMarginHost host, ITestOutputHelper output) : IClassFixture<HdfcSkyMarginHost>
{
    private static string Unknown(string symbol) => $"{symbol} on NSE is not in the HDFC Sky master contract, so the margin cannot be worked out. Check the symbol, or download the master contract again.";

    public static readonly Dictionary<string, Deviation> Deviations = new()
    {
        ["margin_unknown_symbol_alone"] = new("Same status; EP Trade bot names the symbol, where OpenAlgo says no symbol was valid.", f => Expect(f, 400, Error(Unknown("NOSUCH")))),
        ["margin_unknown_symbol_among_others"] = new(
            "OpenAlgo leaves the position whose symbol it cannot find out of the calculation and works out the rest, which understates the margin. EP Trade bot refuses and names the symbol.",
            f =>
            {
                Expect(f, 400, Error(Unknown("NOSUCH")));
                f["sent"]!.AsArray().Clear();
            }),
    };

    public static IEnumerable<object[]> Names() => ContractRunner.Names("margin_hdfcsky.fixtures.json");

    [PostgresTheory]
    [MemberData(nameof(Names))]
    public Task Matches_the_python_implementation(string name) => RunAsync(host, name, Deviations, output);
}
