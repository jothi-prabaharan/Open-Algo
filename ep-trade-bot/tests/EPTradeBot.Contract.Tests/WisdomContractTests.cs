using System.Text.Json.Nodes;
using EPTradeBot.IntegrationTests;
using Xunit.Abstractions;
using static EPTradeBot.Contract.Tests.ContractRunner;

namespace EPTradeBot.Contract.Tests;

public sealed class WisdomHost : BrokerContractHost
{
    public override string BrokerName => "wisdom";
    public override string FixtureFile => "wisdom.fixtures.json";

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

/// <summary>Wisdom Capital (XTS) against the Python implementation (tools/contract-fixtures/wisdom.fixtures.json).</summary>
public sealed class WisdomContractTests(WisdomHost host, ITestOutputHelper output) : IClassFixture<WisdomHost>
{
    private const string AsText = "Wisdom Capital sends the order number as a number and OpenAlgo passes it on as one. EP Trade bot's order ids are text on every broker.";

    private static Action<JsonObject> IdAsText => f =>
    {
        if (f["expected"]!["body"]!["orderid"] is { } id) f["expected"]!["body"]!["orderid"] = id.ToString();
    };

    public static readonly Dictionary<string, Deviation> Deviations = new()
    {
        ["placeorder_market"] = new(AsText, IdAsText),
        ["placeorder_limit_cnc"] = new(AsText, IdAsText),
        ["placeorder_sl_futures"] = new(AsText, IdAsText),
        ["placeorder_slm_option"] = new(AsText, IdAsText),
        ["placeorder_mcx"] = new(AsText, IdAsText),
        ["placeorder_currency"] = new(AsText, IdAsText),
        ["placesmartorder_reduce"] = new(
            "OpenAlgo looks for the open position under keys Wisdom Capital's reply does not have (status, data), so it never sees one: asked to bring a 10-share long to 4, it BUYS 4 more. EP Trade bot reads the position and SELLS 6. Order ids are text.",
            f =>
            {
                IdAsText(f);
                var w = Write(f, "POST")["json"]!;
                w["orderSide"] = "SELL";
                w["orderQuantity"] = "6";
            }),
        ["cancelallorder"] = new(
            "OpenAlgo lists a stop order waiting for its trigger as \"Trigger Pending\" in the order book; EP Trade bot's cancel-all looks for \"trigger pending\", so the book says that. Order ids are text.",
            f =>
            {
                var body = f["expected"]!["body"]!;
                body["canceled_orders"] = new JsonArray("2001", "2003", "2007");
            }),
        ["orderbook"] = new(
            "A stop order waiting for its trigger is \"trigger pending\" in the order book, the spelling the cancel-all and order-status code read.",
            f =>
            {
                foreach (var o in f["expected"]!["body"]!["data"]!["orders"]!.AsArray().OfType<JsonObject>().Where(o => o["order_status"]!.GetValue<string>() == "Trigger Pending")) o["order_status"] = "trigger pending";
            }),
        ["closeposition"] = new(
            "A position whose symbol is not in the master contract (UNLISTED) cannot be closed by symbol; OpenAlgo sends its instrument id straight to Wisdom Capital. EP Trade bot says which position is still open.",
            f =>
            {
                Expect(f, 500, Error("1 position(s) could not be squared off and are still open: UNLISTED (NSE): UNLISTED on NSE is not in the Wisdom Capital master contract. Check the symbol, or download the master contract again."));
                var sent = f["sent"]!.AsArray();
                sent.RemoveAt(sent.Count - 1);
            }),
        ["placeorder_error_type_carrying_an_order_number"] = new(
            "OpenAlgo answers success with no order id when Wisdom Capital's reply is an error. EP Trade bot refuses with the reason.",
            f => Expect(f, 500, Error("Rejected by risk"))),
        ["modifyorder_refused"] = new(
            "OpenAlgo drops Wisdom Capital's reason on a refused modify. EP Trade bot passes it on.",
            f => f["expected"]!["body"]!["message"] = "Order already filled"),
        ["funds_session_code_on_a_bad_request"] = new(
            "OpenAlgo shows an empty balance when Wisdom Capital refuses the session. EP Trade bot says the session has expired, so nothing is mistaken for a balance.",
            f => Expect(f, 403, Error("Wisdom Capital says the session has expired. Log in to Wisdom Capital again."))),
        ["funds_session_expired"] = new(
            "OpenAlgo shows an empty balance when Wisdom Capital refuses the session. EP Trade bot says the session has expired, so nothing is mistaken for a balance.",
            f => Expect(f, 403, Error("Wisdom Capital says the session has expired. Log in to Wisdom Capital again."))),
        ["placeorder_unknown_symbol"] = new(
            "OpenAlgo sends exchangeInstrumentID null to Wisdom Capital for a symbol missing from the master contract. EP Trade bot refuses before sending anything.",
            Status: 400),
        ["placeorder_broker_refuses"] = new(
            "OpenAlgo answers \"Failed to place order\" and drops Wisdom Capital's reason. EP Trade bot passes the reason on.",
            f => f["expected"]!["body"]!["message"] = "Insufficient funds"),
        ["cancelorder_refused"] = new(
            "OpenAlgo drops Wisdom Capital's reason on a refused cancel. EP Trade bot passes it on.",
            f => f["expected"]!["body"]!["message"] = "Order cannot be cancelled"),
    };

    public static IEnumerable<object[]> Names() => ContractRunner.Names("wisdom.fixtures.json");

    [PostgresTheory]
    [MemberData(nameof(Names))]
    public Task Matches_the_python_implementation(string name) => RunAsync(host, name, Deviations, output);
}
