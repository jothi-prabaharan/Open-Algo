using EPTradeBot.IntegrationTests;
using Xunit.Abstractions;

namespace EPTradeBot.Contract.Tests;

public sealed class ZerodhaHost : BrokerContractHost
{
    public override string BrokerName => "zerodha";
    public override string FixtureFile => "zerodha.fixtures.json";

    public override IReadOnlyDictionary<(string, string), decimal> Prices { get; } = new Dictionary<(string, string), decimal>
    {
        [("NSE", "SBIN")] = 805m, [("NSE", "INFY")] = 1500m, [("NFO", "NIFTY25NOV26FUT")] = 24000m,
    };

    public override IReadOnlyDictionary<string, string> HarnessReplies { get; } = new Dictionary<string, string>
    {
        ["GET /portfolio/positions"] = """{"status":"success","data":{"net":[],"day":[]}}""",
    };

    /// <summary>A fresh tag per order, used to find an order whose reply was lost.</summary>
    public override IReadOnlyDictionary<string, string> VolatileFields { get; } = new Dictionary<string, string> { ["form.tag"] = "^openalgo[0-9a-f]{12}$" };
}

/// <summary>Zerodha against the Python implementation (tools/contract-fixtures/zerodha.fixtures.json).</summary>
public sealed class ZerodhaContractTests(ZerodhaHost host, ITestOutputHelper output) : IClassFixture<ZerodhaHost>
{
    public static readonly Dictionary<string, Deviation> Deviations = new()
    {
        ["placeorder_unknown_symbol"] = new(
            "OpenAlgo sends tradingsymbol \"None\" to Kite for a symbol missing from the master contract. EP Trade bot refuses before sending anything.",
            Status: 400),
        ["auth_header_key"] = new(
            "OpenAlgo's /funds schema requires apikey in the body, so the X-API-KEY header is refused there. EP Trade bot accepts the header on every endpoint.",
            Status: 200),
    };

    public static IEnumerable<object[]> Names() => ContractRunner.Names("zerodha.fixtures.json");

    [PostgresTheory]
    [MemberData(nameof(Names))]
    public Task Matches_the_python_implementation(string name) => ContractRunner.RunAsync(host, name, Deviations, output);
}

/// <summary>Zerodha on MCX (tools/contract-fixtures/zerodha_mcx.fixtures.json): Kite counts contracts, OpenAlgo counts units.</summary>
public sealed class ZerodhaMcxHost : BrokerContractHost
{
    public override string BrokerName => "zerodha";
    public override string FixtureFile => "zerodha_mcx.fixtures.json";
    public override IReadOnlyDictionary<(string, string), decimal> Prices { get; } = new Dictionary<(string, string), decimal>
    {
        [("MCX", "CRUDEOIL18DEC30FUT")] = 6500m, [("MCX", "GOLD05DEC30FUT")] = 75000m, [("MCX", "GOLDPETAL05DEC30FUT")] = 7500m,
        [("MCX", "SILVERM05DEC30FUT")] = 90000m, [("MCX", "MCXBULLDEX18DEC30FUT")] = 20000m,
    };
    public override IReadOnlyDictionary<string, string> HarnessReplies { get; } = new Dictionary<string, string>
    {
        ["GET /portfolio/positions"] = """{"status":"success","data":{"net":[],"day":[]}}""",
    };

    public override IReadOnlyDictionary<string, string> VolatileFields { get; } = new Dictionary<string, string> { ["form.tag"] = "^openalgo[0-9a-f]{12}$" };
}

public sealed class ZerodhaMcxContractTests(ZerodhaMcxHost host, ITestOutputHelper output) : IClassFixture<ZerodhaMcxHost>
{
    public static readonly Dictionary<string, Deviation> Deviations = new()
    {
        ["mcx_positionbook_unsizable_root"] = new(
            "OpenAlgo cannot name a contract missing from the master contract and shows the position as one contract with no symbol. MCX has revised this root's contract size, so the figure cannot be turned into units. EP Trade bot refuses the whole position book and names the contract, rather than show a quantity that may be wrong.",
            f => ContractRunner.Expect(f, 500, ContractRunner.Error("Cannot read the open position in MCXBULLDEX30JANFUT: MCX has revised its contract size and the master contract has no row for this expiry. Re-download the master contract, then retry."))),
    };

    public static IEnumerable<object[]> Names() => ContractRunner.Names("zerodha_mcx.fixtures.json");

    [PostgresTheory]
    [MemberData(nameof(Names))]
    public Task Matches_the_python_implementation(string name) => ContractRunner.RunAsync(host, name, Deviations, output);
}
