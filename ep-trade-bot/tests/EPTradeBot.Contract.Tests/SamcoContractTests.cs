using System.Text.Json.Nodes;
using EPTradeBot.IntegrationTests;
using Xunit.Abstractions;
using static EPTradeBot.Contract.Tests.ContractRunner;

namespace EPTradeBot.Contract.Tests;

public sealed class SamcoHost : BrokerContractHost
{
    public override string BrokerName => "samco";
    public override string FixtureFile => "samco.fixtures.json";
    public override bool NumbersMayBeText => true;
    public override IReadOnlyDictionary<string, string> Keys { get; } = new Dictionary<string, string> { ["ApiKey"] = "samco-api-key", ["ApiSecret"] = "samco-api-secret" };
    public override IReadOnlyDictionary<(string, string), decimal> Prices { get; } = new Dictionary<(string, string), decimal>
    {
        [("NSE", "SBIN")] = 805m, [("NSE", "INFY")] = 1500m, [("BSE", "TATAMOTORS")] = 500m, [("NFO", "NIFTY25NOV26FUT")] = 24000m, [("NFO", "NIFTY25NOV2624000CE")] = 100m,
        [("BFO", "SENSEX26NOVFUT")] = 80000m, [("MCX", "GOLDM25NOV26FUT")] = 75000m, [("CDS", "USDINR25NOV26FUT")] = 83.5m,
    };
    public override IReadOnlyDictionary<string, string> HarnessReplies { get; } = new Dictionary<string, string> { ["GET /position/getPositions"] = """{"status":"Success","positionDetails":[]}""" };
    public override IReadOnlyDictionary<string, string> Settings { get; } = new Dictionary<string, string> { ["Brokers:RetryBackoffMs:samco"] = "0" };
}

/// <summary>Samco against the Python implementation (tools/contract-fixtures/samco.fixtures.json).</summary>
public sealed class SamcoContractTests(SamcoHost host, ITestOutputHelper output) : IClassFixture<SamcoHost>
{
    private const string Unpriced = "Samco could not give the last price of SBIN, so the market order was not sent: ";
    private const string Plain = "OpenAlgo words this refusal for a developer. EP Trade bot says what happened and what to do.";

    private static void AddCancel(JsonObject f, string orderNumber)
    {
        var sent = f["sent"]!.AsArray();
        var copy = (JsonObject)sent.Last(x => x!["method"]!.GetValue<string>() == "DELETE")!.DeepClone();
        copy["query"] = new JsonArray(new JsonArray("orderNumber", orderNumber));
        sent.Add(copy);
    }

    public static readonly Dictionary<string, Deviation> Deviations = new()
    {
        ["placeorder_market_without_a_price_is_refused"] = new(Plain, f => Expect(f, 400, Error("Samco shows no last price for SBIN, so the market order was not sent. Place a limit order instead."))),
        ["placeorder_market_when_the_quote_fails_is_refused"] = new(Plain, f => Expect(f, 400, Error(Unpriced + "Samco could not give the quote: no quote"))),
        ["placeorder_market_with_no_quote_details_is_refused"] = new(Plain, f => Expect(f, 400, Error(Unpriced + "Samco sent no quote for SBIN."))),
        ["modifyorder_market_with_a_failed_quote"] = new(Plain, f => Expect(f, 400, Error(Unpriced + "Samco could not give the quote: no quote"))),
        ["placeorder_stop_market_without_a_trigger_is_refused"] = new(Plain, f => Expect(f, 400, Error("A stop-market order for SBIN needs a trigger price, so it was not sent."))),
        ["placeorder_broker_refuses"] = new("OpenAlgo answers a refused order with a generic sentence and drops Samco's reason. EP Trade bot passes Samco's own words on.", f => Expect(f, 400, Error("Insufficient funds"))),
        ["placeorder_success_without_an_order_number"] = new("OpenAlgo reports success with no order id. EP Trade bot refuses and tells the trader to check the order book, because an order with no number cannot be followed or cancelled.",
            f => Expect(f, 502, Error("Samco accepted the order but sent no order number, so it cannot be followed. Check the order book on Samco before trying again."))),
        ["placeorder_unknown_symbol"] = new("OpenAlgo sends the order with no symbol and lets Samco refuse it. The platform names the symbol and says what to do, and sends nothing.",
            f => { Expect(f, 400, Error("NOPE on NSE is not in the Samco master contract. Check the symbol, or download the master contract again.")); f["sent"]!.AsArray().Clear(); }),
        ["orderbook_refused"] = new("OpenAlgo shows an empty order book when Samco refuses the read. EP Trade bot reports the refusal, so a dead session is never read as no orders.", f => Expect(f, 500, Error("Samco could not send the order book: bad"))),
        ["holdings_failed"] = new("OpenAlgo shows no holdings when Samco refuses the read. EP Trade bot reports the refusal.", f => Expect(f, 500, Error("Samco could not send the holdings: no"))),
        ["positionbook_failed"] = new("OpenAlgo shows no positions when Samco refuses the read. EP Trade bot reports the refusal.", f => Expect(f, 500, Error("Samco could not send the DAY positions: no"))),
        ["funds_failed"] = new("OpenAlgo shows empty funds when Samco refuses the read. EP Trade bot reports the refusal.", f => Expect(f, 500, Error("Samco could not send the funds: Invalid session"))),
        ["cancelallorder_book_error"] = new("OpenAlgo cancels nothing and reports success when the order book cannot be read. EP Trade bot reports the failure.", f => Expect(f, 500, Error("Failed to cancel all orders due to internal error"))),
        ["cancelallorder"] = new("OpenAlgo cancels only orders whose status is open, pending or trigger pending. EP Trade bot's shared cancel-all cancels every order the book shows as open, which includes an after-market order Samco has received, so one more cancel is tried.",
            f => { AddCancel(f, "S7"); var body = f["expected"]!["body"]!.AsObject(); body["canceled_orders"]!.AsArray().Add("S7"); body["message"] = "Canceled 3 orders. Failed to cancel 1 orders."; }),
        ["closeposition_merges_the_day_and_net_books"] = new("OpenAlgo asks Samco for a price of a position whose symbol it does not know and sends nothing for it, but still reports success. EP Trade bot names the position that is still open.",
            f =>
            {
                var sent = f["sent"]!.AsArray();
                sent.RemoveAt(sent.ToList().FindIndex(x => x!["path"]!.GetValue<string>() == "/quote/getQuote" && x["query"]![0]![1]!.GetValue<string>() == "None"));
                Expect(f, 500, Error("1 position(s) could not be squared off and are still open: NOSUCH (NSE): There is no live price for NOSUCH yet, so the position limit cannot be checked. Try again once the price is showing."));
            }),
        ["closeposition_the_net_book_cannot_be_read"] = new("Same status; EP Trade bot says which positions it could not read and that nothing was closed.", f => Expect(f, 500, Error("Your positions could not be read from samco, so nothing was closed. Try again in a moment."))),
        ["closeposition_the_day_book_cannot_be_read"] = new("OpenAlgo reads the second book even after the first failed. EP Trade bot stops at the first failure, because nothing is closed either way.",
            f => { f["sent"]!.AsArray().RemoveAt(1); Expect(f, 500, Error("Your positions could not be read from samco, so nothing was closed. Try again in a moment.")); }),
        ["placesmartorder_already_matched"] = new("OpenAlgo answers an already-matched smart order with a 500 whose message reads as a failure. EP Trade bot answers success and says the position already matches.", f => Expect(f, 200, new JsonObject { ["status"] = "success", ["message"] = "Positions Already Matched. No Action needed." })),
    };

    public static IEnumerable<object[]> Names() => ContractRunner.Names("samco.fixtures.json");

    [PostgresTheory]
    [MemberData(nameof(Names))]
    public Task Matches_the_python_implementation(string name) => RunAsync(host, name, Deviations, output);
}

public sealed class SamcoDataHost : BrokerContractHost
{
    public override string BrokerName => "samco";
    public override string FixtureFile => "samco_data.fixtures.json";
    public override bool NumbersMayBeText => true;
    public override IReadOnlyDictionary<string, string> Keys { get; } = new Dictionary<string, string> { ["ApiKey"] = "samco-api-key", ["ApiSecret"] = "samco-api-secret" };
    public override IReadOnlyDictionary<(string, string), decimal> Prices { get; } = new Dictionary<(string, string), decimal>();
    public override IReadOnlyDictionary<string, string> HarnessReplies { get; } = new Dictionary<string, string>();
    public override IReadOnlyDictionary<string, string> Settings { get; } = new Dictionary<string, string> { ["Brokers:RetryBackoffMs:samco"] = "0", ["OpenAlgoApi:HistorySpacingMs"] = "0" };
}

/// <summary>Samco quotes, depth and several quotes (tools/contract-fixtures/samco_data.fixtures.json).</summary>
public sealed class SamcoDataContractTests(SamcoDataHost host, ITestOutputHelper output) : IClassFixture<SamcoDataHost>
{
    private const string Words = "Same status. OpenAlgo words the failure for a developer (a library error, a call name). EP Trade bot says what Samco did and what to do.";

    private static Deviation Says(string message) => new(Words, f => Expect(f, 500, Error(message)));

    private const string Busy = "Samco is limiting how often it can be asked. Wait a moment, then try again.";
    private const string Down = "Samco could not answer just now. Try again in a few minutes.";

    public static readonly Dictionary<string, Deviation> Deviations = new()
    {
        ["quotes_refused"] = Says("Samco could not give the quote: Symbol not found"),
        ["quotes_refused_without_a_message"] = Says("Samco could not give the quote: no reason was given."),
        ["quotes_without_details"] = Says("Samco sent no quote for SBIN."),
        ["quotes_session_refused"] = Says("Samco did not accept the session. Sign in to Samco again, then try again."),
        ["quotes_rate_limited_every_time"] = Says(Busy),
        ["quotes_server_error_every_time"] = Says(Down),
        ["quotes_index_without_details"] = Says("Samco sent no index data for NIFTY."),
        ["quotes_index_refused"] = Says("Samco could not give the index quote: Index not found"),
        ["depth_refused"] = Says("Samco could not give the market depth: No depth"),
        ["depth_without_details"] = Says("Samco sent no market depth for SBIN."),
        ["multiquotes_refused"] = Says("Samco could not give the quotes: Bad request"),
        ["multiquotes_server_error_every_time"] = Says(Down),
        ["multiquotes_an_index_that_fails"] = new(Words, f => f["expected"]!["body"]!["results"]![1]!["error"] = "Samco could not give the index quote: Index not found"),
    };

    public static IEnumerable<object[]> Names() => ContractRunner.Names("samco_data.fixtures.json");

    [PostgresTheory]
    [MemberData(nameof(Names))]
    public Task Matches_the_python_implementation(string name) => RunAsync(host, name, Deviations, output);
}

public sealed class SamcoHistoryHost : BrokerContractHost
{
    public override string BrokerName => "samco";
    public override string FixtureFile => "history_samco.fixtures.json";
    public override bool NumbersMayBeText => true;
    public override IReadOnlyDictionary<string, string> Keys { get; } = new Dictionary<string, string> { ["ApiKey"] = "samco-api-key", ["ApiSecret"] = "samco-api-secret" };
    public override IReadOnlyDictionary<(string, string), decimal> Prices { get; } = new Dictionary<(string, string), decimal>();
    public override IReadOnlyDictionary<string, string> HarnessReplies { get; } = new Dictionary<string, string>();
    public override IReadOnlyDictionary<string, string> Settings { get; } = new Dictionary<string, string> { ["Brokers:RetryBackoffMs:samco"] = "0", ["OpenAlgoApi:HistorySpacingMs"] = "0" };
}

/// <summary>Samco history (tools/contract-fixtures/history_samco.fixtures.json).</summary>
public sealed class SamcoHistoryContractTests(SamcoHistoryHost host, ITestOutputHelper output) : IClassFixture<SamcoHistoryHost>
{
    private const string Words = "Same status. OpenAlgo words the failure for a developer (a library error, a call name). EP Trade bot says what Samco did and what to do.";

    public static readonly Dictionary<string, Deviation> Deviations = new()
    {
        ["history_session_refused"] = new(Words, f => Expect(f, 500, Error("Samco did not accept the session. Sign in to Samco again, then try again."))),
        ["history_rate_limited_every_time"] = new(Words, f => Expect(f, 500, Error("Samco is limiting how often it can be asked. Wait a moment, then try again."))),
        ["history_server_error_every_time"] = new(Words, f => Expect(f, 500, Error("Samco could not answer just now. Try again in a few minutes."))),
        ["history_interval_samco_lacks"] = new("OpenAlgo asks Samco for one-minute candles when the interval is one it does not offer, and answers with those. EP Trade bot answers 400 and names the intervals Samco offers, so a trader is never handed candles of a different size.",
            f => { Expect(f, 400, Error("Interval 3m is not supported by Samco. Use one of: 1m, 5m, 10m, 15m, 30m, 1h, D.")); f["sent"]!.AsArray().Clear(); }),
    };

    public static IEnumerable<object[]> Names() => ContractRunner.Names("history_samco.fixtures.json");

    [PostgresTheory]
    [MemberData(nameof(Names))]
    public Task Matches_the_python_implementation(string name) => RunAsync(host, name, Deviations, output);
}

public sealed class MarginSamcoHost : BrokerContractHost
{
    public override string BrokerName => "samco";
    public override string FixtureFile => "margin_samco.fixtures.json";
    public override bool NumbersMayBeText => true;
    public override IReadOnlyDictionary<string, string> Keys { get; } = new Dictionary<string, string> { ["ApiKey"] = "samco-api-key", ["ApiSecret"] = "samco-api-secret" };
    public override IReadOnlyDictionary<(string, string), decimal> Prices { get; } = new Dictionary<(string, string), decimal>();
    public override IReadOnlyDictionary<string, string> HarnessReplies { get; } = new Dictionary<string, string>();
}

/// <summary>Samco margin (tools/contract-fixtures/margin_samco.fixtures.json).</summary>
public sealed class MarginSamcoContractTests(MarginSamcoHost host, ITestOutputHelper output) : IClassFixture<MarginSamcoHost>
{
    private static string Unknown(string symbol) => $"{symbol} on NFO is not in the Samco master contract, so the margin cannot be worked out. Check the symbol, or download the master contract again.";

    public static readonly Dictionary<string, Deviation> Deviations = new()
    {
        ["margin_refused_with_a_message"] = new("OpenAlgo answers 200 carrying the refusal. EP Trade bot answers with a failure status and the same words, so a refusal is never read as a margin figure.", f => Expect(f, 400, Error("Invalid scrip"))),
        ["margin_refused_without_a_message"] = new("OpenAlgo answers 200 carrying the refusal. EP Trade bot answers with a failure status.", f => Expect(f, 400, Error("Failed to calculate margin"))),
        ["margin_an_answer_that_is_not_json"] = new("Same status; EP Trade bot says to try again.", f => Expect(f, 500, Error("Samco sent a margin answer EP Trade bot could not read. Try again in a moment."))),
        ["margin_cash_equity_only"] = new("OpenAlgo answers that no position was valid. EP Trade bot names the leg Samco cannot work out margin for.", f => { Expect(f, 400, Error("Samco works out margin only for futures and options, so SBIN on NSE cannot be included. Remove it and try again.")); }),
        ["margin_cash_equity_among_derivatives"] = new("OpenAlgo leaves the cash leg out of the calculation, which understates the margin. EP Trade bot refuses and names the leg.", f => { Expect(f, 400, Error("Samco works out margin only for futures and options, so SBIN on NSE cannot be included. Remove it and try again.")); f["sent"]!.AsArray().Clear(); }),
        ["margin_unknown_symbol_alone"] = new("Same status; EP Trade bot names the symbol, where OpenAlgo says no symbol was valid.", f => Expect(f, 400, Error(Unknown("NOSUCH")))),
        ["margin_unknown_symbol_among_others"] = new("OpenAlgo leaves the position whose symbol it cannot find out of the calculation and works out the rest, which understates the margin. EP Trade bot refuses and names the symbol.", f => { Expect(f, 400, Error(Unknown("NOSUCH"))); f["sent"]!.AsArray().Clear(); }),
    };

    public static IEnumerable<object[]> Names() => ContractRunner.Names("margin_samco.fixtures.json");

    [PostgresTheory]
    [MemberData(nameof(Names))]
    public Task Matches_the_python_implementation(string name) => RunAsync(host, name, Deviations, output);
}
