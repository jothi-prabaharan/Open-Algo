using System.Text.Json.Nodes;
using EPTradeBot.IntegrationTests;
using Xunit.Abstractions;
using static EPTradeBot.Contract.Tests.ContractRunner;

namespace EPTradeBot.Contract.Tests;

public sealed class AngelHost : BrokerContractHost
{
    public override string BrokerName => "angel";
    public override string FixtureFile => "angel.fixtures.json";
    public override bool NumbersMayBeText => true;

    /// <summary>SmartAPI's private key, sent as X-PrivateKey (BROKER_API_KEY in the recording).</summary>
    public override IReadOnlyDictionary<string, string> Keys { get; } = new Dictionary<string, string> { ["ApiKey"] = "angel-private-key" };

    public override IReadOnlyDictionary<(string, string), decimal> Prices { get; } = new Dictionary<(string, string), decimal>
    {
        [("NSE", "SBIN")] = 805m, [("NSE", "INFY")] = 1500m, [("NFO", "NIFTY25NOV26FUT")] = 24000m,
    };

    public override IReadOnlyDictionary<string, string> HarnessReplies { get; } = new Dictionary<string, string>
    {
        ["GET /rest/secure/angelbroking/order/v1/getPosition"] = """{"status":true,"message":"SUCCESS","errorcode":"","data":[]}""",
    };

    /// <summary>A fresh tag per order, used to find an order whose reply was lost.</summary>
    public override IReadOnlyDictionary<string, string> VolatileFields { get; } = new Dictionary<string, string> { ["json.ordertag"] = "^oa[0-9a-f]{16}$" };
}

public sealed class UpstoxHost : BrokerContractHost
{
    public override string BrokerName => "upstox";
    public override string FixtureFile => "upstox.fixtures.json";
    public override bool NumbersMayBeText => true;

    public override IReadOnlyDictionary<(string, string), decimal> Prices { get; } = new Dictionary<(string, string), decimal>
    {
        [("NSE", "SBIN")] = 805m, [("NSE", "INFY")] = 1500m, [("NFO", "NIFTY25NOV26FUT")] = 24000m,
    };

    public override IReadOnlyDictionary<string, string> HarnessReplies { get; } = new Dictionary<string, string>
    {
        ["GET /v2/portfolio/short-term-positions"] = """{"status":"success","data":[]}""",
    };
}

/// <summary>Angel One against the Python implementation (tools/contract-fixtures/angel.fixtures.json).</summary>
public sealed class AngelContractTests(AngelHost host, ITestOutputHelper output) : IClassFixture<AngelHost>
{
    private const string Expired = "Angel One says the session has expired. Log in to Angel One again.";

    public static readonly Dictionary<string, Deviation> Deviations = new()
    {
        ["placeorder_unknown_symbol"] = new("OpenAlgo sends tradingsymbol and symboltoken null to Angel One for a symbol missing from the master contract. EP Trade bot refuses before sending anything.", Status: 400),
        ["placeorder_ambiguous_reply"] = new("Same status, the same order-book check for the order's tag, and the same conclusion; EP Trade bot words it for a trader.",
            f => f["expected"]!["body"]!["message"] = "Angel One sent no usable reply and the order is not in the order book yet, so it may or may not have been placed. Check the order book on Angel One before trying again."),
        ["modifyorder_refused"] = new("OpenAlgo reports a refused modify as success, because Angel One answers it with HTTP 200. EP Trade bot reports the refusal.",
            f => Expect(f, 400, Error("Order is already complete"))),
        ["cancelorder_refused"] = new("OpenAlgo reports a refused cancel as success, because Angel One answers it with HTTP 200. EP Trade bot reports the refusal.",
            f => Expect(f, 400, Error("Order is already complete"))),
        ["closeposition_one_exit_refused"] = new("OpenAlgo reports every position squared off even when an exit order is refused. EP Trade bot says which position is still open.",
            f => Expect(f, 500, Error("1 position(s) could not be squared off and are still open: NIFTY25NOV26FUT (NFO): Order rejected: insufficient margin"))),
        ["tradebook"] = new("SmartAPI reports a trade's size as fillsize; OpenAlgo reads a quantity field SmartAPI does not send, so every Angel One trade showed quantity 0.",
            f =>
            {
                var rows = f["expected"]!["body"]!["data"]!.AsArray();
                rows[0]!["quantity"] = 5;
                rows[1]!["quantity"] = 75;
            }),
        ["funds_token_expired"] = new("OpenAlgo answers success with no funds when Angel One refuses the token. EP Trade bot says the session has expired.",
            f => Expect(f, 403, Error(Expired))),
        ["quotes_refused"] = new("Same refusal; EP Trade bot names the cause and the next step instead of the API's text.", f => Expect(f, 403, Error(Expired))),
    };

    public static IEnumerable<object[]> Names() => ContractRunner.Names("angel.fixtures.json");

    [PostgresTheory]
    [MemberData(nameof(Names))]
    public Task Matches_the_python_implementation(string name) => RunAsync(host, name, Deviations, output);
}

/// <summary>Upstox against the Python implementation (tools/contract-fixtures/upstox.fixtures.json).</summary>
public sealed class UpstoxContractTests(UpstoxHost host, ITestOutputHelper output) : IClassFixture<UpstoxHost>
{
    private const string Expired = "Upstox says the access token is invalid or has expired. Log in to Upstox again.";

    public static readonly Dictionary<string, Deviation> Deviations = new()
    {
        ["placeorder_unknown_symbol"] = new("OpenAlgo fails with an internal error for a symbol missing from the master contract. EP Trade bot refuses it plainly.", Status: 400),
        ["modifyorder_refused"] = new("Same refusal and status; OpenAlgo drops Upstox's reason, EP Trade bot passes it on.",
            f => f["expected"]!["body"]!["message"] = "UDAPI100049: Order is already complete"),
        ["cancelorder_refused"] = new("Same refusal and status; OpenAlgo drops Upstox's reason, EP Trade bot passes it on.",
            f => f["expected"]!["body"]!["message"] = "UDAPI100049: Order is already complete"),
        ["cancelallorder"] = new("OpenAlgo's cancel-all skips an order waiting on a modify, although its order book shows it open and it can still fill. EP Trade bot cancels it too.",
            f =>
            {
                var delete = (JsonObject)Write(f, "DELETE", 1).DeepClone();
                delete["query"] = new JsonArray(new JsonArray("order_id", "3005"));
                f["sent"]!.AsArray().Add(delete);
                f["expected"]!["body"]!["canceled_orders"]!.AsArray().Add("3005");
                f["expected"]!["body"]!["message"] = "Canceled 3 orders. Failed to cancel 0 orders.";
            }),
        ["closeposition_one_exit_refused"] = new("OpenAlgo reports every position squared off when Upstox refuses an exit (it counts only rate-limited exits). EP Trade bot says which position is still open.",
            f => Expect(f, 500, Error("1 position(s) could not be squared off and are still open: NIFTY25NOV26FUT (NFO): UDAPI100500: Insufficient funds"))),
        ["placesmartorder_matched"] = new("OpenAlgo answers a position already at its target with an error saying no position is open. EP Trade bot answers success, as for every other broker.",
            f => Expect(f, 200, new JsonObject { ["status"] = "success", ["message"] = "Positions Already Matched. No Action needed." })),
        ["holdings_unpriced"] = new("A holding Upstox cannot price has pnl null; OpenAlgo's sum then fails the whole holdings request. EP Trade bot counts it as 0, and shows its P&L percent as 0, not a -100% loss.",
            f => Expect(f, 200, new JsonObject
            {
                ["status"] = "success",
                ["data"] = new JsonObject
                {
                    ["holdings"] = new JsonArray(
                        new JsonObject { ["symbol"] = "INFY", ["exchange"] = "NSE", ["quantity"] = 20, ["product"] = "CNC", ["average_price"] = 1400.0, ["ltp"] = 1499.0, ["pnl"] = 1980.0, ["pnlpercent"] = 7.07 },
                        new JsonObject { ["symbol"] = "OLDCO", ["exchange"] = "NSE", ["quantity"] = 3, ["product"] = "CNC", ["average_price"] = 12.5, ["ltp"] = 0.0, ["pnl"] = 0.0, ["pnlpercent"] = 0.0 }),
                    ["statistics"] = new JsonObject { ["totalholdingvalue"] = 29980.0, ["totalinvvalue"] = 28037.5, ["totalprofitandloss"] = 1980.0, ["totalpnlpercentage"] = 7.06 },
                },
            })),
        ["funds_outside_hours"] = new("OpenAlgo shows zero cash outside Upstox's funds hours. EP Trade bot says funds are unavailable until the service opens, so zero is never mistaken for a balance.",
            f => Expect(f, 503, Error("Upstox reports funds only from 5:30 AM to midnight IST. Try again within those hours."))),
        ["funds_token_expired"] = new("OpenAlgo answers success with no funds when Upstox refuses the token. EP Trade bot says the token has expired.",
            f => Expect(f, 403, Error(Expired))),
        ["quotes_refused"] = new("Same refusal; EP Trade bot names the cause and the next step instead of the API's code.", f => Expect(f, 403, Error(Expired))),
    };

    public static IEnumerable<object[]> Names() => ContractRunner.Names("upstox.fixtures.json");

    [PostgresTheory]
    [MemberData(nameof(Names))]
    public Task Matches_the_python_implementation(string name) => RunAsync(host, name, Deviations, output);
}
