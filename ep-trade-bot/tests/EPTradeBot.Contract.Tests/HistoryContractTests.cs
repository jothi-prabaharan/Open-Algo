using EPTradeBot.IntegrationTests;
using Xunit.Abstractions;

namespace EPTradeBot.Contract.Tests;

/// <summary>History and intervals on Zerodha (tools/contract-fixtures/history.fixtures.json).</summary>
public sealed class HistoryHost : BrokerContractHost
{
    public override string BrokerName => "zerodha";
    public override string FixtureFile => "history.fixtures.json";
    public override IReadOnlyDictionary<(string, string), decimal> Prices { get; } = new Dictionary<(string, string), decimal>();
    public override IReadOnlyDictionary<string, string> HarnessReplies { get; } = new Dictionary<string, string>();
    public override IReadOnlyDictionary<string, string> Settings { get; } = new Dictionary<string, string> { ["OpenAlgoApi:HistorySpacingMs"] = "0" };
}

public sealed class HistoryContractTests(HistoryHost host, ITestOutputHelper output) : IClassFixture<HistoryHost>
{
    public static readonly Dictionary<string, Deviation> Deviations = new()
    {
        ["history_unsupported_interval"] = new(
            "OpenAlgo answers 500 with \"Error fetching historical data: Unsupported timeframe: 2m\". EP Trade bot answers 400 and names what Zerodha offers.",
            Status: 400),
        ["history_refused"] = new(
            "Same status; the reason is worded as EP Trade bot's Zerodha plugin words every refusal.",
            f => ContractRunner.Expect(f, 500, ContractRunner.Error("Zerodha refused the request: Incorrect `api_key` or `access_token`."))),
    };

    public static IEnumerable<object[]> Names() => ContractRunner.Names("history.fixtures.json");

    [PostgresTheory]
    [MemberData(nameof(Names))]
    public Task Matches_the_python_implementation(string name) => ContractRunner.RunAsync(host, name, Deviations, output);
}
