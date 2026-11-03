using EPTradeBot.IntegrationTests;
using Xunit.Abstractions;

namespace EPTradeBot.Contract.Tests;

/// <summary>Symbol master, option resolution and multi-order endpoints, recorded on Zerodha (tools/contract-fixtures/composite.fixtures.json).</summary>
public sealed class CompositeHost : BrokerContractHost
{
    public override string BrokerName => "zerodha";
    public override string FixtureFile => "composite.fixtures.json";

    public override IReadOnlyDictionary<(string, string), decimal> Prices { get; } = new Dictionary<(string, string), decimal>
    {
        [("NSE", "SBIN")] = 805m, [("NSE", "INFY")] = 1500m,
        [("NFO", "NIFTY30DEC3023900CE")] = 260m, [("NFO", "NIFTY30DEC3024000CE")] = 200m, [("NFO", "NIFTY30DEC3024200CE")] = 110m,
        [("NFO", "NIFTY30DEC3023800PE")] = 90m, [("NFO", "NIFTY30DEC3023900PE")] = 120m, [("NFO", "NIFTY30DEC3024000PE")] = 160m,
    };

    public override IReadOnlyDictionary<string, string> HarnessReplies { get; } = new Dictionary<string, string>
    {
        ["GET /portfolio/positions"] = """{"status":"success","data":{"net":[],"day":[]}}""",
    };

    public override IReadOnlyDictionary<string, string> VolatileFields { get; } = new Dictionary<string, string> { ["form.tag"] = "^openalgo[0-9a-f]{12}$" };
}

public sealed class CompositeContractTests(CompositeHost host, ITestOutputHelper output) : IClassFixture<CompositeHost>
{
    public static readonly Dictionary<string, Deviation> Deviations = new()
    {
        ["optionsymbol_quote_refused"] = new(
            "Same status; the reason is worded as EP Trade bot's Zerodha plugin words every refusal (\"Zerodha refused the request: ...\") instead of OpenAlgo's \"Error fetching quotes: API Error: ...\".",
            f => ContractRunner.Expect(f, 500, ContractRunner.Error("Failed to fetch LTP for NIFTY. Zerodha refused the request: Incorrect `api_key` or `access_token`."))),
        ["optionsorder_bad_offset"] = new(
            "OpenAlgo's /optionsorder schema takes any offset, quotes the underlying and then answers that offset MIDDLE is out of range. EP Trade bot refuses it in validation, as OpenAlgo's /optionsymbol does, before asking the broker for anything.",
            Status: 400),
    };

    public static IEnumerable<object[]> Names() => ContractRunner.Names("composite.fixtures.json");

    [PostgresTheory]
    [MemberData(nameof(Names))]
    public Task Matches_the_python_implementation(string name) => ContractRunner.RunAsync(host, name, Deviations, output);
}
