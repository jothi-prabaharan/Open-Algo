using EPTradeBot.IntegrationTests;
using Xunit.Abstractions;
using static EPTradeBot.Contract.Tests.ContractRunner;

namespace EPTradeBot.Contract.Tests;

public sealed class ArrowHost : BrokerContractHost
{
    public override string BrokerName => "arrow";
    public override string FixtureFile => "arrow.fixtures.json";
    public override bool NumbersMayBeText => true;
    public override IReadOnlyDictionary<string, string> Keys { get; } = new Dictionary<string, string> { ["ApiKey"] = "arrow-app-id", ["ApiSecret"] = "arrow-api-secret" };
    public override IReadOnlyDictionary<(string, string), decimal> Prices { get; } = new Dictionary<(string, string), decimal>
    {
        [("NSE", "SBIN")] = 805m, [("NSE", "INFY")] = 1500m, [("NFO", "NIFTY25NOV26FUT")] = 24000m, [("MCX", "GOLDM25NOV26FUT")] = 75000m,
    };
    public override IReadOnlyDictionary<string, string> HarnessReplies { get; } = new Dictionary<string, string>
    {
        ["GET /user/positions"] = """{"status":"success","data":[]}""",
    };
}

/// <summary>Arrow against the Python implementation (tools/contract-fixtures/arrow.fixtures.json).</summary>
public sealed class ArrowContractTests(ArrowHost host, ITestOutputHelper output) : IClassFixture<ArrowHost>
{
    private const string Refusal = "Same status; EP Trade bot words the refusal for the trader and names which call Arrow refused, where OpenAlgo shows the HTTP library's own sentence with the address in it.";

    private static Deviation Words(string message, int status = 500) => new(Refusal, f => Expect(f, status, Error(message)));

    public static readonly Dictionary<string, Deviation> Deviations = new()
    {
        ["orderbook_refused"] = Words("Arrow could not send the order book: bad token"),
        ["tradebook_refused"] = Words("Arrow could not send the trade book: bad token"),
        ["positionbook_refused"] = Words("Arrow could not send the positions: bad token"),
        ["holdings_refused"] = Words("Arrow could not send the holdings: bad token"),
        ["cancelorder_refused_without_a_message"] = Words("Failed to cancel order: Arrow did not accept the cancel."),
        ["placeorder_unknown_symbol"] = new("Same status; the platform's own words for a symbol missing from the master contract. OpenAlgo sends the order with no symbol.",
            f =>
            {
                Expect(f, 400, Error("NOPE on NSE is not in the Arrow master contract. Check the symbol, or download the master contract again."));
                f["sent"]!.AsArray().Clear();
            }),
        ["placeorder_refused_with_an_ok_status"] = new("OpenAlgo answers success with no order id when Arrow refuses an order but answers with an OK status. EP Trade bot refuses, so a trader is never told an order was placed that does not exist.",
            f => Expect(f, 400, Error("Rejected"))),
        ["placeorder_success_without_an_order_number"] = new("OpenAlgo answers success with no order id when Arrow says success without an order number. EP Trade bot refuses, so a trader is never told an order was placed that cannot be found.",
            f => Expect(f, 400, Error("Failed to place order"))),
        ["placeorder_error_status_with_an_order_number_is_not_an_order"] = new("OpenAlgo answers success with no order id when Arrow answers an error with an OK status. EP Trade bot refuses.", f => Expect(f, 400, Error("Rejected"))),
        ["funds_refused"] = new("OpenAlgo shows an empty balance when Arrow refuses the funds call. EP Trade bot reports the refusal, so an empty balance is never mistaken for a real one.", Status: 500),
        ["funds_error_status_in_an_ok_answer"] = new("OpenAlgo shows an empty balance when Arrow answers the funds call with an error status in the body. EP Trade bot reports the refusal.", Status: 500),
    };

    public static IEnumerable<object[]> Names() => ContractRunner.Names("arrow.fixtures.json");

    [PostgresTheory]
    [MemberData(nameof(Names))]
    public Task Matches_the_python_implementation(string name) => RunAsync(host, name, Deviations, output);
}

public sealed class ArrowDataHost : BrokerContractHost
{
    public override string BrokerName => "arrow";
    public override string FixtureFile => "arrow_data.fixtures.json";
    public override bool NumbersMayBeText => true;
    public override IReadOnlyDictionary<string, string> Keys { get; } = new Dictionary<string, string> { ["ApiKey"] = "arrow-app-id", ["ApiSecret"] = "arrow-api-secret" };
    public override IReadOnlyDictionary<(string, string), decimal> Prices { get; } = new Dictionary<(string, string), decimal>();
    public override IReadOnlyDictionary<string, string> HarnessReplies { get; } = new Dictionary<string, string>();
    public override IReadOnlyDictionary<string, string> Settings { get; } = new Dictionary<string, string> { ["OpenAlgoApi:HistorySpacingMs"] = "0" };
}

/// <summary>Arrow quotes, depth, several quotes and history (tools/contract-fixtures/arrow_data.fixtures.json).</summary>
public sealed class ArrowDataContractTests(ArrowDataHost host, ITestOutputHelper output) : IClassFixture<ArrowDataHost>
{
    private const string Refusal = "Same status; EP Trade bot words the refusal for the trader and names which call Arrow refused, where OpenAlgo shows the HTTP library's own sentence with the address in it.";
    private const string Plain = "Same status; the words are plain, where OpenAlgo's carry a Python list and a remark about the official SDK.";

    private static Deviation Words(string reason, string message, int status = 500) => new(reason, f => Expect(f, status, Error(message)));

    public static readonly Dictionary<string, Deviation> Deviations = new()
    {
        ["quotes_refused"] = Words(Refusal, "Arrow could not send the quote: bad token"),
        ["quotes_index_a_different_error_stops_the_search"] = Words(Refusal, "Arrow could not send the quote: down"),
        ["depth_refused"] = Words(Refusal, "Arrow could not send the depth: bad token"),
        ["history_refused"] = Words(Refusal, "Arrow could not send the candles: bad token"),
        ["history_a_later_chunk_fails"] = Words(Refusal, "Arrow could not send the candles: down"),
        ["quotes_index_every_name_refused"] = Words(Plain, "Error fetching quotes: Arrow's quote API did not accept any name for the index NIFTYMEDIA, so its quote cannot be read. The live feed still works."),
        ["quotes_exchange_arrow_does_not_serve"] = Words(Plain, "Error fetching quotes: Arrow's quote API does not serve the CDS exchange. Use the live feed for prices on this exchange."),
        ["quotes_nse_commodities_arrow_does_not_serve"] = Words(Plain, "Error fetching quotes: Arrow's quote API does not serve the NCO exchange. Use the live feed for prices on this exchange."),
        ["depth_unsupported_exchange"] = Words(Plain, "Error fetching market depth: Arrow's quote API does not serve the CDS exchange. Use the live feed for prices on this exchange."),
        ["history_interval_arrow_lacks"] = Words("OpenAlgo answers 500 with \"Unsupported timeframe: 20m\". EP Trade bot answers 400 and names what Arrow offers.", "Arrow does not offer 20m candles. Choose one of: 1m, 3m, 5m, 10m, 15m, 30m, 1h, 2h, 3h, 4h, D, W, M.", 400),
    };

    public static IEnumerable<object[]> Names() => ContractRunner.Names("arrow_data.fixtures.json");

    [PostgresTheory]
    [MemberData(nameof(Names))]
    public Task Matches_the_python_implementation(string name) => RunAsync(host, name, Deviations, output);
}
