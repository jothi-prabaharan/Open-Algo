using System.Text.Json.Nodes;
using EPTradeBot.Infrastructure.Brokers.Delta;
using EPTradeBot.IntegrationTests;
using Xunit.Abstractions;
using static EPTradeBot.Contract.Tests.ContractRunner;

namespace EPTradeBot.Contract.Tests;

public sealed class FyersHost : BrokerContractHost
{
    public override string BrokerName => "fyers";
    public override string FixtureFile => "fyers.fixtures.json";

    /// <summary>The Fyers app id, sent with the token as "app_id:token" (BROKER_API_KEY in the recording).</summary>
    public override IReadOnlyDictionary<string, string> Keys { get; } = new Dictionary<string, string> { ["ApiKey"] = "FYAPPID-100" };

    public override IReadOnlyDictionary<(string, string), decimal> Prices { get; } = new Dictionary<(string, string), decimal>
    {
        [("NSE", "SBIN")] = 805m, [("NSE", "INFY")] = 1500m, [("NFO", "NIFTY25NOV26FUT")] = 24000m,
    };

    public override IReadOnlyDictionary<string, string> HarnessReplies { get; } = new Dictionary<string, string>
    {
        ["GET /api/v3/positions"] = """{"s":"ok","code":200,"message":"","netPositions":[],"overall":{}}""",
    };
}

public sealed class DeltaHost : BrokerContractHost
{
    private const string Secret = "delta-api-secret";

    public override string BrokerName => "deltaexchange";
    public override string FixtureFile => "deltaexchange.fixtures.json";
    public override bool NumbersMayBeText => true;
    public override IReadOnlyDictionary<string, string> Keys { get; } = new Dictionary<string, string> { ["ApiSecret"] = Secret };

    public override IReadOnlyDictionary<(string, string), decimal> Prices { get; } = new Dictionary<(string, string), decimal>
    {
        [("CRYPTO", "BTCUSD")] = 67000m, [("CRYPTO", "ETHUSD")] = 3450m, [("CRYPTO", "SOL_INR")] = 15000m,
    };

    public override IReadOnlyDictionary<string, string> HarnessReplies { get; } = new Dictionary<string, string>
    {
        ["GET /v2/positions/margined"] = """{"success":true,"result":[]}""",
        ["GET /v2/wallet/balances"] = """{"success":true,"result":[]}""",
    };

    /// <summary>
    /// Every private request carries a valid signature: HMAC-SHA256 of method, timestamp, path,
    /// query and body under the secret, recomputed here from what was actually sent, with the
    /// timestamp of the (fixed) clock. Public market data is sent unsigned.
    /// </summary>
    public override void Verify(SentRequest request)
    {
        if (!request.Headers.TryGetValue("signature", out var signature))
        {
            Assert.True(request.Path.StartsWith("/v2/tickers/", StringComparison.Ordinal) || request.Path.StartsWith("/v2/l2orderbook/", StringComparison.Ordinal),
                $"{request.Method} {request.Path} was sent unsigned.");
            return;
        }

        var timestamp = request.Headers["timestamp"];
        Assert.Equal(RecordedAt!.Value.ToUnixTimeSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture), timestamp);
        Assert.Equal(DeltaExchangePlugin.Sign(Secret, request.Method, timestamp, request.Path, request.RawQuery, request.RawBody), signature);
        Assert.Equal("delta-api-key", request.Headers["api-key"]);
    }
}

/// <summary>Fyers against the Python implementation (tools/contract-fixtures/fyers.fixtures.json).</summary>
public sealed class FyersContractTests(FyersHost host, ITestOutputHelper output) : IClassFixture<FyersHost>
{
    private const string Expired = "Fyers says the session has expired. Log in to Fyers again.";

    private static JsonObject Exit(string symbol, int qty, int side, string product) => new()
    {
        ["method"] = "POST", ["host"] = "api-t1.fyers.in", ["path"] = "/api/v3/orders/sync", ["query"] = new JsonArray(),
        ["headers"] = new JsonObject { ["authorization"] = "FYAPPID-100:fyers-access-token", ["content-type"] = "application/json" },
        ["json"] = new JsonObject
        {
            ["symbol"] = symbol, ["qty"] = qty, ["type"] = 2, ["side"] = side, ["productType"] = product, ["limitPrice"] = 0.0, ["stopPrice"] = 0.0,
            ["validity"] = "DAY", ["disclosedQty"] = 0, ["offlineOrder"] = false, ["stopLoss"] = 0, ["takeProfit"] = 0, ["orderTag"] = "openalgo",
        },
    };

    public static readonly Dictionary<string, Deviation> Deviations = new()
    {
        ["placeorder_unknown_symbol"] = new("OpenAlgo sends symbol null to Fyers for a symbol missing from the master contract. EP Trade bot refuses before sending anything.", Status: 400),
        ["placeorder_refused_with_200"] = new("When Fyers refuses an order with HTTP 200, OpenAlgo reports success with order id null, so the trader believes it was placed. EP Trade bot reports the refusal.",
            f => Expect(f, 500, Error("Order rejected by RMS"))),
        ["closeposition"] = new("OpenAlgo closes everything with one Fyers call (DELETE /positions, exit_all). EP Trade bot squares off each position with its own order through the dispatcher, so every exit passes the environment lock and is audited.",
            f =>
            {
                f["sent"] = new JsonArray(Exit("NSE:SBIN-EQ", 10, -1, "INTRADAY"), Exit("NSE:NIFTY26NOVFUT", 75, 1, "MARGIN"));
            }),
        ["funds_token_expired"] = new("OpenAlgo answers success with no funds when Fyers refuses the session. EP Trade bot says it has expired.", f => Expect(f, 403, Error(Expired))),
        ["quotes_refused"] = new("Same refusal; OpenAlgo's message carries the request URL, EP Trade bot names the cause and the next step.", f => Expect(f, 403, Error(Expired))),
        ["multiquotes_refused"] = new("OpenAlgo answers a refused /data/quotes call with success and an empty list, so a trader sees no quotes and no reason. EP Trade bot reports the refusal.",
            f => Expect(f, 500, Error("Fyers could not send quotes: Invalid symbol"))),
        ["multiquotes_missing_quote"] = new("OpenAlgo leaves a symbol Fyers did not quote out of the answer. EP Trade bot keeps it, with the reason, as OpenAlgo does for the other brokers.",
            f => f["expected"]!["body"]!["results"]!.AsArray().Add(new JsonObject { ["symbol"] = "INFY", ["exchange"] = "NSE", ["error"] = "No quote data available" })),
    };

    public static IEnumerable<object[]> Names() => ContractRunner.Names("fyers.fixtures.json");

    [PostgresTheory]
    [MemberData(nameof(Names))]
    public Task Matches_the_python_implementation(string name) => RunAsync(host, name, Deviations, output);
}

/// <summary>Delta Exchange against the Python implementation (tools/contract-fixtures/deltaexchange.fixtures.json).</summary>
public sealed class DeltaContractTests(DeltaHost host, ITestOutputHelper output) : IClassFixture<DeltaHost>
{
    private static JsonObject Cancel(long id, long productId) => new()
    {
        ["method"] = "DELETE", ["host"] = "api.india.delta.exchange", ["path"] = "/v2/orders", ["query"] = new JsonArray(),
        ["headers"] = new JsonObject { ["api-key"] = "delta-api-key", ["content-type"] = "application/json", ["accept"] = "application/json" },
        ["json"] = new JsonObject { ["id"] = id, ["product_id"] = productId },
    };

    public static readonly Dictionary<string, Deviation> Deviations = new()
    {
        ["placeorder_unknown_symbol"] = new("Same refusal and status; EP Trade bot words it for a trader.",
            f => f["expected"]!["body"]!["message"] = "NOPEUSD on CRYPTO is not in the Delta Exchange master contract. Check the symbol, or download the master contract again."),
        ["cancelallorder"] = new("OpenAlgo cancels with Delta's bulk call and reports the result as one order, \"all\". EP Trade bot cancels each open order and names each one cancelled or refused.",
            f =>
            {
                var sent = f["sent"]!.AsArray();
                sent.Clear();
                sent.Add(Cancel(9001, 27));
                sent.Add(Cancel(9003, 3136));
                f["expected"]!["body"]!["canceled_orders"] = new JsonArray("27:9001", "3136:9003");
                f["expected"]!["body"]!["message"] = "Canceled 2 orders. Failed to cancel 0 orders.";
            }),
        ["closeposition_with_spot"] = new("Orders carry whole quantities in this build, so the 0.5 SOL spot holding cannot be sold here. OpenAlgo sells it; EP Trade bot squares off the rest and names it as still open, instead of rounding it to 0 and calling everything closed.",
            f =>
            {
                var sent = f["sent"]!.AsArray();
                sent.RemoveAt(sent.Count - 1);
                Expect(f, 500, Error("1 position(s) could not be squared off and are still open: SOL_INR (CRYPTO): its quantity 0.5 is fractional, which EP Trade bot cannot place yet. Close it on deltaexchange directly"));
            }),
        ["funds_key_refused"] = new("OpenAlgo shows zero funds when Delta refuses the API key. EP Trade bot says the key was refused.",
            f => Expect(f, 403, Error("Delta Exchange refused the API key. Check the key and secret, or create new ones on Delta Exchange."))),
        ["depth_book_unavailable"] = new("OpenAlgo shows an empty order book when Delta's book cannot be read. EP Trade bot says it is unavailable.",
            f => Expect(f, 500, Error("Delta Exchange could not send the order book right now. Try again in a moment."))),
        ["quotes_refused"] = new("Same status; EP Trade bot words it for a trader instead of quoting the raw reply.",
            f => f["expected"]!["body"]!["message"] = "Delta Exchange has no ticker for this contract."),
        ["multiquotes"] = new("Same answer; the missing ticker is worded for a trader instead of quoting the raw reply.",
            f => f["expected"]!["body"]!["results"]![2]!["error"] = "Delta Exchange has no ticker for this contract."),
    };

    public static IEnumerable<object[]> Names() => ContractRunner.Names("deltaexchange.fixtures.json");

    [PostgresTheory]
    [MemberData(nameof(Names))]
    public Task Matches_the_python_implementation(string name) => RunAsync(host, name, Deviations, output);
}
