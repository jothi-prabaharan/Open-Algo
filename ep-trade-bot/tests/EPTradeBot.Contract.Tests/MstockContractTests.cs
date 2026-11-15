using System.Text.Json.Nodes;
using EPTradeBot.IntegrationTests;
using Xunit.Abstractions;
using static EPTradeBot.Contract.Tests.ContractRunner;

namespace EPTradeBot.Contract.Tests;

public abstract class MstockFileHost(string file) : BrokerContractHost
{
    public override string BrokerName => "mstock";
    public override string FixtureFile => file;
    public override IReadOnlyDictionary<string, string> Keys { get; } = new Dictionary<string, string> { ["ApiKey"] = "ms-client-code", ["ApiSecret"] = "ms-private-key" };
    public override IReadOnlyDictionary<(string, string), decimal> Prices { get; } = new Dictionary<(string, string), decimal>
    {
        [("NSE", "SBIN")] = 805m, [("NSE", "INFY")] = 1500m, [("BSE", "TATAMOTORS")] = 500m, [("NFO", "NIFTY25NOV26FUT")] = 24000m, [("BFO", "SENSEX26NOVFUT")] = 80000m, [("MCX", "GOLDM25NOV26FUT")] = 75000m, [("CDS", "USDINR25NOV26FUT")] = 83.5m,
    };
    public override IReadOnlyDictionary<string, string> HarnessReplies { get; } = new Dictionary<string, string> {
        ["GET /openapi/typeb/portfolio/positions"] = """{"status":true,"data":[]}""",
        ["DELETE /openapi/typeb/orders/regular/MS1"] = """{"status":"true","message":"SUCCESS"}""",
        ["DELETE /openapi/typeb/orders/regular/MS2"] = """{"status":"true","message":"SUCCESS"}""",
        ["DELETE /openapi/typeb/orders/regular/MS4"] = """{"status":"true","message":"SUCCESS"}""",
    };
    public override bool NumbersMayBeText => true;
    public override IReadOnlyDictionary<string, string> Settings { get; } = new Dictionary<string, string> { ["Brokers:RetryBackoffMs:mstock"] = "0", ["OpenAlgoApi:HistorySpacingMs"] = "0", ["Brokers:HistoryGapMs:mstock"] = "0" };
}

public sealed class MstockHost() : MstockFileHost("mstock.fixtures.json");

/// <summary>mStock orders, books, positions and funds (tools/contract-fixtures/mstock.fixtures.json).</summary>
public sealed class MstockContractTests(MstockHost host, ITestOutputHelper output) : IClassFixture<MstockHost>
{
    private const string Words = "OpenAlgo shows a failed read as an empty list or an empty balance. EP Trade bot says what mStock did and what to do.";

    private static Deviation Says(int status, string message) => new(Words, f => Expect(f, status, Error(message)));

    private static void Products(JsonArray rows, params string[] symbols)
    {
        foreach (var row in rows.OfType<JsonObject>().Where(r => symbols.Contains((string?)r["symbol"]))) row["product"] = "NRML";
    }

    private const string NoNumber = "mStock accepted the order but sent no order number, so it cannot be followed. Check the order book on mStock before trying again.";

    private const string Missing = "NOPE on NSE is not in the mStock master contract. Check the symbol, or download the master contract again.";

    /// <summary>OpenAlgo cancels everything with one call to mStock's own cancel-all. EP Trade bot cancels the open orders one by one, as it does for every broker, so each is told apart in the answer.</summary>
    private static Deviation PerOrder(params string[] ids) => new("OpenAlgo cancels all orders with mStock's one cancel-all call. EP Trade bot cancels each open order on its own and reports each.", f =>
    {
        var sent = f["sent"]!.AsArray();
        var bulk = sent.OfType<JsonObject>().First(s => s["path"]!.GetValue<string>().EndsWith("/orders/cancelall", StringComparison.Ordinal));
        sent.Remove(bulk);
        foreach (var id in ids)
        {
            sent.Add(new JsonObject
            {
                ["method"] = "DELETE", ["host"] = bulk["host"]!.DeepClone(), ["path"] = $"/openapi/typeb/orders/regular/{id}", ["query"] = new JsonArray(), ["headers"] = bulk["headers"]!.DeepClone(),
                ["json"] = new JsonObject { ["variety"] = "NORMAL", ["orderid"] = id },
            });
        }

        Expect(f, 200, new JsonObject
        {
            ["status"] = "success", ["canceled_orders"] = new JsonArray(ids.Select(i => (JsonNode)i).ToArray()), ["failed_cancellations"] = new JsonArray(), ["message"] = $"Canceled {ids.Length} orders. Failed to cancel 0 orders.",
        });
    });

    public static readonly Dictionary<string, Deviation> Deviations = new()
    {
        ["placeorder_success_without_an_order_id"] = new("OpenAlgo answers a refusal with a generic sentence when mStock accepts an order and sends no number. EP Trade bot says it may have been placed and to check the order book.", f => Expect(f, 502, Error(NoNumber))),
        ["placeorder_success_with_no_data"] = new("Same.", f => Expect(f, 502, Error(NoNumber))),
        ["placeorder_data_that_is_not_an_object"] = new("Same.", f => Expect(f, 502, Error(NoNumber))),
        ["placeorder_unknown_symbol"] = new("OpenAlgo sends the order with no symbol token and reports it placed. The platform names the symbol and says what to do, and sends nothing.",
            f => { Expect(f, 400, Error("NOPE on NSE is not in the mStock by Mirae Asset master contract. Check the symbol, or download the master contract again.")); f["sent"]!.AsArray().Clear(); }),
        ["modifyorder_unknown_symbol"] = new("OpenAlgo says the symbol token is missing from its database. EP Trade bot names the symbol and says what to do.", f => Expect(f, 400, Error(Missing))),
        ["modifyorder_not_json"] = new("OpenAlgo answers an internal error when mStock's reply is not JSON. EP Trade bot answers a refusal that says what was asked.", f => Expect(f, 400, Error("Failed to modify order"))),
        ["cancelorder_not_json"] = new("Same.", f => Expect(f, 400, Error("Failed to cancel order"))),
        ["cancelallorder"] = PerOrder("MS1", "MS2", "MS4"),
        ["cancelallorder_reply_status_success"] = PerOrder("MS1", "MS2", "MS4"),
        ["cancelallorder_reply_message_success"] = PerOrder("MS1", "MS2", "MS4"),
        ["cancelallorder_refused"] = PerOrder("MS1", "MS2", "MS4"),
        ["cancelallorder_not_json"] = PerOrder("MS1", "MS2", "MS4"),
        ["cancelallorder_book_failed"] = new("OpenAlgo cancels nothing and reports success when the order book cannot be read. EP Trade bot reports the failure.", f => Expect(f, 500, Error("Failed to cancel all orders due to internal error"))),
        ["closeposition"] = new("OpenAlgo skips a position whose symbol it cannot find and still reports every position squared off. EP Trade bot names the position it could not close and sends no order for it.",
            f =>
            {
                Expect(f, 500, Error("1 position(s) could not be squared off and are still open: NOSUCH-EQ (NSE): NOSUCH-EQ on NSE is not in the mStock master contract. Check the symbol, or download the master contract again."));
                // An order that carries no price goes with the prices as numbers (0.0); OpenAlgo's square-off leaves them out and sends the text 0.
                foreach (var call in f["sent"]!.AsArray().OfType<JsonObject>().Where(c => c["method"]!.GetValue<string>() == "POST"))
                {
                    call["json"]!["price"] = "0.0";
                    call["json"]!["triggerprice"] = "0.0";
                }
            }),
        ["openposition_short_is_negative"] = new("OpenAlgo compares the cash exchange mStock names for a future with the derivatives exchange it was asked about, and finds no position. EP Trade bot finds it.",
            f => Expect(f, 200, new JsonObject { ["status"] = "success", ["quantity"] = -75 })),
        ["positionbook"] = new("OpenAlgo leaves CARRYFORWARD on a future mStock names on the cash exchange, a product no order can carry. EP Trade bot shows NRML.", f => Products(f["expected"]!["body"]!["data"]!.AsArray(), "NIFTY25NOV26FUT", "SENSEX26NOVFUT")),
        ["orderbook"] = new("Same.", f => Products(f["expected"]!["body"]!["data"]!["orders"]!.AsArray(), "NIFTY25NOV26FUT", "SENSEX26NOVFUT")),
        ["orderbook_failed"] = Says(500, "mStock could not send the order book: Session expired"),
        ["orderbook_not_json"] = Says(500, "mStock could not send the order book: no reason was given."),
        ["orderbook_http_error"] = Says(500, "mStock could not send the order book: down"),
        ["tradebook_failed"] = Says(500, "mStock could not send the trade book: Session expired"),
        ["positionbook_failed"] = Says(500, "mStock could not send the positions: Session expired"),
        ["holdings_failed"] = Says(500, "mStock could not send the holdings: Session expired"),
        ["funds_failed"] = Says(500, "mStock could not send the funds: Session expired"),
        ["funds_http_error"] = Says(500, "mStock could not send the funds: no reason was given."),
        ["funds_without_data"] = Says(500, "mStock could not send the funds: no reason was given."),
        ["closeposition_book_failed"] = new("OpenAlgo answers that every position was squared off when the positions cannot be read. EP Trade bot says nothing was closed and to try again.", f => Expect(f, 500, Error("Your positions could not be read from mstock, so nothing was closed. Try again in a moment."))),
        ["placesmartorder_book_failed_is_refused"] = new("OpenAlgo answers a position it could not read with a 500; EP Trade bot answers 503 and says to try again.",
            f => Expect(f, 503, Error("Your open position in SBIN could not be read from mstock, so no order was placed. Try again in a moment."))),
        ["placesmartorder_already_matched"] = new("OpenAlgo answers an already-matched smart order with a 500 whose message reads as a failure. EP Trade bot answers success and says the position already matches.", f => Expect(f, 200, new JsonObject { ["status"] = "success", ["message"] = "Positions Already Matched. No Action needed." })),
    };

    public static IEnumerable<object[]> Names() => ContractRunner.Names("mstock.fixtures.json");

    [PostgresTheory]
    [MemberData(nameof(Names))]
    public Task Matches_the_python_implementation(string name) => RunAsync(host, name, Deviations, output);
}

public sealed class MstockDataHost() : MstockFileHost("mstock_data.fixtures.json");

/// <summary>mStock quotes and several quotes (tools/contract-fixtures/mstock_data.fixtures.json).</summary>
public sealed class MstockDataContractTests(MstockDataHost host, ITestOutputHelper output) : IClassFixture<MstockDataHost>
{
    private const string Words = "OpenAlgo puts the status mStock answered or an internal error in the sentence. EP Trade bot says what mStock did and what to do.";

    private static Deviation Says(int status, string message) => new(Words, f => Expect(f, status, Error(message)));

    /// <summary>OpenAlgo puts a Python exception or the status mStock answered in each item's error; EP Trade bot says what mStock did.</summary>
    private static Deviation Items(string text) => new(Words, f =>
    {
        foreach (var item in f["expected"]!["body"]!["results"]!.AsArray().OfType<JsonObject>().Where(i => i["error"] is not null)) item["error"] = text;
    });

    public static readonly Dictionary<string, Deviation> Deviations = new()
    {
        ["multiquotes_status_false"] = Items("mStock could not give the quotes: Session expired"),
        ["multiquotes_http_error"] = Items("mStock could not give the quotes: no reason was given."),
        ["multiquotes_not_json"] = Items("mStock could not give the quotes: no reason was given."),
        ["multiquotes_with_a_price_that_is_not_a_number"] = Items("mStock sent a quote with a figure that is not a number."),
        ["quote_with_no_rows"] = Says(500, "mStock sent no quote for SBIN on NSE. Try again in a moment."),
        ["quote_without_data"] = Says(500, "mStock sent no quote for SBIN on NSE. Try again in a moment."),
        ["quote_status_false"] = Says(500, "mStock could not give the quote: Invalid token"),
        ["quote_status_false_without_a_message"] = Says(500, "mStock could not give the quote: no reason was given."),
        ["quote_http_error"] = Says(500, "mStock could not give the quote: down"),
        ["quote_not_json"] = Says(500, "mStock could not give the quote: no reason was given."),
        ["quote_with_a_price_that_is_not_a_number"] = Says(500, "mStock sent a quote with a figure that is not a number."),
        ["quote_with_a_null_price"] = Says(500, "mStock sent a quote with a figure that is not a number."),
    };

    public static IEnumerable<object[]> Names() => ContractRunner.Names("mstock_data.fixtures.json");

    [PostgresTheory]
    [MemberData(nameof(Names))]
    public Task Matches_the_python_implementation(string name) => RunAsync(host, name, Deviations, output);
}
