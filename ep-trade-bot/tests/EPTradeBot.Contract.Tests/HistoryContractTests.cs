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

/// <summary>History and intervals on Angel One (tools/contract-fixtures/history_angel.fixtures.json).</summary>
public sealed class HistoryAngelHost : BrokerContractHost
{
    public override string BrokerName => "angel";
    public override string FixtureFile => "history_angel.fixtures.json";
    public override bool NumbersMayBeText => true;
    public override IReadOnlyDictionary<string, string> Keys { get; } = new Dictionary<string, string> { ["ApiKey"] = "angel-private-key" };
    public override IReadOnlyDictionary<(string, string), decimal> Prices { get; } = new Dictionary<(string, string), decimal>();
    public override IReadOnlyDictionary<string, string> HarnessReplies { get; } = new Dictionary<string, string>();
    public override IReadOnlyDictionary<string, string> Settings { get; } = new Dictionary<string, string>
    {
        ["OpenAlgoApi:HistorySpacingMs"] = "0", ["Brokers:HistoryGapMs:angel"] = "0",
    };
}

public sealed class HistoryAngelContractTests(HistoryAngelHost host, ITestOutputHelper output) : IClassFixture<HistoryAngelHost>
{
    private const string Gap = "OpenAlgo logs a chunk Angel One would not answer, carries on and answers success with the candles it has (none at all, here). EP Trade bot stops and says so, because a history with a missing stretch looks like a quiet market.";

    public static readonly Dictionary<string, Deviation> Deviations = new()
    {
        ["history_unsupported_interval"] = new(
            "OpenAlgo answers 500 with \"Error fetching historical data: Timeframe '2m' is not supported by Angel\". EP Trade bot answers 400 and names what Angel One offers.",
            Status: 400),
        ["history_session_expired"] = new(Gap, f => ContractRunner.Expect(f, 403, ContractRunner.Error("Angel One says the session has expired. Log in to Angel One again."))),
        ["history_chunk_refused"] = new(Gap, f => ContractRunner.Expect(f, 500,
            ContractRunner.Error("Angel One could not send the candles for 2026-11-02 to 2026-11-02: Something went wrong, please try again after sometime."))),
    };

    public static IEnumerable<object[]> Names() => ContractRunner.Names("history_angel.fixtures.json");

    [PostgresTheory]
    [MemberData(nameof(Names))]
    public Task Matches_the_python_implementation(string name) => ContractRunner.RunAsync(host, name, Deviations, output);
}
