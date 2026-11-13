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

public sealed class MarginDhanSandboxHost : DhanFamilyHost
{
    public override string BrokerName => "dhan_sandbox";
    public override string FixtureFile => "margin_dhan_sandbox.fixtures.json";
}

public sealed class MarginDhanSandboxContractTests(MarginDhanSandboxHost host, ITestOutputHelper output) : IClassFixture<MarginDhanSandboxHost>
{
    private const string ThreeFigures = "OpenAlgo gives the sandbox's own long answer (raw reply, brokerage, leverage and more). EP Trade bot gives the three figures every broker gives, with the same values.";

    private static void Trim(JsonObject f)
    {
        var data = f["expected"]!["body"]!["data"]!.AsObject();
        var kept = new JsonObject { ["total_margin_required"] = data["total_margin_required"]!.DeepClone(), ["span_margin"] = data["span_margin"]!.DeepClone(), ["exposure_margin"] = data["exposure_margin"]!.DeepClone() };
        f["expected"]!["body"] = new JsonObject { ["status"] = "success", ["data"] = kept };
    }

    private static Deviation Shorter() => new(ThreeFigures, f => Trim(f));

    private static string Unknown(string symbol) => $"{symbol} on NSE is not in the Dhan Sandbox master contract, so the margin cannot be worked out. Check the symbol, or download the master contract again.";

    public static readonly Dictionary<string, Deviation> Deviations = new()
    {
        ["margin_one_position_goes_to_the_multi_calculator_first"] = Shorter(),
        ["margin_two_positions_use_the_multi_calculator"] = Shorter(),
        ["margin_multi_answer_with_blank_figures"] = Shorter(),
        ["margin_limit_order_with_price"] = Shorter(),
        ["margin_stop_loss_with_trigger"] = Shorter(),
        ["margin_cnc_equity"] = Shorter(),
        ["margin_multi_refused_one_position_falls_back_to_the_single_calculator"] = new(ThreeFigures, f => { f["expected"]!["body"]!.AsObject().Remove("_fallback_reason"); Trim(f); }),
        ["margin_multi_refused_ok_status_falls_back"] = new(ThreeFigures, f => { f["expected"]!["body"]!.AsObject().Remove("_fallback_reason"); Trim(f); }),
        ["margin_multi_refused_two_positions_are_added_up"] = new(ThreeFigures, f => { f["expected"]!["body"]!.AsObject().Remove("_fallback_reason"); Trim(f); }),
        ["margin_both_calculators_refuse"] = new("Same status; the reason is worded for the trader.",
            f => ContractRunner.Expect(f, 400, ContractRunner.Error("Dhan Sandbox could not work out the margin: Missing required fields, bad values for parameters etc."))),
        ["margin_both_calculators_refuse_with_ok_status"] = new("Dhan Sandbox refuses with a 200 and an error in the body, and OpenAlgo passes that on as a success status with an error message. EP Trade bot answers an error status.",
            f => ContractRunner.Expect(f, 400, ContractRunner.Error("Dhan Sandbox could not work out the margin: Invalid Client ID"))),
        ["margin_token_expired"] = new("Same refusal; the status and words are EP Trade bot's own for an expired session.",
            f => ContractRunner.Expect(f, 403, ContractRunner.Error("Dhan Sandbox says the access token is invalid or has expired. Log in to Dhan Sandbox again."))),
        ["margin_unknown_symbol_alone"] = new("Same status; EP Trade bot names the symbol, where OpenAlgo says no symbol was valid.", f => ContractRunner.Expect(f, 400, ContractRunner.Error(Unknown("NOSUCH")))),
        ["margin_unknown_symbol_among_others"] = new("OpenAlgo leaves the position whose symbol it cannot find out of the basket and calculates the rest, which understates the margin. EP Trade bot refuses and names the symbol.",
            f => { ContractRunner.Expect(f, 400, ContractRunner.Error(Unknown("NOSUCH"))); f["sent"]!.AsArray().Clear(); }),
    };

    public static IEnumerable<object[]> Names() => ContractRunner.Names("margin_dhan_sandbox.fixtures.json");

    [PostgresTheory]
    [MemberData(nameof(Names))]
    public Task Matches_the_python_implementation(string name) => ContractRunner.RunAsync(host, name, Deviations, output);
}

public sealed class MarginRmoneyHost : XtsHost
{
    public override string BrokerName => "rmoney";
    public override string FixtureFile => "margin_rmoney.fixtures.json";
}

public sealed class MarginRmoneyContractTests(MarginRmoneyHost host, ITestOutputHelper output) : IClassFixture<MarginRmoneyHost>
{
    private const string Words = "Same outcome; the reason is worded for the trader.";

    public static readonly Dictionary<string, Deviation> Deviations = Build();

    private static Dictionary<string, Deviation> Build()
    {
        string Unknown(string symbol) => $"{symbol} on NSE is not in the RMoney master contract, so the margin cannot be worked out. Check the symbol, or download the master contract again.";
        return new()
        {
            ["margin_broker_refuses"] = new(Words, f => ContractRunner.Expect(f, 400, ContractRunner.Error("RMoney could not work out the margin: Invalid instrument"))),
            ["margin_error_type_with_ok_status"] = new(
                "RMoney refuses with a 200 and an error in the body, and OpenAlgo passes that on as a success status with an error message. EP Trade bot answers an error status.",
                f => ContractRunner.Expect(f, 400, ContractRunner.Error("RMoney could not work out the margin: Not allowed"))),
            ["margin_session_expired"] = new(Words, f => ContractRunner.Expect(f, 403, ContractRunner.Error("RMoney says the session has expired. Log in to RMoney again."))),
            ["margin_no_details"] = new(
                "OpenAlgo answers a success status with an error message; EP Trade bot answers an error status.",
                f => ContractRunner.Expect(f, 502, ContractRunner.Error("RMoney sent no margin details. Try again in a moment."))),
            ["margin_unknown_symbol_alone"] = new("Same status; EP Trade bot names the symbol, where OpenAlgo says no symbol was valid.", f => ContractRunner.Expect(f, 400, ContractRunner.Error(Unknown("NOSUCH")))),
            ["margin_unknown_symbol_among_others"] = new(
                "OpenAlgo leaves the position whose symbol it cannot find out of the basket and calculates the rest, which understates the margin. EP Trade bot refuses and names the symbol.",
                f =>
                {
                    ContractRunner.Expect(f, 400, ContractRunner.Error(Unknown("NOSUCH")));
                    f["sent"]!.AsArray().Clear();
                }),
            ["margin_index_exchange"] = new(
                "Same status; EP Trade bot says RMoney has no margin calculation for an index, where OpenAlgo says no symbol was valid.",
                f => ContractRunner.Expect(f, 400, ContractRunner.Error("RMoney has no margin calculation for NSE_INDEX."))),
        };
    }

    public static IEnumerable<object[]> Names() => ContractRunner.Names("margin_rmoney.fixtures.json");

    [PostgresTheory]
    [MemberData(nameof(Names))]
    public Task Matches_the_python_implementation(string name) => ContractRunner.RunAsync(host, name, Deviations, output);
}

public sealed class MarginWisdomHost : XtsHost
{
    public override string BrokerName => "wisdom";
    public override string FixtureFile => "margin_wisdom.fixtures.json";
}

/// <summary>An XTS broker with no margin calculator answers that plainly instead of failing.</summary>
public sealed class MarginWisdomContractTests(MarginWisdomHost host, ITestOutputHelper output) : IClassFixture<MarginWisdomHost>
{
    public static readonly Dictionary<string, Deviation> Deviations = new()
    {
        ["margin_has_no_calculator"] = new("Same status; the words differ (EP Trade bot names the broker and says margin is not available for it).", Status: 501),
    };

    public static IEnumerable<object[]> Names() => ContractRunner.Names("margin_wisdom.fixtures.json");

    [PostgresTheory]
    [MemberData(nameof(Names))]
    public Task Matches_the_python_implementation(string name) => ContractRunner.RunAsync(host, name, Deviations, output);
}

public sealed class MarginShoonyaHost : ShoonyaBaseHost
{
    public override string BrokerName => "shoonya";
    public override string FixtureFile => "margin_shoonya.fixtures.json";
}

public sealed class MarginShoonyaContractTests(MarginShoonyaHost host, ITestOutputHelper output) : IClassFixture<MarginShoonyaHost>
{
    private const string Status = "Shoonya answers a refusal with a success status and an error message. EP Trade bot answers an error status, with the reason worded for the trader.";

    public static readonly Dictionary<string, Deviation> Deviations = Build();

    private static Dictionary<string, Deviation> Build()
    {
        string Unknown(string symbol) => $"{symbol} on NSE is not in the Shoonya master contract, so the margin cannot be worked out. Check the symbol, or download the master contract again.";
        return new()
        {
            ["margin_broker_refuses"] = new(Status, f => ContractRunner.Expect(f, 400, ContractRunner.Error("Shoonya could not work out the margin: Invalid Input : bad basket"))),
            ["margin_refuses_with_remarks_only"] = new(Status, f => ContractRunner.Expect(f, 400, ContractRunner.Error("Shoonya could not work out the margin: Insufficient data"))),
            ["margin_refuses_with_nothing"] = new(Status, f => ContractRunner.Expect(f, 400, ContractRunner.Error("Shoonya could not work out the margin: no reason was given."))),
            ["margin_session_expired"] = new(Status, f => ContractRunner.Expect(f, 403, ContractRunner.Error("Shoonya says the session has expired. Log in to Shoonya again."))),
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

    public static IEnumerable<object[]> Names() => ContractRunner.Names("margin_shoonya.fixtures.json");

    [PostgresTheory]
    [MemberData(nameof(Names))]
    public Task Matches_the_python_implementation(string name) => ContractRunner.RunAsync(host, name, Deviations, output);
}

public sealed class MarginZebuHost : ShoonyaBaseHost
{
    public override string BrokerName => "zebu";
    public override string FixtureFile => "margin_zebu.fixtures.json";
    public override IReadOnlyDictionary<string, string> Keys { get; } = new Dictionary<string, string> { ["ApiKey"] = "FA12345:::zebu-client-id", ["ApiSecret"] = "zebu-secret" };
}

/// <summary>Zebu has no position-specific margin calculator and answers that plainly.</summary>
public sealed class MarginZebuContractTests(MarginZebuHost host, ITestOutputHelper output) : IClassFixture<MarginZebuHost>
{
    public static readonly Dictionary<string, Deviation> Deviations = new()
    {
        ["margin_has_no_calculator"] = new("Same status; the words differ (EP Trade bot names the broker and says margin is not available for it).", Status: 501),
    };

    public static IEnumerable<object[]> Names() => ContractRunner.Names("margin_zebu.fixtures.json");

    [PostgresTheory]
    [MemberData(nameof(Names))]
    public Task Matches_the_python_implementation(string name) => ContractRunner.RunAsync(host, name, Deviations, output);
}
