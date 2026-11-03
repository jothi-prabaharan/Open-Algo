using System.Text.Json.Nodes;
using EPTradeBot.IntegrationTests;
using Xunit.Abstractions;
using static EPTradeBot.Contract.Tests.ContractRunner;

namespace EPTradeBot.Contract.Tests;

public abstract class DhanFamilyHost : BrokerContractHost
{
    public override IReadOnlyDictionary<(string, string), decimal> Prices { get; } = new Dictionary<(string, string), decimal>
    {
        [("NSE", "SBIN")] = 805m, [("NSE", "INFY")] = 1500m, [("NFO", "NIFTY25NOV26FUT")] = 24000m,
        [("NFO", "NIFTY25NOV2624000CE")] = 120m, [("NSE", "NOTICK")] = 50m,
    };

    public override IReadOnlyDictionary<string, string> HarnessReplies { get; } = new Dictionary<string, string> { ["GET /v2/positions"] = "[]" };
}

public sealed class DhanHost : DhanFamilyHost
{
    public override string BrokerName => "dhan";
    public override string FixtureFile => "dhan.fixtures.json";
}

public sealed class DhanSandboxHost : DhanFamilyHost
{
    public override string BrokerName => "dhan_sandbox";
    public override string FixtureFile => "dhan_sandbox.fixtures.json";

    /// <summary>A fresh reference per order, and today's date on candle requests.</summary>
    public override IReadOnlyDictionary<string, string> VolatileFields { get; } = new Dictionary<string, string>
    {
        ["json.correlationId"] = "^[0-9a-f]{8}$", ["json.fromDate"] = @"^\d{4}-\d{2}-\d{2}$", ["json.toDate"] = @"^\d{4}-\d{2}-\d{2}$",
    };
}

/// <summary>Differences shared by Dhan and Dhan Sandbox.</summary>
internal static class DhanDeviations
{
    public static void DropRead(JsonObject fixture, string path)
    {
        var sent = fixture["sent"]!.AsArray();
        foreach (var s in sent.OfType<JsonObject>().Where(s => s["path"]!.GetValue<string>() == path).ToList()) sent.Remove(s);
    }

    public static Dictionary<string, Deviation> Common(string display) => new()
    {
        ["placeorder_unknown_symbol"] = new(
            $"OpenAlgo sends securityId null to {display} for a symbol missing from the master contract. EP Trade bot refuses before sending anything.",
            Status: 400),
        ["placeorder_broker_refuses"] = new(
            $"OpenAlgo answers \"Failed to place order\" and drops {display}'s reason. EP Trade bot passes the reason on.",
            f => f["expected"]!["body"]!["message"] = "Incorrect order request: insufficient funds"),
        ["cancelorder_refused"] = new(
            $"OpenAlgo reports any reply to a cancel as success, including {display}'s refusal. EP Trade bot reports the refusal.",
            f => Expect(f, 400, Error("Order is already traded"))),
        ["closeposition_one_exit_refused"] = new(
            "OpenAlgo reports every position squared off even when an exit order is refused. EP Trade bot says which position is still open.",
            f => Expect(f, 500, Error("1 position(s) could not be squared off and are still open: NIFTY25NOV26FUT (NFO): Incorrect order request: insufficient funds"))),
        ["funds_token_expired"] = new(
            $"OpenAlgo shows zero cash when {display} refuses the token. EP Trade bot says the token has expired, so zero is never mistaken for a balance.",
            f =>
            {
                Expect(f, 403, Error($"{display} says the access token is invalid or has expired. Log in to {display} again."));
                DropRead(f, "/v2/positions");
            }),
    };
}

/// <summary>Dhan against the Python implementation (tools/contract-fixtures/dhan.fixtures.json).</summary>
public sealed class DhanContractTests(DhanHost host, ITestOutputHelper output) : IClassFixture<DhanHost>
{
    private const string ClientId = "1100000001";

    public static readonly Dictionary<string, Deviation> Deviations = Build();

    private static Dictionary<string, Deviation> Build()
    {
        var d = DhanDeviations.Common("Dhan");
        const string key = "OpenAlgo sends the whole BROKER_API_KEY value (client id, \":::\" and the app key) as dhanClientId on a modify, so the app key leaves the server. EP Trade bot sends the client id.";
        d["modifyorder_limit"] = new(key, f => Write(f, "PUT")["json"]!["dhanClientId"] = ClientId);
        d["modifyorder_slm"] = new(key, f => Write(f, "PUT")["json"]!["dhanClientId"] = ClientId);
        d["modifyorder_refused"] = new(key + " OpenAlgo also fails with an internal error on a refused modify; EP Trade bot passes Dhan's reason on.", f =>
        {
            Write(f, "PUT")["json"]!["dhanClientId"] = ClientId;
            Expect(f, 400, Error("Order is already traded"));
        });
        d["placeorder_slm_no_tick_size"] = new(
            "Same refusal and status as OpenAlgo; EP Trade bot says why instead of \"internal error\".",
            f => f["expected"]!["body"]!["message"] = "The tick size of NOTICK on NSE is not in the Dhan master contract, so a protective price for the stop-loss-market order cannot be set. Download the master contract again, or use a stop-loss limit order.");
        d["quotes_not_subscribed"] = new(
            "OpenAlgo answers success with every price 0 when market data is not subscribed. EP Trade bot answers with the reason, so 0 is never read as a price.",
            f => Expect(f, 500, Error("Dhan says market data is not subscribed for this account. Subscribe to Dhan's data APIs, then try again.")));
        return d;
    }

    public static IEnumerable<object[]> Names() => ContractRunner.Names("dhan.fixtures.json");

    [PostgresTheory]
    [MemberData(nameof(Names))]
    public Task Matches_the_python_implementation(string name) => RunAsync(host, name, Deviations, output);
}

/// <summary>Dhan Sandbox against the Python implementation (tools/contract-fixtures/dhan_sandbox.fixtures.json).</summary>
public sealed class DhanSandboxContractTests(DhanSandboxHost host, ITestOutputHelper output) : IClassFixture<DhanSandboxHost>
{
    public static readonly Dictionary<string, Deviation> Deviations = Build();

    /// <summary>From the recorded candles: last close, first open, highest high, lowest positive low, total volume.</summary>
    private static JsonObject CandleQuote() => new()
    {
        ["ltp"] = 805.9, ["open"] = 805.0, ["high"] = 806.5, ["low"] = 804.5, ["volume"] = 2400, ["oi"] = 0, ["bid"] = 0, ["ask"] = 0, ["prev_close"] = 0,
    };

    private static Dictionary<string, Deviation> Build()
    {
        var d = DhanDeviations.Common("Dhan Sandbox");
        const string invented = "OpenAlgo's sandbox plugin moves every price by pseudo-random noise, invents open interest from a hash of the security id, and invents a whole quote when the sandbox has none. EP Trade bot reports the candles as they are and never invents a price.";
        d["quotes"] = new(invented, f => Expect(f, 200, new JsonObject { ["status"] = "success", ["data"] = CandleQuote() }));
        d["quotes_futures"] = new(invented, f => Expect(f, 200, new JsonObject { ["status"] = "success", ["data"] = CandleQuote() }));
        d["depth"] = new(invented, f =>
        {
            static JsonArray Empty() => new(Enumerable.Range(0, 5).Select(_ => (JsonNode?)new JsonObject { ["price"] = 0, ["quantity"] = 0 }).ToArray());
            Expect(f, 200, new JsonObject
            {
                ["status"] = "success",
                ["data"] = new JsonObject
                {
                    ["asks"] = Empty(), ["bids"] = Empty(), ["high"] = 806.5, ["low"] = 804.5, ["ltp"] = 805.9, ["ltq"] = 0, ["oi"] = 0,
                    ["open"] = 805.0, ["prev_close"] = 0, ["totalbuyqty"] = 0, ["totalsellqty"] = 0, ["volume"] = 2400,
                },
            });
        });
        var none = "Dhan Sandbox has no price for SBIN today. The sandbox only has prices while its market is open.";
        d["quotes_not_subscribed"] = new(invented, f => Expect(f, 500, Error(none)));
        d["quotes_no_candles"] = new(invented, f => Expect(f, 500, Error(none)));
        d["cancelallorder_working_states"] = new(
            "OpenAlgo's cancel-all skips a part-traded order, although its order book shows it open and its remainder can still fill. EP Trade bot cancels it too.",
            f =>
            {
                var sent = f["sent"]!.AsArray();
                var delete = (JsonObject)Write(f, "DELETE", 2).DeepClone();
                delete["path"] = "/v2/orders/1007";
                sent.Add(delete);
                f["expected"]!["body"]!["canceled_orders"]!.AsArray().Add("1007");
                f["expected"]!["body"]!["message"] = "Canceled 4 orders. Failed to cancel 0 orders.";
            });
        return d;
    }

    public static IEnumerable<object[]> Names() => ContractRunner.Names("dhan_sandbox.fixtures.json");

    [PostgresTheory]
    [MemberData(nameof(Names))]
    public Task Matches_the_python_implementation(string name) => RunAsync(host, name, Deviations, output);
}
