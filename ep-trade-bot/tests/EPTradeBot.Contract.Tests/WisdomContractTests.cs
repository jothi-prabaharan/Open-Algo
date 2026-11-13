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
