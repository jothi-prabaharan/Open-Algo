using System.Text.Json.Nodes;
using EPTradeBot.IntegrationTests;
using Xunit.Abstractions;

namespace EPTradeBot.Contract.Tests;

/// <summary>/margin on Angel One, Upstox and Fyers (tools/contract-fixtures/margin_*.fixtures.json).</summary>
internal static class MarginDeviations
{
    private const string Refusal = "Same status; the reason is worded for the trader.";

    public static Dictionary<string, Deviation> For(string display, string errorStatus400Text, string okStatusText, int expiredStatus, string expiredText)
    {
        string Unknown(string symbol) => $"{symbol} on NSE is not in the {display} master contract, so the margin cannot be worked out. Check the symbol, or download the master contract again.";
        return new()
        {
            ["margin_broker_refuses_with_an_error_status"] = new(Refusal, f => ContractRunner.Expect(f, 400, ContractRunner.Error(errorStatus400Text))),
            ["margin_broker_refuses_with_ok_status"] = new(
                $"{display} refuses with a 200 and an error in the body, and OpenAlgo passes that on as a success status with an error message. EP Trade bot answers an error status.",
                f => ContractRunner.Expect(f, 400, ContractRunner.Error(okStatusText))),
            ["margin_token_expired"] = new(Refusal, f => ContractRunner.Expect(f, expiredStatus, ContractRunner.Error(expiredText))),
            ["margin_unknown_symbol_alone"] = new("Same status; EP Trade bot names the symbol, where OpenAlgo says no symbol was valid.", f => ContractRunner.Expect(f, 400, ContractRunner.Error(Unknown("NOSUCH")))),
            ["margin_unknown_symbol_among_others"] = new(
                "OpenAlgo leaves the position whose symbol it cannot find out of the basket and calculates the rest, which understates the margin. EP Trade bot refuses and names the symbol.",
                f =>
                {
                    ContractRunner.Expect(f, 400, ContractRunner.Error(Unknown("NOSUCH")));
                    f["sent"]!.AsArray().Clear();
                }),
        };
    }
}

public sealed class MarginAngelHost : BrokerContractHost
{
    public override string BrokerName => "angel";
    public override string FixtureFile => "margin_angel.fixtures.json";
    public override bool NumbersMayBeText => true;
    public override IReadOnlyDictionary<string, string> Keys { get; } = new Dictionary<string, string> { ["ApiKey"] = "angel-private-key" };
    public override IReadOnlyDictionary<(string, string), decimal> Prices { get; } = new Dictionary<(string, string), decimal>();
    public override IReadOnlyDictionary<string, string> HarnessReplies { get; } = new Dictionary<string, string>();
}

public sealed class MarginAngelContractTests(MarginAngelHost host, ITestOutputHelper output) : IClassFixture<MarginAngelHost>
{
    public static readonly Dictionary<string, Deviation> Deviations = MarginDeviations.For("Angel One",
        "Angel One could not work out the margin: Invalid token", "Angel One could not work out the margin: Margin cannot be calculated for this instrument",
        403, "Angel One could not work out the margin: Invalid Token");

    public static IEnumerable<object[]> Names() => ContractRunner.Names("margin_angel.fixtures.json");

    [PostgresTheory]
    [MemberData(nameof(Names))]
    public Task Matches_the_python_implementation(string name) => ContractRunner.RunAsync(host, name, Deviations, output);
}

public sealed class MarginUpstoxHost : BrokerContractHost
{
    public override string BrokerName => "upstox";
    public override string FixtureFile => "margin_upstox.fixtures.json";
    public override bool NumbersMayBeText => true;
    public override IReadOnlyDictionary<(string, string), decimal> Prices { get; } = new Dictionary<(string, string), decimal>();
    public override IReadOnlyDictionary<string, string> HarnessReplies { get; } = new Dictionary<string, string>();
}

public sealed class MarginUpstoxContractTests(MarginUpstoxHost host, ITestOutputHelper output) : IClassFixture<MarginUpstoxHost>
{
    public static readonly Dictionary<string, Deviation> Deviations = MarginDeviations.For("Upstox",
        "Upstox could not work out the margin: UDAPI1021: Invalid Instrument key", "Upstox could not work out the margin: UDAPI1999: Margin cannot be calculated for this instrument",
        403, "Upstox says the access token is invalid or has expired. Log in to Upstox again.");

    public static IEnumerable<object[]> Names() => ContractRunner.Names("margin_upstox.fixtures.json");

    [PostgresTheory]
    [MemberData(nameof(Names))]
    public Task Matches_the_python_implementation(string name) => ContractRunner.RunAsync(host, name, Deviations, output);
}

public sealed class MarginFyersHost : BrokerContractHost
{
    public override string BrokerName => "fyers";
    public override string FixtureFile => "margin_fyers.fixtures.json";
    public override IReadOnlyDictionary<string, string> Keys { get; } = new Dictionary<string, string> { ["ApiKey"] = "FYAPPID-100" };
    public override IReadOnlyDictionary<(string, string), decimal> Prices { get; } = new Dictionary<(string, string), decimal>();
    public override IReadOnlyDictionary<string, string> HarnessReplies { get; } = new Dictionary<string, string>();
}

public sealed class MarginFyersContractTests(MarginFyersHost host, ITestOutputHelper output) : IClassFixture<MarginFyersHost>
{
    public static readonly Dictionary<string, Deviation> Deviations = MarginDeviations.For("Fyers",
        "Fyers could not work out the margin (code -50): Invalid input", "Fyers could not work out the margin (code -99): Margin cannot be calculated for this instrument",
        403, "Fyers says the session has expired. Log in to Fyers again.");

    public static IEnumerable<object[]> Names() => ContractRunner.Names("margin_fyers.fixtures.json");

    [PostgresTheory]
    [MemberData(nameof(Names))]
    public Task Matches_the_python_implementation(string name) => ContractRunner.RunAsync(host, name, Deviations, output);
}

public sealed class MarginDhanHost : DhanFamilyHost
{
    public override string BrokerName => "dhan";
    public override string FixtureFile => "margin_dhan.fixtures.json";
}

public sealed class MarginDhanContractTests(MarginDhanHost host, ITestOutputHelper output) : IClassFixture<MarginDhanHost>
{
    public static readonly Dictionary<string, Deviation> Deviations = Build();

    private static Dictionary<string, Deviation> Build()
    {
        var d = MarginDeviations.For("Dhan", "Dhan could not work out the margin: Missing required fields, bad values for parameters etc.",
            "Dhan could not work out the margin: Failed to calculate margin", 403, "Dhan says the access token is invalid or has expired. Log in to Dhan again.");
        d["margin_multi_broker_refuses"] = new("Same status; the reason is worded for the trader.",
            f => ContractRunner.Expect(f, 400, ContractRunner.Error("Dhan could not work out the margin: Missing required fields, bad values for parameters etc.")));
        return d;
    }

    public static IEnumerable<object[]> Names() => ContractRunner.Names("margin_dhan.fixtures.json");

    [PostgresTheory]
    [MemberData(nameof(Names))]
    public Task Matches_the_python_implementation(string name) => ContractRunner.RunAsync(host, name, Deviations, output);
}

public sealed class MarginDeltaHost : BrokerContractHost
{
    public override string BrokerName => "deltaexchange";
    public override string FixtureFile => "margin_delta.fixtures.json";
    public override bool NumbersMayBeText => true;
    public override IReadOnlyDictionary<string, string> Keys { get; } = new Dictionary<string, string> { ["ApiSecret"] = "delta-api-secret" };
    public override IReadOnlyDictionary<(string, string), decimal> Prices { get; } = new Dictionary<(string, string), decimal>();
    public override IReadOnlyDictionary<string, string> HarnessReplies { get; } = new Dictionary<string, string>();
    public override IReadOnlySet<string> IgnoredReads { get; } = new HashSet<string> { "/v2/users/trading_preferences" };
}

public sealed class MarginDeltaContractTests(MarginDeltaHost host, ITestOutputHelper output) : IClassFixture<MarginDeltaHost>
{
    private const string Understated = "OpenAlgo adds up the positions Delta Exchange answered and leaves the others out, which understates the margin. EP Trade bot gives no total and names the symbol.";

    private static string Unknown(string symbol) => $"{symbol} on CRYPTO is not in the Delta Exchange master contract, so the margin cannot be worked out. Check the symbol, or download the master contract again.";

    public static readonly Dictionary<string, Deviation> Deviations = new()
    {
        ["margin_one_position_refused"] = new(Understated, f => ContractRunner.Expect(f, 500, ContractRunner.Error("Delta Exchange could not work out the margin for BTCUSD, so no total is given."))),
        ["margin_server_error"] = new(Understated, f => ContractRunner.Expect(f, 500, ContractRunner.Error("Delta Exchange could not work out the margin for BTCUSD, so no total is given."))),
        ["margin_one_of_two_refused"] = new(Understated, f => ContractRunner.Expect(f, 500, ContractRunner.Error("Delta Exchange could not work out the margin for ETHUSD, so no total is given."))),
        ["margin_unknown_symbol_alone"] = new("Same status; EP Trade bot names the symbol.", f => ContractRunner.Expect(f, 400, ContractRunner.Error(Unknown("NOSUCH")))),
        ["margin_unknown_symbol_among_others"] = new(
            "OpenAlgo leaves the position whose symbol it cannot find out of the basket and calculates the rest, which understates the margin. EP Trade bot refuses and names the symbol.",
            f =>
            {
                ContractRunner.Expect(f, 400, ContractRunner.Error(Unknown("NOSUCH")));
                f["sent"]!.AsArray().Clear();
            }),
    };

    public static IEnumerable<object[]> Names() => ContractRunner.Names("margin_delta.fixtures.json");

    [PostgresTheory]
    [MemberData(nameof(Names))]
    public Task Matches_the_python_implementation(string name) => ContractRunner.RunAsync(host, name, Deviations, output);
}
