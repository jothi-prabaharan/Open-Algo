using EPTradeBot.IntegrationTests;
using Xunit.Abstractions;

namespace EPTradeBot.Contract.Tests;

/// <summary>
/// Signing in to a broker, recorded from each plugin's authenticate_broker (tools/contract-fixtures/login_*.fixtures.json). The token exchange the
/// broker sees and the tokens stored must match OpenAlgo's; the words of a refusal are EP Trade bot's own, written for a trader.
/// </summary>
public sealed class LoginZerodhaHost : BrokerContractHost
{
    public override string BrokerName => "zerodha";
    public override string FixtureFile => "login_zerodha.fixtures.json";
    public override IReadOnlyDictionary<string, string> Keys { get; } = new Dictionary<string, string> { ["ApiKey"] = "kite-api-key", ["ApiSecret"] = "kite-api-secret" };
    public override IReadOnlyDictionary<(string, string), decimal> Prices { get; } = new Dictionary<(string, string), decimal>();
    public override IReadOnlyDictionary<string, string> HarnessReplies { get; } = new Dictionary<string, string>();
}

public sealed class LoginAngelHost : BrokerContractHost
{
    public override string BrokerName => "angel";
    public override string FixtureFile => "login_angel.fixtures.json";
    public override bool NumbersMayBeText => true;
    public override IReadOnlyDictionary<string, string> Keys { get; } = new Dictionary<string, string> { ["ApiKey"] = "angel-private-key" };
    public override IReadOnlyDictionary<(string, string), decimal> Prices { get; } = new Dictionary<(string, string), decimal>();
    public override IReadOnlyDictionary<string, string> HarnessReplies { get; } = new Dictionary<string, string>();
}

public sealed class LoginPaytmHost : BrokerContractHost
{
    public override string BrokerName => "paytm";
    public override string FixtureFile => "login_paytm.fixtures.json";
    public override IReadOnlyDictionary<string, string> Keys { get; } = new Dictionary<string, string> { ["ApiKey"] = "paytm-api-key", ["ApiSecret"] = "paytm-api-secret" };
    public override IReadOnlyDictionary<(string, string), decimal> Prices { get; } = new Dictionary<(string, string), decimal>();
    public override IReadOnlyDictionary<string, string> HarnessReplies { get; } = new Dictionary<string, string>();
}

internal static class LoginDeviations
{
    public const string Words = "OpenAlgo shows the broker's raw error text, prefixed with \"API error\" or not at all. EP Trade bot says in a sentence which broker refused and what to do next.";

    public static Deviation Refused(string message) => new(Words, f => ContractRunner.Expect(f, 400, ContractRunner.Error(message)));
}

public sealed class LoginZerodhaContractTests(LoginZerodhaHost host, ITestOutputHelper output) : IClassFixture<LoginZerodhaHost>
{
    public static readonly Dictionary<string, Deviation> Deviations = new()
    {
        ["login_kite_refuses_the_token"] = LoginDeviations.Refused("Zerodha did not accept the sign-in: Token is invalid or has expired."),
        ["login_kite_refuses_without_a_message"] = LoginDeviations.Refused("Zerodha did not accept the sign-in. Start it again from the beginning."),
        ["login_no_access_token_in_the_answer"] = new(LoginDeviations.Words, f => ContractRunner.Expect(f, 502, ContractRunner.Error("Zerodha accepted the sign-in but sent no access token. Start the sign-in again."))),
    };

    public static IEnumerable<object[]> Names() => ContractRunner.Names("login_zerodha.fixtures.json");

    [PostgresTheory]
    [MemberData(nameof(Names))]
    public Task Matches_the_python_implementation(string name) => ContractRunner.RunAsync(host, name, Deviations, output);
}

public sealed class LoginAngelContractTests(LoginAngelHost host, ITestOutputHelper output) : IClassFixture<LoginAngelHost>
{
    public static readonly Dictionary<string, Deviation> Deviations = new()
    {
        ["login_wrong_one_time_code"] = LoginDeviations.Refused("Angel One did not accept the sign-in: Invalid totp"),
        ["login_wrong_pin"] = LoginDeviations.Refused("Angel One did not accept the sign-in: Invalid Password"),
        ["login_refused_without_a_message"] = LoginDeviations.Refused("Angel One did not accept the sign-in. Check the client code, PIN and one-time code, then try again."),
    };

    public static IEnumerable<object[]> Names() => ContractRunner.Names("login_angel.fixtures.json");

    [PostgresTheory]
    [MemberData(nameof(Names))]
    public Task Matches_the_python_implementation(string name) => ContractRunner.RunAsync(host, name, Deviations, output);
}

public sealed class LoginPaytmContractTests(LoginPaytmHost host, ITestOutputHelper output) : IClassFixture<LoginPaytmHost>
{
    public static readonly Dictionary<string, Deviation> Deviations = new()
    {
        ["login_refused_with_reasons"] = LoginDeviations.Refused("Paytm Money did not accept the sign-in: Request token is invalid.; Request token has expired."),
        ["login_refused_without_reasons"] = LoginDeviations.Refused("Paytm Money did not accept the sign-in. Start it again from the beginning."),
        ["login_no_access_token_in_the_answer"] = new(LoginDeviations.Words, f => ContractRunner.Expect(f, 502, ContractRunner.Error("Paytm Money accepted the sign-in but sent no access token. Start the sign-in again."))),
    };

    public static IEnumerable<object[]> Names() => ContractRunner.Names("login_paytm.fixtures.json");

    [PostgresTheory]
    [MemberData(nameof(Names))]
    public Task Matches_the_python_implementation(string name) => ContractRunner.RunAsync(host, name, Deviations, output);
}
