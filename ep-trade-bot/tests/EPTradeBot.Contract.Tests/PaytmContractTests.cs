using System.Text.Json.Nodes;
using EPTradeBot.IntegrationTests;
using Xunit.Abstractions;
using static EPTradeBot.Contract.Tests.ContractRunner;

namespace EPTradeBot.Contract.Tests;

public sealed class PaytmHost : BrokerContractHost
{
    public override string BrokerName => "paytm";
    public override string FixtureFile => "paytm.fixtures.json";
    public override bool NumbersMayBeText => true;
    public override IReadOnlyDictionary<string, string> Keys { get; } = new Dictionary<string, string> { ["ApiKey"] = "paytm-api-key", ["ApiSecret"] = "paytm-api-secret" };

    public override IReadOnlyDictionary<(string, string), decimal> Prices { get; } = new Dictionary<(string, string), decimal>
    {
        [("NSE", "SBIN")] = 805m, [("NSE", "INFY")] = 1500m, [("BSE", "INFY")] = 1500m, [("NFO", "NIFTY25NOV26FUT")] = 24000m,
    };

    public override IReadOnlyDictionary<string, string> HarnessReplies { get; } = new Dictionary<string, string>
    {
        ["GET /orders/v1/position"] = """{"status":"success","data":[]}""",
    };

    public override IReadOnlyDictionary<string, string> Settings { get; } = new Dictionary<string, string> { ["Brokers:RetryBackoffMs:paytm"] = "0" };
}

/// <summary>Paytm Money against the Python implementation (tools/contract-fixtures/paytm.fixtures.json).</summary>
public sealed class PaytmContractTests(PaytmHost host, ITestOutputHelper output) : IClassFixture<PaytmHost>
{
    private static Action<JsonObject> Refusal(int status, string message) => f => Expect(f, status, Error(message));

    /// <summary>Changes one field of the first write OpenAlgo sent.</summary>
    private static Action<JsonObject> Sent(string field, JsonNode? value, params (string Field, JsonNode? Value)[] more) => f =>
    {
        var json = f["sent"]!.AsArray().OfType<JsonObject>().First(s => s["path"]!.GetValue<string>() == "/orders/v1/place/regular")["json"]!.AsObject();
        json[field] = value;
        foreach (var (name, v) in more) json[name] = v;
    };

    public static readonly Dictionary<string, Deviation> Deviations = new()
    {
        ["placeorder_unknown_symbol"] = new("OpenAlgo sends security_id null to Paytm Money for a symbol missing from the master contract. EP Trade bot refuses before sending anything.", Status: 400),
        ["placeorder_futures_nrml"] = new("OpenAlgo has no entry for NRML and sends product null, which Paytm Money refuses. EP Trade bot sends \"M\", Paytm Money's margin product.", Sent("product", "M")),
        ["placeorder_stop_loss_with_trigger"] = new(
            "OpenAlgo looks the order type up under names Paytm Money's API does not use, so a stop-loss order goes out as a MARKET order, and it never sends the trigger price. EP Trade bot sends SL with its trigger price.",
            Sent("order_type", "SL", ("trigger_price", "801.5"))),
        ["placeorder_stop_loss_market"] = new(
            "OpenAlgo sends a stop-loss-market order as a MARKET order, with no trigger price. EP Trade bot sends SLM with its trigger price.",
            Sent("order_type", "SLM", ("trigger_price", "801.5"))),
        ["cancelorder_not_found"] = new("OpenAlgo fails with \"Failed to cancel order due to internal error\" for an order that is not in the book. EP Trade bot says so.", Refusal(404, "Order 999 not found")),
        ["cancelorder_already_complete"] = new("OpenAlgo fails with \"Failed to cancel order due to internal error\" for an order that is not pending. EP Trade bot says what state it is in.",
            Refusal(400, "Order 2001000000001 is successful, so it cannot be cancelled.")),
        ["cancelorder_refused"] = new("Same status; EP Trade bot passes Paytm Money's reason on.", Refusal(500, "Insufficient funds to place this order")),
        ["orderbook_empty"] = new("OpenAlgo fails with a Python error when Paytm Money sends no orders at all. EP Trade bot shows an empty book.", f =>
            Expect(f, 200, new JsonObject
            {
                ["status"] = "success",
                ["data"] = new JsonObject
                {
                    ["orders"] = new JsonArray(),
                    ["statistics"] = new JsonObject { ["total_buy_orders"] = 0, ["total_sell_orders"] = 0, ["total_completed_orders"] = 0, ["total_open_orders"] = 0, ["total_rejected_orders"] = 0 },
                },
            })),
        ["placesmartorder_futures_nrml_short_to_flat"] = new("OpenAlgo sends product null for NRML, which Paytm Money refuses. EP Trade bot sends \"M\".", Sent("product", "M")),
        ["cancelallorder"] = new(
            "OpenAlgo cancels only orders whose status is \"Pending\" and leaves a resting \"Open\" order alone. EP Trade bot cancels every order that can still be changed, so it cancels the open stop order too.",
            f =>
            {
                var book = f["broker"]!["GET /orders/v1/user/orders"]!["body"]!["data"]!.AsArray().OfType<JsonObject>().First(o => o["order_no"]!.GetValue<string>() == "2001000000004");
                var cancel = (JsonObject)f["sent"]!.AsArray().OfType<JsonObject>().First(s => s["path"]!.GetValue<string>() == "/orders/v1/cancel/regular").DeepClone();
                var body = cancel["json"]!.AsObject();
                foreach (var field in new[] { "txn_type", "exchange", "segment", "product", "security_id", "quantity", "validity", "order_type", "price", "off_mkt_flag", "mkt_type", "serial_no", "group_id" })
                    body[field] = book[field]!.DeepClone();
                body["order_no"] = "2001000000004";
                f["sent"]!.AsArray().Add(cancel);
                var expected = f["expected"]!["body"]!;
                expected["canceled_orders"]!.AsArray().Add("2001000000004");
                expected["message"] = "Canceled 2 orders. Failed to cancel 0 orders.";
            }),
        ["quotes_no_data"] = new("Same status; the reason is worded for the trader.", Refusal(500, "Paytm Money has no quote for SBIN right now.")),
        ["tradebook"] = new(
            "Paytm Money has no trade book, so OpenAlgo lists every order of the day as a trade, pending and rejected ones too, with a price of 0. EP Trade bot lists the orders that traded.",
            f =>
            {
                var rows = f["expected"]!["body"]!["data"]!.AsArray();
                foreach (var row in rows.OfType<JsonObject>().Where(r => r["average_price"]!.GetValue<double>() <= 0).ToList()) rows.Remove(row);
            }),
        ["positionbook"] = new("OpenAlgo calls Paytm Money's margin product MARGIN, which no order matches. EP Trade bot calls it NRML.", f =>
        {
            foreach (var row in f["expected"]!["body"]!["data"]!.AsArray().OfType<JsonObject>().Where(r => r["product"]!.GetValue<string>() == "MARGIN")) row["product"] = "NRML";
        }),
    };

    public static IEnumerable<object[]> Names() => ContractRunner.Names("paytm.fixtures.json");

    [PostgresTheory]
    [MemberData(nameof(Names))]
    public Task Matches_the_python_implementation(string name) => RunAsync(host, name, Deviations, output);
}
