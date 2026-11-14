using EPTradeBot.IntegrationTests;
using Xunit.Abstractions;
using static EPTradeBot.Contract.Tests.ContractRunner;

namespace EPTradeBot.Contract.Tests;

public sealed class SahiHost : BrokerContractHost
{
    public override string BrokerName => "sahi";
    public override string FixtureFile => "sahi.fixtures.json";
    public override bool NumbersMayBeText => true;
    public override IReadOnlyDictionary<string, string> Keys { get; } = new Dictionary<string, string> { ["ApiKey"] = "sahi-api-key", ["ApiSecret"] = "sahi-api-secret" };
    public override IReadOnlyDictionary<(string, string), decimal> Prices { get; } = new Dictionary<(string, string), decimal>
    {
        [("NSE", "SBIN")] = 805m, [("NSE", "INFY")] = 1500m, [("NFO", "NIFTY25NOV26FUT")] = 24000m,
    };
    public override IReadOnlyDictionary<string, string> HarnessReplies { get; } = new Dictionary<string, string>
    {
        ["GET /v1/positions"] = """{"positions":[]}""",
    };
}

/// <summary>Sahi against the Python implementation (tools/contract-fixtures/sahi.fixtures.json).</summary>
public sealed class SahiContractTests(SahiHost host, ITestOutputHelper output) : IClassFixture<SahiHost>
{
    private const string Brand = "Same status; the words name EP Trade bot where OpenAlgo's name its own product.";

    private static Deviation Words(int status, string message) => new(Brand, f => Expect(f, status, Error(message)));

    public static readonly Dictionary<string, Deviation> Deviations = new()
    {
        ["placeorder_session_expired"] = Words(401, Expired),
        ["modifyorder_session_expired"] = Words(401, Expired),
        ["orderbook_session_expired"] = Words(500, Expired),
        ["positionbook_refused"] = Words(500, Expired),
        ["placeorder_ip_not_allowed"] = Words(403, "Sahi refused the order because it did not come from your registered static IP. Check the IP in Sahi web, Profile, API Dashboard, and that EP Trade bot runs on that machine."),
        ["placeorder_forbidden"] = Words(403, "Your Sahi API key does not have permission for this. Create a key with Full access in the Sahi API Dashboard and use it in EP Trade bot."),
        ["placeorder_unknown_symbol"] = new("Same status; the platform's own words for a symbol missing from the master contract, where OpenAlgo's plugin words it itself.",
            f => Expect(f, 400, Error("NOPE on NSE is not in the Sahi master contract. Check the symbol, or download the master contract again."))),
        ["closeposition_mtf_position_stays_open"] = new("OpenAlgo refuses to square off a margin-trading position and says to do it in the Sahi app. EP Trade bot's close-all sends a plain product error from the shared order path, so it reads the same way for every broker; the position still stays open and the run reports failure.",
            f => Expect(f, 500, Error("1 position(s) could not be squared off and are still open: SBIN (NSE): Product MTF is not supported. Use CNC, MIS or NRML."))),
        ["funds_refused"] = new("OpenAlgo shows an empty balance when Sahi refuses the funds call. EP Trade bot reports the refusal, so an empty balance is never mistaken for a real one.", Status: 500),
        ["funds_without_the_available_block"] = new("OpenAlgo shows an empty balance when Sahi answers the funds call without an available block. EP Trade bot reports it, so an empty balance is never mistaken for a real one.", Status: 500),
    };

    private const string Expired = "Your Sahi session has expired. Log in to EP Trade bot again.";

    public static IEnumerable<object[]> Names() => ContractRunner.Names("sahi.fixtures.json");

    [PostgresTheory]
    [MemberData(nameof(Names))]
    public Task Matches_the_python_implementation(string name) => RunAsync(host, name, Deviations, output);
}
