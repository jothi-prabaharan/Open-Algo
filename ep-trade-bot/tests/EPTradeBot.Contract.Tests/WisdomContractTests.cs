using System.Text.Json.Nodes;
using EPTradeBot.IntegrationTests;
using Xunit.Abstractions;
using static EPTradeBot.Contract.Tests.ContractRunner;

namespace EPTradeBot.Contract.Tests;

public abstract class XtsHost : BrokerContractHost
{
    public override IReadOnlyDictionary<(string, string), decimal> Prices { get; } = new Dictionary<(string, string), decimal>
    {
        [("NSE", "SBIN")] = 805m, [("NSE", "INFY")] = 1500m, [("NFO", "NIFTY25NOV26FUT")] = 24000m, [("NFO", "NIFTY25NOV2624000CE")] = 120m,
        [("MCX", "CRUDEOIL")] = 6500m, [("CDS", "USDINR25NOV26FUT")] = 84.5m,
    };

    public override bool NumbersMayBeText => true;

    public override string? RecordedFeedToken => "feed-token";

    public override IReadOnlyDictionary<string, string> HarnessReplies { get; } = new Dictionary<string, string>
    {
        ["GET /interactive/portfolio/positions"] = """{"type":"success","result":{"positionList":[]}}""",
    };
}

public sealed class WisdomHost : XtsHost
{
    public override string BrokerName => "wisdom";
    public override string FixtureFile => "wisdom.fixtures.json";
}

public sealed class FivepaisaXtsHost : XtsHost
{
    public override string BrokerName => "fivepaisaxts";
    public override string FixtureFile => "fivepaisaxts.fixtures.json";
}

public sealed class IiflHost : XtsHost
{
    public override string BrokerName => "iifl";
    public override string FixtureFile => "iifl.fixtures.json";
}

/// <summary>Differences from OpenAlgo shared by every XTS broker.</summary>
internal static class XtsDeviations
{
    private const string AsText = "The broker sends the order number as a number and OpenAlgo passes it on as one. EP Trade bot's order ids are text on every broker.";

    public static Action<JsonObject> OrderIdAsText => IdAsText;

    private static Action<JsonObject> IdAsText => f =>
    {
        if (f["expected"]!["body"]!["orderid"] is { } id) f["expected"]!["body"]!["orderid"] = id.ToString();
    };

    public static Dictionary<string, Deviation> Common(string display) => new()
    {
        ["placeorder_market"] = new(AsText, IdAsText),
        ["placeorder_limit_cnc"] = new(AsText, IdAsText),
        ["placeorder_sl_futures"] = new(AsText, IdAsText),
        ["placeorder_slm_option"] = new(AsText, IdAsText),
        ["placeorder_mcx"] = new(AsText, IdAsText),
        ["placeorder_currency"] = new(AsText, IdAsText),
        ["cancelallorder"] = new(
            "OpenAlgo lists a stop order waiting for its trigger as \"Trigger Pending\" in the order book; EP Trade bot's cancel-all looks for \"trigger pending\", so the book says that. Order ids are text.",
            f => f["expected"]!["body"]!["canceled_orders"] = new JsonArray("2001", "2003", "2007")),
        ["orderbook"] = new(
            "A stop order waiting for its trigger is \"trigger pending\" in the order book, the spelling the cancel-all and order-status code read.",
            f =>
            {
                foreach (var o in f["expected"]!["body"]!["data"]!["orders"]!.AsArray().OfType<JsonObject>().Where(o => o["order_status"]!.GetValue<string>() == "Trigger Pending")) o["order_status"] = "trigger pending";
            }),
        ["closeposition"] = new(
            $"A position whose symbol is not in the master contract (UNLISTED) cannot be closed by symbol; OpenAlgo sends its instrument id straight to {display}. EP Trade bot says which position is still open.",
            f =>
            {
                Expect(f, 500, Error($"1 position(s) could not be squared off and are still open: UNLISTED (NSE): UNLISTED on NSE is not in the {display} master contract. Check the symbol, or download the master contract again."));
                var sent = f["sent"]!.AsArray();
                sent.RemoveAt(sent.Count - 1);
            }),
        ["placeorder_unknown_symbol"] = new(
            $"OpenAlgo sends exchangeInstrumentID null to {display} for a symbol missing from the master contract. EP Trade bot refuses before sending anything.",
            Status: 400),
        ["placeorder_error_type_carrying_an_order_number"] = new(
            $"OpenAlgo answers success with no order id when {display}'s reply is an error. EP Trade bot refuses with the reason.",
            f => Expect(f, 500, Error("Rejected by risk"))),
        ["placeorder_broker_refuses"] = new(
            $"OpenAlgo answers \"Failed to place order\" and drops {display}'s reason. EP Trade bot passes the reason on.",
            f => f["expected"]!["body"]!["message"] = "Insufficient funds"),
        ["modifyorder_refused"] = new(
            $"OpenAlgo drops {display}'s reason on a refused modify. EP Trade bot passes it on.",
            f => f["expected"]!["body"]!["message"] = "Order already filled"),
        ["cancelorder_refused"] = new(
            $"OpenAlgo drops {display}'s reason on a refused cancel. EP Trade bot passes it on.",
            f => f["expected"]!["body"]!["message"] = "Order cannot be cancelled"),
        ["funds_session_expired"] = new(
            $"OpenAlgo shows an empty balance when {display} refuses the session. EP Trade bot says the session has expired, so nothing is mistaken for a balance.",
            f => Expect(f, 403, Error($"{display} says the session has expired. Log in to {display} again."))),
        ["funds_session_code_on_a_bad_request"] = new(
            $"OpenAlgo shows an empty balance when {display} refuses the session. EP Trade bot says the session has expired, so nothing is mistaken for a balance.",
            f => Expect(f, 403, Error($"{display} says the session has expired. Log in to {display} again."))),
        ["placesmartorder_reduce"] = new(
            $"OpenAlgo looks for the open position under keys {display}'s reply does not have (status, data), so it never sees one: asked to bring a 10-share long to 4, it BUYS 4 more. EP Trade bot reads the position and SELLS 6. Order ids are text.",
            f =>
            {
                IdAsText(f);
                var w = Write(f, "POST")["json"]!;
                w["orderSide"] = "SELL";
                w["orderQuantity"] = "6";
            }),
    };

    /// <summary>OpenAlgo's plugin for this broker fails on a trade with no price and on a position reply with no result; EP Trade bot answers as the Wisdom plugin does.</summary>
    public static Dictionary<string, Deviation> WithCrashes(string display)
    {
        const string reason = "OpenAlgo fails with an internal error here (it converts an empty price, or reads a list as a dictionary). EP Trade bot answers as it does when the reply is well formed.";
        var d = Common(display);
        d["tradebook"] = new(reason, f => CopyFromWisdom(f, "tradebook"));
        d["positionbook_no_result"] = new(reason, f => CopyFromWisdom(f, "positionbook_no_result"));
        d["orderstatus_complete"] = new(reason + " The trade book it reads the average price from is one of these failures.", f => CopyFromWisdom(f, "orderstatus_complete"));
        return d;
    }

    private static void CopyFromWisdom(JsonObject f, string name)
    {
        var wisdom = BrokerContractHost.Load("wisdom.fixtures.json")["fixtures"]!.AsArray().OfType<JsonObject>().Single(x => x["name"]!.GetValue<string>() == name);
        f["expected"] = wisdom["expected"]!.DeepClone();
    }
}

/// <summary>Wisdom Capital (XTS) against the Python implementation (tools/contract-fixtures/wisdom.fixtures.json).</summary>
public sealed class WisdomContractTests(WisdomHost host, ITestOutputHelper output) : IClassFixture<WisdomHost>
{
    public static readonly Dictionary<string, Deviation> Deviations = XtsDeviations.Common("Wisdom Capital");

    public static IEnumerable<object[]> Names() => ContractRunner.Names("wisdom.fixtures.json");

    [PostgresTheory]
    [MemberData(nameof(Names))]
    public Task Matches_the_python_implementation(string name) => RunAsync(host, name, Deviations, output);
}

/// <summary>5paisa XTS against the Python implementation (tools/contract-fixtures/fivepaisaxts.fixtures.json).</summary>
public sealed class FivepaisaXtsContractTests(FivepaisaXtsHost host, ITestOutputHelper output) : IClassFixture<FivepaisaXtsHost>
{
    public static readonly Dictionary<string, Deviation> Deviations = XtsDeviations.WithCrashes("5paisa XTS");

    public static IEnumerable<object[]> Names() => ContractRunner.Names("fivepaisaxts.fixtures.json");

    [PostgresTheory]
    [MemberData(nameof(Names))]
    public Task Matches_the_python_implementation(string name) => RunAsync(host, name, Deviations, output);
}

/// <summary>IIFL against the Python implementation (tools/contract-fixtures/iifl.fixtures.json).</summary>
public sealed class IiflContractTests(IiflHost host, ITestOutputHelper output) : IClassFixture<IiflHost>
{
    public static readonly Dictionary<string, Deviation> Deviations = XtsDeviations.WithCrashes("IIFL");

    public static IEnumerable<object[]> Names() => ContractRunner.Names("iifl.fixtures.json");

    [PostgresTheory]
    [MemberData(nameof(Names))]
    public Task Matches_the_python_implementation(string name) => RunAsync(host, name, Deviations, output);
}

public sealed class WisdomDataHost : XtsHost
{
    public override string BrokerName => "wisdom";
    public override string FixtureFile => "wisdom_data.fixtures.json";
}

/// <summary>Wisdom Capital market data against the Python implementation (tools/contract-fixtures/wisdom_data.fixtures.json).</summary>
public sealed class WisdomDataContractTests(WisdomDataHost host, ITestOutputHelper output) : IClassFixture<WisdomDataHost>
{
    private const string Words = "OpenAlgo answers a refused or empty quote with \"Failed to fetch market data\"; EP Trade bot names the broker and passes its reason on.";

    public static readonly Dictionary<string, Deviation> Deviations = new()
    {
        ["quotes_market_data_refused"] = new(Words, f => Expect(f, 500, Error("Wisdom Capital could not send the quote: Market data not enabled"))),
        ["quotes_empty_list"] = new(Words, f => Expect(f, 500, Error("Wisdom Capital has no quote for SBIN right now."))),
        ["multiquotes_refused"] = new(Words, f => Expect(f, 500, Error("Wisdom Capital could not send the quotes: Market data not enabled"))),
        ["depth_market_data_refused"] = new(
            "OpenAlgo answers success with a depth of zeros when the broker refuses, which reads as a market with no orders. EP Trade bot reports the refusal.",
            f => Expect(f, 500, Error("Wisdom Capital could not send the quote: Market data not enabled"))),
    };

    public static IEnumerable<object[]> Names() => ContractRunner.Names("wisdom_data.fixtures.json");

    [PostgresTheory]
    [MemberData(nameof(Names))]
    public Task Matches_the_python_implementation(string name) => RunAsync(host, name, Deviations, output);
}

public sealed class FivepaisaXtsDataHost : XtsHost
{
    public override string BrokerName => "fivepaisaxts";
    public override string FixtureFile => "fivepaisaxts_data.fixtures.json";
}

/// <summary>5paisa XTS market data against the Python implementation (tools/contract-fixtures/fivepaisaxts_data.fixtures.json).</summary>
public sealed class FivepaisaXtsDataContractTests(FivepaisaXtsDataHost host, ITestOutputHelper output) : IClassFixture<FivepaisaXtsDataHost>
{
    private const string Words = "OpenAlgo answers a refused or empty quote with \"Failed to fetch market data\"; EP Trade bot names the broker and passes its reason on.";

    public static readonly Dictionary<string, Deviation> Deviations = new()
    {
        ["quotes_market_data_refused"] = new(Words, f => Expect(f, 500, Error("5paisa XTS could not send the quote: Market data not enabled"))),
        ["quotes_empty_list"] = new(Words, f => Expect(f, 500, Error("5paisa XTS has no quote for SBIN right now."))),
        ["multiquotes_refused"] = new(Words, f => Expect(f, 500, Error("5paisa XTS could not send the quotes: Market data not enabled"))),
        ["depth_market_data_refused"] = new(
            "OpenAlgo answers success with a depth of zeros when the broker refuses, which reads as a market with no orders. EP Trade bot reports the refusal.",
            f => Expect(f, 500, Error("5paisa XTS could not send the quote: Market data not enabled"))),
    };

    public static IEnumerable<object[]> Names() => ContractRunner.Names("fivepaisaxts_data.fixtures.json");

    [PostgresTheory]
    [MemberData(nameof(Names))]
    public Task Matches_the_python_implementation(string name) => RunAsync(host, name, Deviations, output);
}

public sealed class IiflDataHost : XtsHost
{
    public override string BrokerName => "iifl";
    public override string FixtureFile => "iifl_data.fixtures.json";
}

/// <summary>IIFL market data against the Python implementation (tools/contract-fixtures/iifl_data.fixtures.json).</summary>
public sealed class IiflDataContractTests(IiflDataHost host, ITestOutputHelper output) : IClassFixture<IiflDataHost>
{
    private const string Words = "OpenAlgo answers a refused or empty quote with \"Failed to fetch market data\"; EP Trade bot names the broker and passes its reason on.";

    public static readonly Dictionary<string, Deviation> Deviations = new()
    {
        ["quotes_market_data_refused"] = new(Words, f => Expect(f, 500, Error("IIFL could not send the quote: Market data not enabled"))),
        ["quotes_empty_list"] = new(Words, f => Expect(f, 500, Error("IIFL has no quote for SBIN right now."))),
        ["multiquotes_refused"] = new(Words, f => Expect(f, 500, Error("IIFL could not send the quotes: Market data not enabled"))),
        ["depth_market_data_refused"] = new(
            "OpenAlgo answers success with a depth of zeros when the broker refuses, which reads as a market with no orders. EP Trade bot reports the refusal.",
            f => Expect(f, 500, Error("IIFL could not send the quote: Market data not enabled"))),
    };

    public static IEnumerable<object[]> Names() => ContractRunner.Names("iifl_data.fixtures.json");

    [PostgresTheory]
    [MemberData(nameof(Names))]
    public Task Matches_the_python_implementation(string name) => RunAsync(host, name, Deviations, output);
}

public sealed class WisdomHistoryHost : XtsHost
{
    public override string BrokerName => "wisdom";
    public override string FixtureFile => "history_wisdom.fixtures.json";
    public override IReadOnlyDictionary<string, string> Settings { get; } = new Dictionary<string, string> { ["OpenAlgoApi:HistorySpacingMs"] = "0" };
}

/// <summary>Wisdom Capital history against the Python implementation (tools/contract-fixtures/history_wisdom.fixtures.json).</summary>
public sealed class WisdomHistoryContractTests(WisdomHistoryHost host, ITestOutputHelper output) : IClassFixture<WisdomHistoryHost>
{
    private const string Quiet = "OpenAlgo fails with \"No objects to concatenate\" when no candle came back, which looks like a fault; EP Trade bot answers success with no candles.";

    private static void Refused(JsonObject f, string from, string to) =>
        Expect(f, 500, Error($"Wisdom Capital could not send the candles for {from} to {to}: Invalid Token"));

    public static readonly Dictionary<string, Deviation> Deviations = new()
    {
        ["history_nothing_traded"] = new(Quiet, f => Expect(f, 200, new JsonObject { ["status"] = "success", ["data"] = new JsonArray() })),
        ["history_end_before_start"] = new(Quiet, f => Expect(f, 200, new JsonObject { ["status"] = "success", ["data"] = new JsonArray() })),
        ["history_refused"] = new("OpenAlgo shows an internal-looking message; EP Trade bot says which days were refused and why.", f => Refused(f, "2026-11-02", "2026-11-02")),
        ["history_unsupported_interval"] = new(
            "OpenAlgo answers 500 with \"Unsupported timeframe: 4h\". EP Trade bot answers 400 and names what Wisdom Capital offers.",
            f => Expect(f, 400, Error("Wisdom Capital does not offer 4h candles. Choose one of: 1s, 1m, 2m, 3m, 5m, 10m, 15m, 30m, 60m, D."))),
        ["history_chunked_in_weeks"] = new(
            "OpenAlgo ends every week but the last at midnight of its seventh day and starts the next on the day after, so the seventh day's trading is never asked for. EP Trade bot asks for the whole of each day.",
            f =>
            {
                var sent = f["sent"]!.AsArray();
                sent[0]!["query"]!.AsArray()[3]!.AsArray()[1] = "Oct 25 2026 235959";
                sent[1]!["query"]!.AsArray()[3]!.AsArray()[1] = "Nov 01 2026 235959";
            }),
        ["history_one_chunk_refused"] = new("OpenAlgo shows an internal-looking message; EP Trade bot says which days were refused and why.", f =>
        {
            Refused(f, "2026-11-02", "2026-11-02");
            f["sent"]!.AsArray()[0]!["query"]!.AsArray()[3]!.AsArray()[1] = "Nov 01 2026 235959";
        }),
    };

    public static IEnumerable<object[]> Names() => ContractRunner.Names("history_wisdom.fixtures.json");

    [PostgresTheory]
    [MemberData(nameof(Names))]
    public Task Matches_the_python_implementation(string name) => RunAsync(host, name, Deviations, output);
}

public sealed class FivepaisaXtsHistoryHost : XtsHost
{
    public override string BrokerName => "fivepaisaxts";
    public override string FixtureFile => "history_fivepaisaxts.fixtures.json";
    public override IReadOnlyDictionary<string, string> Settings { get; } = new Dictionary<string, string> { ["OpenAlgoApi:HistorySpacingMs"] = "0" };
}

/// <summary>5paisa XTS history against the Python implementation (tools/contract-fixtures/history_fivepaisaxts.fixtures.json).</summary>
public sealed class FivepaisaXtsHistoryContractTests(FivepaisaXtsHistoryHost host, ITestOutputHelper output) : IClassFixture<FivepaisaXtsHistoryHost>
{
    private const string Quiet = "OpenAlgo fails with \"No objects to concatenate\" when no candle came back, which looks like a fault; EP Trade bot answers success with no candles.";

    private static void Refused(JsonObject f, string from, string to) =>
        Expect(f, 500, Error($"5paisa XTS could not send the candles for {from} to {to}: Invalid Token"));

    public static readonly Dictionary<string, Deviation> Deviations = new()
    {
        ["history_nothing_traded"] = new(Quiet, f => Expect(f, 200, new JsonObject { ["status"] = "success", ["data"] = new JsonArray() })),
        ["history_end_before_start"] = new(Quiet, f => Expect(f, 200, new JsonObject { ["status"] = "success", ["data"] = new JsonArray() })),
        ["history_refused"] = new("OpenAlgo shows an internal-looking message; EP Trade bot says which days were refused and why.", f => Refused(f, "2026-11-02", "2026-11-02")),
        ["history_unsupported_interval"] = new(
            "OpenAlgo answers 500 with \"Unsupported timeframe: 4h\". EP Trade bot answers 400 and names what 5paisa XTS offers.",
            f => Expect(f, 400, Error("5paisa XTS does not offer 4h candles. Choose one of: 1s, 1m, 2m, 3m, 5m, 10m, 15m, 30m, 60m, D."))),
        ["history_chunked_in_weeks"] = new(
            "OpenAlgo ends every week but the last at midnight of its seventh day and starts the next on the day after, so the seventh day's trading is never asked for. EP Trade bot asks for the whole of each day.",
            f =>
            {
                var sent = f["sent"]!.AsArray();
                sent[0]!["query"]!.AsArray()[3]!.AsArray()[1] = "Oct 25 2026 235959";
                sent[1]!["query"]!.AsArray()[3]!.AsArray()[1] = "Nov 01 2026 235959";
            }),
        ["history_one_chunk_refused"] = new("OpenAlgo shows an internal-looking message; EP Trade bot says which days were refused and why.", f =>
        {
            Refused(f, "2026-11-02", "2026-11-02");
            f["sent"]!.AsArray()[0]!["query"]!.AsArray()[3]!.AsArray()[1] = "Nov 01 2026 235959";
        }),
    };

    public static IEnumerable<object[]> Names() => ContractRunner.Names("history_fivepaisaxts.fixtures.json");

    [PostgresTheory]
    [MemberData(nameof(Names))]
    public Task Matches_the_python_implementation(string name) => RunAsync(host, name, Deviations, output);
}

public sealed class IiflHistoryHost : XtsHost
{
    public override string BrokerName => "iifl";
    public override string FixtureFile => "history_iifl.fixtures.json";
    public override IReadOnlyDictionary<string, string> Settings { get; } = new Dictionary<string, string> { ["OpenAlgoApi:HistorySpacingMs"] = "0" };
}

/// <summary>IIFL history against the Python implementation (tools/contract-fixtures/history_iifl.fixtures.json).</summary>
public sealed class IiflHistoryContractTests(IiflHistoryHost host, ITestOutputHelper output) : IClassFixture<IiflHistoryHost>
{
    private const string Quiet = "OpenAlgo fails with \"No objects to concatenate\" when no candle came back, which looks like a fault; EP Trade bot answers success with no candles.";

    private static void Refused(JsonObject f, string from, string to) =>
        Expect(f, 500, Error($"IIFL could not send the candles for {from} to {to}: Invalid Token"));

    private const string NoRefresh = "IIFL signs in to market data again when it is told the token is invalid, then tries the call once more. EP Trade bot's plugin cannot store a new token, so it reports the refusal.";

    private static Action<JsonObject> DropRefresh(Action<JsonObject> inner) => f =>
    {
        inner(f);
        var sent = f["sent"]!.AsArray();
        foreach (var s in sent.OfType<JsonObject>().Where(s => s["path"]!.GetValue<string>() == "/apimarketdata/auth/login").ToList()) sent.Remove(s);
    };

    public static readonly Dictionary<string, Deviation> Deviations = Refreshing(new()
    {
        ["history_nothing_traded"] = new(Quiet, f => Expect(f, 200, new JsonObject { ["status"] = "success", ["data"] = new JsonArray() })),
        ["history_end_before_start"] = new(Quiet, f => Expect(f, 200, new JsonObject { ["status"] = "success", ["data"] = new JsonArray() })),
        ["history_refused"] = new("OpenAlgo shows an internal-looking message; EP Trade bot says which days were refused and why.", f => Refused(f, "2026-11-02", "2026-11-02")),
        ["history_unsupported_interval"] = new(
            "OpenAlgo answers 500 with \"Unsupported timeframe: 4h\". EP Trade bot answers 400 and names what IIFL offers.",
            f => Expect(f, 400, Error("IIFL does not offer 4h candles. Choose one of: 1s, 1m, 2m, 3m, 5m, 10m, 15m, 30m, 60m, D."))),
        ["history_chunked_in_weeks"] = new(
            "OpenAlgo ends every week but the last at midnight of its seventh day and starts the next on the day after, so the seventh day's trading is never asked for. EP Trade bot asks for the whole of each day.",
            f =>
            {
                var sent = f["sent"]!.AsArray();
                sent[0]!["query"]!.AsArray()[3]!.AsArray()[1] = "Oct 25 2026 235959";
                sent[1]!["query"]!.AsArray()[3]!.AsArray()[1] = "Nov 01 2026 235959";
            }),
        ["history_one_chunk_refused"] = new("OpenAlgo shows an internal-looking message; EP Trade bot says which days were refused and why.", f =>
        {
            Refused(f, "2026-11-02", "2026-11-02");
            f["sent"]!.AsArray()[0]!["query"]!.AsArray()[3]!.AsArray()[1] = "Nov 01 2026 235959";
        }),
    });

    private static Dictionary<string, Deviation> Refreshing(Dictionary<string, Deviation> d)
    {
        foreach (var name in new[] { "history_refused", "history_one_chunk_refused" }) d[name] = new(NoRefresh + " " + d[name].Reason, DropRefresh(d[name].Adjust!));
        return d;
    }

    public static IEnumerable<object[]> Names() => ContractRunner.Names("history_iifl.fixtures.json");

    [PostgresTheory]
    [MemberData(nameof(Names))]
    public Task Matches_the_python_implementation(string name) => RunAsync(host, name, Deviations, output);
}

public sealed class IbullsHost : XtsHost
{
    public override string BrokerName => "ibulls";
    public override string FixtureFile => "ibulls.fixtures.json";
}

/// <summary>iBulls against the Python implementation (tools/contract-fixtures/ibulls.fixtures.json).</summary>
public sealed class IbullsContractTests(IbullsHost host, ITestOutputHelper output) : IClassFixture<IbullsHost>
{
    public static readonly Dictionary<string, Deviation> Deviations = Build();

    private static Dictionary<string, Deviation> Build()
    {
        var d = XtsDeviations.WithCrashes("iBulls");
        foreach (var name in new[]
        {
            "placeorder_market_buy_priced_from_the_bid", "placeorder_market_sell_priced_from_the_ask", "placeorder_market_bid_is_zero",
            "placeorder_market_ask_is_zero", "placeorder_market_quote_refused", "placeorder_limit_is_not_priced_again", "placeorder_market_priced_cheap_share",
        })
        {
            d[name] = new("The broker sends the order number as a number and OpenAlgo passes it on as one. EP Trade bot's order ids are text on every broker.", XtsDeviations.OrderIdAsText);
        }



        return d;
    }

    public static IEnumerable<object[]> Names() => ContractRunner.Names("ibulls.fixtures.json");

    [PostgresTheory]
    [MemberData(nameof(Names))]
    public Task Matches_the_python_implementation(string name) => RunAsync(host, name, Deviations, output);
}

public sealed class IbullsDataHost : XtsHost
{
    public override string BrokerName => "ibulls";
    public override string FixtureFile => "ibulls_data.fixtures.json";
}

/// <summary>iBulls market data against the Python implementation (tools/contract-fixtures/ibulls_data.fixtures.json).</summary>
public sealed class IbullsDataContractTests(IbullsDataHost host, ITestOutputHelper output) : IClassFixture<IbullsDataHost>
{
    private const string Words = "OpenAlgo answers a refused or empty quote with \"Failed to fetch market data\"; EP Trade bot names the broker and passes its reason on.";

    public static readonly Dictionary<string, Deviation> Deviations = new()
    {
        ["quotes_market_data_refused"] = new(Words, f => Expect(f, 500, Error("iBulls could not send the quote: Market data not enabled"))),
        ["quotes_empty_list"] = new(Words, f => Expect(f, 500, Error("iBulls has no quote for SBIN right now."))),
        ["multiquotes_refused"] = new(Words, f => Expect(f, 500, Error("iBulls could not send the quotes: Market data not enabled"))),
        ["depth_market_data_refused"] = new(
            "OpenAlgo answers success with a depth of zeros when the broker refuses, which reads as a market with no orders. EP Trade bot reports the refusal.",
            f => Expect(f, 500, Error("iBulls could not send the quote: Market data not enabled"))),
    };

    public static IEnumerable<object[]> Names() => ContractRunner.Names("ibulls_data.fixtures.json");

    [PostgresTheory]
    [MemberData(nameof(Names))]
    public Task Matches_the_python_implementation(string name) => RunAsync(host, name, Deviations, output);
}


public sealed class IbullsHistoryHost : XtsHost
{
    public override string BrokerName => "ibulls";
    public override string FixtureFile => "history_ibulls.fixtures.json";
    public override IReadOnlyDictionary<string, string> Settings { get; } = new Dictionary<string, string> { ["OpenAlgoApi:HistorySpacingMs"] = "0" };
}

/// <summary>iBulls history against the Python implementation (tools/contract-fixtures/history_ibulls.fixtures.json).</summary>
public sealed class IbullsHistoryContractTests(IbullsHistoryHost host, ITestOutputHelper output) : IClassFixture<IbullsHistoryHost>
{
    private const string Quiet = "OpenAlgo fails with \"No objects to concatenate\" when no candle came back, which looks like a fault; EP Trade bot answers success with no candles.";

    private static void Refused(JsonObject f, string from, string to) =>
        Expect(f, 500, Error($"iBulls could not send the candles for {from} to {to}: Invalid Token"));

    public static readonly Dictionary<string, Deviation> Deviations = new()
    {
        ["history_nothing_traded"] = new(Quiet, f => Expect(f, 200, new JsonObject { ["status"] = "success", ["data"] = new JsonArray() })),
        ["history_end_before_start"] = new(Quiet, f => Expect(f, 200, new JsonObject { ["status"] = "success", ["data"] = new JsonArray() })),
        ["history_refused"] = new("OpenAlgo shows an internal-looking message; EP Trade bot says which days were refused and why.", f => Refused(f, "2026-11-02", "2026-11-02")),
        ["history_unsupported_interval"] = new(
            "OpenAlgo answers 500 with \"Unsupported timeframe: 4h\". EP Trade bot answers 400 and names what iBulls offers.",
            f => Expect(f, 400, Error("iBulls does not offer 4h candles. Choose one of: 1s, 1m, 2m, 3m, 5m, 10m, 15m, 30m, 60m, D."))),
        ["history_chunked_in_weeks"] = new(
            "OpenAlgo ends every week but the last at midnight of its seventh day and starts the next on the day after, so the seventh day's trading is never asked for. EP Trade bot asks for the whole of each day.",
            f =>
            {
                var sent = f["sent"]!.AsArray();
                sent[0]!["query"]!.AsArray()[3]!.AsArray()[1] = "Oct 25 2026 235959";
                sent[1]!["query"]!.AsArray()[3]!.AsArray()[1] = "Nov 01 2026 235959";
            }),
        ["history_one_chunk_refused"] = new("OpenAlgo shows an internal-looking message; EP Trade bot says which days were refused and why.", f =>
        {
            Refused(f, "2026-11-02", "2026-11-02");
            f["sent"]!.AsArray()[0]!["query"]!.AsArray()[3]!.AsArray()[1] = "Nov 01 2026 235959";
        }),
    };

    public static IEnumerable<object[]> Names() => ContractRunner.Names("history_ibulls.fixtures.json");

    [PostgresTheory]
    [MemberData(nameof(Names))]
    public Task Matches_the_python_implementation(string name) => RunAsync(host, name, Deviations, output);
}


public sealed class JainamXtsHost : XtsHost
{
    public override string BrokerName => "jainamxts";
    public override string FixtureFile => "jainamxts.fixtures.json";
}

/// <summary>Jainam against the Python implementation (tools/contract-fixtures/jainamxts.fixtures.json).</summary>
public sealed class JainamXtsContractTests(JainamXtsHost host, ITestOutputHelper output) : IClassFixture<JainamXtsHost>
{
    public static readonly Dictionary<string, Deviation> Deviations = Build();

    private static Dictionary<string, Deviation> Build()
    {
        var d = XtsDeviations.WithCrashes("Jainam");
        return d;
    }

    public static IEnumerable<object[]> Names() => ContractRunner.Names("jainamxts.fixtures.json");

    [PostgresTheory]
    [MemberData(nameof(Names))]
    public Task Matches_the_python_implementation(string name) => RunAsync(host, name, Deviations, output);
}

public sealed class JainamXtsDataHost : XtsHost
{
    public override string BrokerName => "jainamxts";
    public override string FixtureFile => "jainamxts_data.fixtures.json";
}

/// <summary>Jainam market data against the Python implementation (tools/contract-fixtures/jainamxts_data.fixtures.json).</summary>
public sealed class JainamXtsDataContractTests(JainamXtsDataHost host, ITestOutputHelper output) : IClassFixture<JainamXtsDataHost>
{
    private const string Words = "OpenAlgo answers a refused or empty quote with \"Failed to fetch market data\"; EP Trade bot names the broker and passes its reason on.";

    public static readonly Dictionary<string, Deviation> Deviations = new()
    {
        ["quotes_market_data_refused"] = new(Words, f => Expect(f, 500, Error("Jainam could not send the quote: Market data not enabled"))),
        ["quotes_empty_list"] = new(Words, f => Expect(f, 500, Error("Jainam has no quote for SBIN right now."))),
        ["multiquotes_refused"] = new(Words, f => Expect(f, 500, Error("Jainam could not send the quotes: Market data not enabled"))),
        ["depth_market_data_refused"] = new(
            "OpenAlgo answers success with a depth of zeros when the broker refuses, which reads as a market with no orders. EP Trade bot reports the refusal.",
            f => Expect(f, 500, Error("Jainam could not send the quote: Market data not enabled"))),
    };

    public static IEnumerable<object[]> Names() => ContractRunner.Names("jainamxts_data.fixtures.json");

    [PostgresTheory]
    [MemberData(nameof(Names))]
    public Task Matches_the_python_implementation(string name) => RunAsync(host, name, Deviations, output);
}


public sealed class JainamXtsHistoryHost : XtsHost
{
    public override string BrokerName => "jainamxts";
    public override string FixtureFile => "history_jainamxts.fixtures.json";
    public override IReadOnlyDictionary<string, string> Settings { get; } = new Dictionary<string, string> { ["OpenAlgoApi:HistorySpacingMs"] = "0" };
}

/// <summary>Jainam history against the Python implementation (tools/contract-fixtures/history_jainamxts.fixtures.json).</summary>
public sealed class JainamXtsHistoryContractTests(JainamXtsHistoryHost host, ITestOutputHelper output) : IClassFixture<JainamXtsHistoryHost>
{
    private const string Quiet = "OpenAlgo fails with \"No objects to concatenate\" when no candle came back, which looks like a fault; EP Trade bot answers success with no candles.";

    private static void Refused(JsonObject f, string from, string to) =>
        Expect(f, 500, Error($"Jainam could not send the candles for {from} to {to}: Invalid Token"));

    private const string NoRefresh = "Jainam signs in to market data again when it is told the token is invalid, then tries the call once more. EP Trade bot's plugin cannot store a new token, so it reports the refusal.";

    private static Action<JsonObject> DropRefresh(Action<JsonObject> inner) => f =>
    {
        inner(f);
        var sent = f["sent"]!.AsArray();
        foreach (var s in sent.OfType<JsonObject>().Where(s => s["path"]!.GetValue<string>() == "/apibinarymarketdata/auth/login").ToList()) sent.Remove(s);
    };

    public static readonly Dictionary<string, Deviation> Deviations = Refreshing(new()
    {
        ["history_nothing_traded"] = new(Quiet, f => Expect(f, 200, new JsonObject { ["status"] = "success", ["data"] = new JsonArray() })),
        ["history_end_before_start"] = new(Quiet, f => Expect(f, 200, new JsonObject { ["status"] = "success", ["data"] = new JsonArray() })),
        ["history_refused"] = new("OpenAlgo shows an internal-looking message; EP Trade bot says which days were refused and why.", f => Refused(f, "2026-11-02", "2026-11-02")),
        ["history_unsupported_interval"] = new(
            "OpenAlgo answers 500 with \"Unsupported timeframe: 4h\". EP Trade bot answers 400 and names what Jainam offers.",
            f => Expect(f, 400, Error("Jainam does not offer 4h candles. Choose one of: 1s, 1m, 2m, 3m, 5m, 10m, 15m, 30m, 60m, D."))),
        ["history_chunked_in_weeks"] = new(
            "OpenAlgo ends every week but the last at midnight of its seventh day and starts the next on the day after, so the seventh day's trading is never asked for. EP Trade bot asks for the whole of each day.",
            f =>
            {
                var sent = f["sent"]!.AsArray();
                sent[0]!["query"]!.AsArray()[3]!.AsArray()[1] = "Oct 25 2026 235959";
                sent[1]!["query"]!.AsArray()[3]!.AsArray()[1] = "Nov 01 2026 235959";
            }),
        ["history_one_chunk_refused"] = new("OpenAlgo shows an internal-looking message; EP Trade bot says which days were refused and why.", f =>
        {
            Refused(f, "2026-11-02", "2026-11-02");
            f["sent"]!.AsArray()[0]!["query"]!.AsArray()[3]!.AsArray()[1] = "Nov 01 2026 235959";
        }),
    });

    private static Dictionary<string, Deviation> Refreshing(Dictionary<string, Deviation> d)
    {
        foreach (var name in new[] { "history_refused", "history_one_chunk_refused" }) d[name] = new(NoRefresh + " " + d[name].Reason, DropRefresh(d[name].Adjust!));
        return d;
    }

    public static IEnumerable<object[]> Names() => ContractRunner.Names("history_jainamxts.fixtures.json");

    [PostgresTheory]
    [MemberData(nameof(Names))]
    public Task Matches_the_python_implementation(string name) => RunAsync(host, name, Deviations, output);
}

public sealed class CompositEdgeHost : XtsHost
{
    public override string BrokerName => "compositedge";
    public override string FixtureFile => "compositedge.fixtures.json";
}

/// <summary>CompositEdge against the Python implementation (tools/contract-fixtures/compositedge.fixtures.json).</summary>
public sealed class CompositEdgeContractTests(CompositEdgeHost host, ITestOutputHelper output) : IClassFixture<CompositEdgeHost>
{
    public static readonly Dictionary<string, Deviation> Deviations = XtsDeviations.WithCrashes("CompositEdge");

    public static IEnumerable<object[]> Names() => ContractRunner.Names("compositedge.fixtures.json");

    [PostgresTheory]
    [MemberData(nameof(Names))]
    public Task Matches_the_python_implementation(string name) => RunAsync(host, name, Deviations, output);
}

public sealed class CompositEdgeDataHost : XtsHost
{
    public override string BrokerName => "compositedge";
    public override string FixtureFile => "compositedge_data.fixtures.json";
}

/// <summary>CompositEdge market data against the Python implementation (tools/contract-fixtures/compositedge_data.fixtures.json).</summary>
public sealed class CompositEdgeDataContractTests(CompositEdgeDataHost host, ITestOutputHelper output) : IClassFixture<CompositEdgeDataHost>
{
    private const string Words = "OpenAlgo answers a refused or empty quote with \"Failed to fetch market data\"; EP Trade bot names the broker and passes its reason on.";

    public static readonly Dictionary<string, Deviation> Deviations = new()
    {
        ["quotes_market_data_refused"] = new(Words, f => Expect(f, 500, Error("CompositEdge could not send the quote: Market data not enabled"))),
        ["quotes_empty_list"] = new(Words, f => Expect(f, 500, Error("CompositEdge has no quote for SBIN right now."))),
        ["multiquotes_refused"] = new(Words, f => Expect(f, 500, Error("CompositEdge could not send the quotes: Market data not enabled"))),
        ["depth_market_data_refused"] = new(
            "OpenAlgo answers success with a depth of zeros when the broker refuses, which reads as a market with no orders. EP Trade bot reports the refusal.",
            f => Expect(f, 500, Error("CompositEdge could not send the quote: Market data not enabled"))),
    };

    public static IEnumerable<object[]> Names() => ContractRunner.Names("compositedge_data.fixtures.json");

    [PostgresTheory]
    [MemberData(nameof(Names))]
    public Task Matches_the_python_implementation(string name) => RunAsync(host, name, Deviations, output);
}

public sealed class CompositEdgeHistoryHost : XtsHost
{
    public override string BrokerName => "compositedge";
    public override string FixtureFile => "history_compositedge.fixtures.json";
    public override IReadOnlyDictionary<string, string> Settings { get; } = new Dictionary<string, string> { ["OpenAlgoApi:HistorySpacingMs"] = "0" };
}

/// <summary>CompositEdge history against the Python implementation (tools/contract-fixtures/history_compositedge.fixtures.json).</summary>
public sealed class CompositEdgeHistoryContractTests(CompositEdgeHistoryHost host, ITestOutputHelper output) : IClassFixture<CompositEdgeHistoryHost>
{
    private const string Quiet = "OpenAlgo fails with \"No objects to concatenate\" when no candle came back, which looks like a fault; EP Trade bot answers success with no candles.";

    private static void Refused(JsonObject f, string from, string to) =>
        Expect(f, 500, Error($"CompositEdge could not send the candles for {from} to {to}: Invalid Token"));

    public static readonly Dictionary<string, Deviation> Deviations = new()
    {
        ["history_nothing_traded"] = new(Quiet, f => Expect(f, 200, new JsonObject { ["status"] = "success", ["data"] = new JsonArray() })),
        ["history_end_before_start"] = new(Quiet, f => Expect(f, 200, new JsonObject { ["status"] = "success", ["data"] = new JsonArray() })),
        ["history_refused"] = new("OpenAlgo shows an internal-looking message; EP Trade bot says which days were refused and why.", f => Refused(f, "2026-11-02", "2026-11-02")),
        ["history_unsupported_interval"] = new(
            "OpenAlgo answers 500 with \"Unsupported timeframe: 4h\". EP Trade bot answers 400 and names what CompositEdge offers.",
            f => Expect(f, 400, Error("CompositEdge does not offer 4h candles. Choose one of: 1s, 1m, 2m, 3m, 5m, 10m, 15m, 30m, 60m, D."))),
        ["history_chunked_in_weeks"] = new(
            "OpenAlgo ends every week but the last at midnight of its seventh day and starts the next on the day after, so the seventh day's trading is never asked for. EP Trade bot asks for the whole of each day.",
            f =>
            {
                var sent = f["sent"]!.AsArray();
                sent[0]!["query"]!.AsArray()[3]!.AsArray()[1] = "Oct 25 2026 235959";
                sent[1]!["query"]!.AsArray()[3]!.AsArray()[1] = "Nov 01 2026 235959";
            }),
        ["history_one_chunk_refused"] = new("OpenAlgo shows an internal-looking message; EP Trade bot says which days were refused and why.", f =>
        {
            Refused(f, "2026-11-02", "2026-11-02");
            f["sent"]!.AsArray()[0]!["query"]!.AsArray()[3]!.AsArray()[1] = "Nov 01 2026 235959";
        }),
    };

    public static IEnumerable<object[]> Names() => ContractRunner.Names("history_compositedge.fixtures.json");

    [PostgresTheory]
    [MemberData(nameof(Names))]
    public Task Matches_the_python_implementation(string name) => RunAsync(host, name, Deviations, output);
}


public sealed class RmoneyHost : XtsHost
{
    public override string BrokerName => "rmoney";
    public override string FixtureFile => "rmoney.fixtures.json";
}

/// <summary>RMoney against the Python implementation (tools/contract-fixtures/rmoney.fixtures.json).</summary>
public sealed class RmoneyContractTests(RmoneyHost host, ITestOutputHelper output) : IClassFixture<RmoneyHost>
{
    public static readonly Dictionary<string, Deviation> Deviations = Build();

    private static Dictionary<string, Deviation> Build()
    {
        const string list = "OpenAlgo's RMoney plugin fails with an internal error when the position reply's result is a list, which RMoney's own close-all and open-position lookups accept. EP Trade bot lists the positions.";
        var d = XtsDeviations.WithCrashes("RMoney");
        d["positionbook_result_is_a_list"] = new(list, f => Expect(f, 200, new JsonObject
        {
            ["status"] = "success",
            ["data"] = new JsonArray(
                new JsonObject { ["symbol"] = "SBIN", ["exchange"] = "NSE", ["product"] = "MIS", ["quantity"] = "10", ["average_price"] = "801.12", ["ltp"] = 0, ["pnl"] = 0 },
                new JsonObject { ["symbol"] = "NIFTY25NOV26FUT", ["exchange"] = "NFO", ["product"] = "NRML", ["quantity"] = "-75", ["average_price"] = "24005.50", ["ltp"] = 0, ["pnl"] = 0 }),
        }));
        d["openposition_from_a_list"] = new(list, f => Expect(f, 200, new JsonObject { ["status"] = "success", ["quantity"] = "10" }));
        return d;
    }

    public static IEnumerable<object[]> Names() => ContractRunner.Names("rmoney.fixtures.json");

    [PostgresTheory]
    [MemberData(nameof(Names))]
    public Task Matches_the_python_implementation(string name) => RunAsync(host, name, Deviations, output);
}

public sealed class RmoneyDataHost : XtsHost
{
    public override string BrokerName => "rmoney";
    public override string FixtureFile => "rmoney_data.fixtures.json";
}

/// <summary>RMoney market data against the Python implementation (tools/contract-fixtures/rmoney_data.fixtures.json).</summary>
public sealed class RmoneyDataContractTests(RmoneyDataHost host, ITestOutputHelper output) : IClassFixture<RmoneyDataHost>
{
    private const string Words = "OpenAlgo answers a refused or empty quote with \"Failed to fetch market data\"; EP Trade bot names the broker and passes its reason on.";

    public static readonly Dictionary<string, Deviation> Deviations = new()
    {
        ["quotes_market_data_refused"] = new(Words, f => Expect(f, 500, Error("RMoney could not send the quote: Market data not enabled"))),
        ["quotes_empty_list"] = new(Words, f => Expect(f, 500, Error("RMoney has no quote for SBIN right now."))),
        ["multiquotes_refused"] = new(Words, f => Expect(f, 500, Error("RMoney could not send the quotes: Market data not enabled"))),
        ["depth_market_data_refused"] = new(
            "OpenAlgo answers success with a depth of zeros when the broker refuses, which reads as a market with no orders. EP Trade bot reports the refusal.",
            f => Expect(f, 500, Error("RMoney could not send the quote: Market data not enabled"))),
    };

    public static IEnumerable<object[]> Names() => ContractRunner.Names("rmoney_data.fixtures.json");

    [PostgresTheory]
    [MemberData(nameof(Names))]
    public Task Matches_the_python_implementation(string name) => RunAsync(host, name, Deviations, output);
}

public sealed class RmoneyHistoryHost : XtsHost
{
    public override string BrokerName => "rmoney";
    public override string FixtureFile => "history_rmoney.fixtures.json";
    public override IReadOnlyDictionary<string, string> Settings { get; } = new Dictionary<string, string> { ["OpenAlgoApi:HistorySpacingMs"] = "0" };
}

/// <summary>RMoney history against the Python implementation (tools/contract-fixtures/history_rmoney.fixtures.json).</summary>
public sealed class RmoneyHistoryContractTests(RmoneyHistoryHost host, ITestOutputHelper output) : IClassFixture<RmoneyHistoryHost>
{
    private const string Quiet = "OpenAlgo fails with \"No objects to concatenate\" when no candle came back, which looks like a fault; EP Trade bot answers success with no candles.";

    private static void Refused(JsonObject f, string from, string to) =>
        Expect(f, 500, Error($"RMoney could not send the candles for {from} to {to}: Invalid Token"));

    private const string NoRefresh = "RMoney signs in to market data again when it is told the token is invalid, then tries the call once more. EP Trade bot's plugin cannot store a new token, so it reports the refusal.";

    private static Action<JsonObject> DropRefresh(Action<JsonObject> inner) => f =>
    {
        inner(f);
        var sent = f["sent"]!.AsArray();
        foreach (var s in sent.OfType<JsonObject>().Where(s => s["path"]!.GetValue<string>() == "/apibinarymarketdata/auth/login").ToList()) sent.Remove(s);
    };

    public static readonly Dictionary<string, Deviation> Deviations = Refreshing(new()
    {
        ["history_nothing_traded"] = new(Quiet, f => Expect(f, 200, new JsonObject { ["status"] = "success", ["data"] = new JsonArray() })),
        ["history_end_before_start"] = new(Quiet, f => Expect(f, 200, new JsonObject { ["status"] = "success", ["data"] = new JsonArray() })),
        ["history_refused"] = new("OpenAlgo shows an internal-looking message; EP Trade bot says which days were refused and why.", f => Refused(f, "2026-11-02", "2026-11-02")),
        ["history_unsupported_interval"] = new(
            "OpenAlgo answers 500 with \"Unsupported timeframe: 4h\". EP Trade bot answers 400 and names what RMoney offers.",
            f => Expect(f, 400, Error("RMoney does not offer 4h candles. Choose one of: 1s, 1m, 2m, 3m, 5m, 10m, 15m, 30m, 60m, D."))),
        ["history_chunked_in_weeks"] = new(
            "OpenAlgo ends every week but the last at midnight of its seventh day and starts the next on the day after, so the seventh day's trading is never asked for. EP Trade bot asks for the whole of each day.",
            f =>
            {
                var sent = f["sent"]!.AsArray();
                sent[0]!["query"]!.AsArray()[3]!.AsArray()[1] = "Oct 25 2026 235959";
                sent[1]!["query"]!.AsArray()[3]!.AsArray()[1] = "Nov 01 2026 235959";
            }),
        ["history_one_chunk_refused"] = new("OpenAlgo shows an internal-looking message; EP Trade bot says which days were refused and why.", f =>
        {
            Refused(f, "2026-11-02", "2026-11-02");
            f["sent"]!.AsArray()[0]!["query"]!.AsArray()[3]!.AsArray()[1] = "Nov 01 2026 235959";
        }),
    });

    private static Dictionary<string, Deviation> Refreshing(Dictionary<string, Deviation> d)
    {
        foreach (var name in new[] { "history_refused", "history_one_chunk_refused" }) d[name] = new(NoRefresh + " " + d[name].Reason, DropRefresh(d[name].Adjust!));
        return d;
    }

    public static IEnumerable<object[]> Names() => ContractRunner.Names("history_rmoney.fixtures.json");

    [PostgresTheory]
    [MemberData(nameof(Names))]
    public Task Matches_the_python_implementation(string name) => RunAsync(host, name, Deviations, output);
}
