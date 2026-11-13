using EPTradeBot.IntegrationTests;
using Xunit.Abstractions;

namespace EPTradeBot.Contract.Tests;

/// <summary>Zerodha GTT triggers (tools/contract-fixtures/zerodha_gtt.fixtures.json): place, modify, cancel and the book against Kite's /gtt/triggers.</summary>
public sealed class ZerodhaGttHost : BrokerContractHost
{
    public override string BrokerName => "zerodha";
    public override string FixtureFile => "zerodha_gtt.fixtures.json";
    public override IReadOnlyDictionary<(string, string), decimal> Prices { get; } = new Dictionary<(string, string), decimal>();
    public override IReadOnlyDictionary<string, string> HarnessReplies { get; } = new Dictionary<string, string>();
}

public sealed class ZerodhaGttContractTests(ZerodhaGttHost host, ITestOutputHelper output) : IClassFixture<ZerodhaGttHost>
{
    private const string UnknownSymbol = "OpenAlgo cannot name a symbol missing from the master contract and answers an internal error. EP Trade bot names the symbol and says what to do.";

    private const string OkButRefused = "Kite refused the change but answered with an OK status. OpenAlgo reads the OK status and tells the caller it worked, so the trigger the caller believes changed is not. EP Trade bot reports the refusal.";

    public static readonly Dictionary<string, Deviation> Deviations = new()
    {
        ["place_unknown_symbol"] = new(UnknownSymbol, f => ContractRunner.Expect(f, 400, ContractRunner.Error("NOPE on NSE is not in the Zerodha master contract. Check the symbol, or download the master contract again."))),
        ["modify_broker_error_in_an_ok_answer"] = new(OkButRefused, f => ContractRunner.Expect(f, 500, ContractRunner.Error("Trigger could not be changed."))),
        ["cancel_broker_error_in_an_ok_answer"] = new(OkButRefused, f => ContractRunner.Expect(f, 500, ContractRunner.Error("Trigger could not be cancelled."))),
        ["modify_unknown_symbol"] = new(UnknownSymbol, f => ContractRunner.Expect(f, 400, ContractRunner.Error("NOPE on NSE is not in the Zerodha master contract. Check the symbol, or download the master contract again."))),
    };

    public static IEnumerable<object[]> Names() => ContractRunner.Names("zerodha_gtt.fixtures.json");

    [PostgresTheory]
    [MemberData(nameof(Names))]
    public Task Matches_the_python_implementation(string name) => ContractRunner.RunAsync(host, name, Deviations, output);
}

/// <summary>Angel One GTT rules (tools/contract-fixtures/angel_gtt.fixtures.json). An OCO is two rules whose ids are joined by a dash.</summary>
public sealed class AngelGttHost : BrokerContractHost
{
    public override string BrokerName => "angel";
    public override string FixtureFile => "angel_gtt.fixtures.json";
    public override bool NumbersMayBeText => true;
    public override IReadOnlyDictionary<string, string> Keys { get; } = new Dictionary<string, string> { ["ApiKey"] = "angel-private-key" };
    public override IReadOnlyDictionary<(string, string), decimal> Prices { get; } = new Dictionary<(string, string), decimal>();
    public override IReadOnlyDictionary<string, string> HarnessReplies { get; } = new Dictionary<string, string>();
}

public sealed class AngelGttContractTests(AngelGttHost host, ITestOutputHelper output) : IClassFixture<AngelGttHost>
{
    private const string UnknownSymbol = "OpenAlgo asks for the symbol's token and, finding none, answers that it could not resolve it. EP Trade bot names the symbol and says what to do.";

    public static readonly Dictionary<string, Deviation> Deviations = new()
    {
        ["place_single_unknown_symbol"] = new(UnknownSymbol, f => ContractRunner.Expect(f, 400, ContractRunner.Error("NOPE on NSE is not in the Angel One master contract. Check the symbol, or download the master contract again."))),
    };

    public static IEnumerable<object[]> Names() => ContractRunner.Names("angel_gtt.fixtures.json");

    [PostgresTheory]
    [MemberData(nameof(Names))]
    public Task Matches_the_python_implementation(string name) => ContractRunner.RunAsync(host, name, Deviations, output);
}

/// <summary>Upstox GTT (tools/contract-fixtures/upstox_gtt.fixtures.json). A trigger has no limit price; an OCO is a bracket that opens the entry first.</summary>
public sealed class UpstoxGttHost : BrokerContractHost
{
    public override string BrokerName => "upstox";
    public override string FixtureFile => "upstox_gtt.fixtures.json";
    public override bool NumbersMayBeText => true;
    public override IReadOnlyDictionary<(string, string), decimal> Prices { get; } = new Dictionary<(string, string), decimal>();
    public override IReadOnlyDictionary<string, string> HarnessReplies { get; } = new Dictionary<string, string>();
}

public sealed class UpstoxGttContractTests(UpstoxGttHost host, ITestOutputHelper output) : IClassFixture<UpstoxGttHost>
{
    private const string UnknownSymbol = "OpenAlgo asks for the instrument key and, finding none, answers that it was not found. EP Trade bot names the symbol and says what to do.";

    private const string OkButRefused = "Upstox refused the change but answered with an OK status. OpenAlgo reads the OK status and tells the caller it worked, so the trigger the caller believes changed is not. EP Trade bot reports the refusal.";

    public static readonly Dictionary<string, Deviation> Deviations = new()
    {
        ["place_unknown_symbol"] = new(UnknownSymbol, f => ContractRunner.Expect(f, 400, ContractRunner.Error("NOPE on NSE is not in the Upstox master contract. Check the symbol, or download the master contract again."))),
        ["modify_unknown_symbol"] = new(UnknownSymbol, f => ContractRunner.Expect(f, 400, ContractRunner.Error("NOPE on NSE is not in the Upstox master contract. Check the symbol, or download the master contract again."))),
        ["modify_error_status_in_an_ok_answer"] = new(OkButRefused, f => ContractRunner.Expect(f, 500, ContractRunner.Error("UDAPI2: Rejected in an ok answer"))),
    };

    public static IEnumerable<object[]> Names() => ContractRunner.Names("upstox_gtt.fixtures.json");

    [PostgresTheory]
    [MemberData(nameof(Names))]
    public Task Matches_the_python_implementation(string name) => ContractRunner.RunAsync(host, name, Deviations, output);
}

/// <summary>Dhan Forever Orders (tools/contract-fixtures/dhan_gtt.fixtures.json). A SINGLE is one leg, an OCO is two legs of one order changed one call at a time.</summary>
public sealed class DhanGttHost : DhanFamilyHost
{
    public override string BrokerName => "dhan";
    public override string FixtureFile => "dhan_gtt.fixtures.json";
    public override IReadOnlyDictionary<string, string> HarnessReplies { get; } = new Dictionary<string, string>();
}

public sealed class DhanGttContractTests(DhanGttHost host, ITestOutputHelper output) : IClassFixture<DhanGttHost>
{
    private const string UnknownSymbol = "OpenAlgo sends the Forever Order with the security id \"None\" when the symbol is missing from the master contract. EP Trade bot names the symbol and says what to do.";

    private const string OkButRefused = "Dhan answered with an OK status but no order id. OpenAlgo reads the OK status and tells the caller it worked, so the trigger the caller believes changed is not. EP Trade bot reports the refusal.";

    private const string ReasonShown = "OpenAlgo answers a refused placement with the words \"Failed to place GTT\" and drops Dhan's reason. EP Trade bot passes Dhan's reason on.";

    public static readonly Dictionary<string, Deviation> Deviations = new()
    {
        ["place_unknown_symbol"] = new(UnknownSymbol, f => NoSymbol(f)),
        ["modify_unknown_symbol"] = new(UnknownSymbol, f => NoSymbol(f)),
        ["place_broker_refuses"] = new(ReasonShown, f => ContractRunner.Expect(f, 400, ContractRunner.Error("Invalid trigger price"))),
        ["place_broker_session_expired"] = new(ReasonShown, f => ContractRunner.Expect(f, 401, ContractRunner.Error("Invalid access token"))),
        ["modify_answer_without_an_order_id"] = new(OkButRefused, f => ContractRunner.Expect(f, 500, ContractRunner.Error("Failed to modify ENTRY_LEG"))),
        ["cancel_ok_answer_without_an_order_id"] = new(OkButRefused, f => ContractRunner.Expect(f, 500, ContractRunner.Error("Failed to cancel GTT"))),
    };

    /// <summary>Nothing is sent to Dhan for a symbol the master contract lacks; OpenAlgo sent the order with the security id "None".</summary>
    private static void NoSymbol(System.Text.Json.Nodes.JsonObject f)
    {
        ContractRunner.Expect(f, 400, ContractRunner.Error("NOPE on NSE is not in the Dhan master contract. Check the symbol, or download the master contract again."));
        f["sent"]!.AsArray().Clear();
    }

    public static IEnumerable<object[]> Names() => ContractRunner.Names("dhan_gtt.fixtures.json");

    [PostgresTheory]
    [MemberData(nameof(Names))]
    public Task Matches_the_python_implementation(string name) => ContractRunner.RunAsync(host, name, Deviations, output);
}
