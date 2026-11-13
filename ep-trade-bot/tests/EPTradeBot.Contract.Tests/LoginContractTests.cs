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

public sealed class LoginUpstoxHost : BrokerContractHost
{
    public override string BrokerName => "upstox";
    public override string FixtureFile => "login_upstox.fixtures.json";
    public override IReadOnlyDictionary<string, string> Keys { get; } = new Dictionary<string, string>
    {
        ["ApiKey"] = "upstox-api-key", ["ApiSecret"] = "upstox-api-secret", ["RedirectUrl"] = "http://127.0.0.1:5000/upstox/callback",
    };
    public override IReadOnlyDictionary<(string, string), decimal> Prices { get; } = new Dictionary<(string, string), decimal>();
    public override IReadOnlyDictionary<string, string> HarnessReplies { get; } = new Dictionary<string, string>();
}

public sealed class LoginFyersHost : BrokerContractHost
{
    public override string BrokerName => "fyers";
    public override string FixtureFile => "login_fyers.fixtures.json";
    public override IReadOnlyDictionary<string, string> Keys { get; } = new Dictionary<string, string>
    {
        ["ApiKey"] = "FYAPP-100", ["ApiSecret"] = "fyers-secret", ["RedirectUrl"] = "http://127.0.0.1:5000/fyers/callback",
    };
    public override IReadOnlyDictionary<(string, string), decimal> Prices { get; } = new Dictionary<(string, string), decimal>();
    public override IReadOnlyDictionary<string, string> HarnessReplies { get; } = new Dictionary<string, string>();
}

public sealed class LoginDeltaHost : BrokerContractHost
{
    public override string BrokerName => "deltaexchange";
    public override string FixtureFile => "login_deltaexchange.fixtures.json";
    public override IReadOnlyDictionary<string, string> Keys { get; } = new Dictionary<string, string> { ["ApiKey"] = "delta-api-key", ["ApiSecret"] = "delta-api-secret" };
    public override IReadOnlyDictionary<(string, string), decimal> Prices { get; } = new Dictionary<(string, string), decimal>();
    public override IReadOnlyDictionary<string, string> HarnessReplies { get; } = new Dictionary<string, string>();
}

public sealed class LoginDhanHost : BrokerContractHost
{
    public override string BrokerName => "dhan";
    public override string FixtureFile => "login_dhan.fixtures.json";
    public override IReadOnlyDictionary<string, string> Keys { get; } = new Dictionary<string, string> { ["ApiKey"] = "DH1000:::dhan-app-key", ["ApiSecret"] = "dhan-app-secret" };
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

public sealed class LoginUpstoxContractTests(LoginUpstoxHost host, ITestOutputHelper output) : IClassFixture<LoginUpstoxHost>
{
    public static readonly Dictionary<string, Deviation> Deviations = new()
    {
        ["login_refused_with_reasons"] = LoginDeviations.Refused("Upstox did not accept the sign-in: Invalid Auth code; Code expired"),
        ["login_refused_without_reasons"] = LoginDeviations.Refused("Upstox did not accept the sign-in. Start it again from the beginning."),
        ["login_empty_access_token"] = new(LoginDeviations.Words, f => ContractRunner.Expect(f, 502, ContractRunner.Error("Upstox accepted the sign-in but sent no access token. Start the sign-in again."))),
        ["login_no_access_token_in_the_answer"] = new(LoginDeviations.Words, f => ContractRunner.Expect(f, 502, ContractRunner.Error("Upstox accepted the sign-in but sent no access token. Start the sign-in again."))),
    };

    public static IEnumerable<object[]> Names() => ContractRunner.Names("login_upstox.fixtures.json");

    [PostgresTheory]
    [MemberData(nameof(Names))]
    public Task Matches_the_python_implementation(string name) => ContractRunner.RunAsync(host, name, Deviations, output);
}

public sealed class LoginFyersContractTests(LoginFyersHost host, ITestOutputHelper output) : IClassFixture<LoginFyersHost>
{
    public static readonly Dictionary<string, Deviation> Deviations = new()
    {
        ["login_refused_by_status"] = LoginDeviations.Refused("Fyers did not accept the sign-in: Invalid auth code"),
        ["login_refused_by_http_error"] = LoginDeviations.Refused("Fyers did not accept the sign-in: Invalid auth code"),
        ["login_http_error_with_an_ok_looking_body"] = LoginDeviations.Refused("Fyers did not accept the sign-in. Start it again from the beginning."),
        ["login_empty_access_token"] = new(LoginDeviations.Words, f => ContractRunner.Expect(f, 502, ContractRunner.Error("Fyers accepted the sign-in but sent no access token. Start the sign-in again."))),
        ["login_no_access_token_in_the_answer"] = new(LoginDeviations.Words, f => ContractRunner.Expect(f, 502, ContractRunner.Error("Fyers accepted the sign-in but sent no access token. Start the sign-in again."))),
    };

    public static IEnumerable<object[]> Names() => ContractRunner.Names("login_fyers.fixtures.json");

    [PostgresTheory]
    [MemberData(nameof(Names))]
    public Task Matches_the_python_implementation(string name) => ContractRunner.RunAsync(host, name, Deviations, output);
}

public sealed class LoginDeltaContractTests(LoginDeltaHost host, ITestOutputHelper output) : IClassFixture<LoginDeltaHost>
{
    public static readonly Dictionary<string, Deviation> Deviations = new()
    {
        ["login_profile_says_not_successful"] = LoginDeviations.Refused("Delta Exchange did not accept the API key. Check the key and secret in the server's settings, then sign in again."),
        ["login_key_refused"] = LoginDeviations.Refused("Delta Exchange did not accept the API key or secret. Check both in the server's settings, then sign in again."),
        ["login_blocked_by_ip_whitelist"] = LoginDeviations.Refused("Delta Exchange blocked this server. Add this server's address to the API key's allowed addresses in your Delta Exchange account, then sign in again."),
        ["login_unexpected_status"] = new(LoginDeviations.Words, f => ContractRunner.Expect(f, 502, ContractRunner.Error("Delta Exchange could not confirm the API key right now. Try again in a minute."))),
    };

    public static IEnumerable<object[]> Names() => ContractRunner.Names("login_deltaexchange.fixtures.json");

    [PostgresTheory]
    [MemberData(nameof(Names))]
    public Task Matches_the_python_implementation(string name) => ContractRunner.RunAsync(host, name, Deviations, output);
}

public sealed class LoginDhanContractTests(LoginDhanHost host, ITestOutputHelper output) : IClassFixture<LoginDhanHost>
{
    private const string NoToken = "Dhan accepted the sign-in but sent no access token. Start the sign-in again.";
    private const string Other = "This Dhan sign-in is for a different account than the client id saved for Dhan. Sign in with that account, or change the client id in the server's settings.";

    public static readonly Dictionary<string, Deviation> Deviations = new()
    {
        ["login_consent_refused"] = LoginDeviations.Refused("Dhan did not accept the sign-in. Start it again from the beginning."),
        ["login_consent_without_an_access_token"] = new(LoginDeviations.Words, f => ContractRunner.Expect(f, 502, ContractRunner.Error(NoToken))),
        ["login_consent_for_another_account"] = new(LoginDeviations.Words, f => ContractRunner.Expect(f, 409, ContractRunner.Error(Other))),
        ["login_consent_names_no_account"] = new(LoginDeviations.Words, f => ContractRunner.Expect(f, 409, ContractRunner.Error("Dhan did not confirm which account this sign-in is for, so it was not stored. Sign in again."))),
        ["login_token_fails_the_fund_limits_check"] = LoginDeviations.Refused("Dhan did not accept that access token. Sign in again to get a fresh one."),
        ["login_token_fails_the_fund_limits_check_by_status"] = LoginDeviations.Refused("Dhan did not accept that access token. Sign in again to get a fresh one."),
        ["login_pasted_access_token_for_another_account"] = new(LoginDeviations.Words, f => ContractRunner.Expect(f, 409, ContractRunner.Error(Other))),
        ["login_pasted_access_token_refused_by_dhan"] = new(LoginDeviations.Words, f => ContractRunner.Expect(f, 401, ContractRunner.Error("Dhan did not accept that access token. Sign in again to get a fresh one."))),
    };

    public static IEnumerable<object[]> Names() => ContractRunner.Names("login_dhan.fixtures.json");

    [PostgresTheory]
    [MemberData(nameof(Names))]
    public Task Matches_the_python_implementation(string name) => ContractRunner.RunAsync(host, name, Deviations, output);
}

public sealed class LoginWisdomHost : BrokerContractHost
{
    public override string BrokerName => "wisdom";
    public override string FixtureFile => "login_wisdom.fixtures.json";
    public override IReadOnlyDictionary<string, string> Keys { get; } = new Dictionary<string, string>
    {
        ["ApiKey"] = "wisdom-app-key", ["ApiSecret"] = "wisdom-app-secret", ["ApiKeyMarket"] = "wisdom-market-key", ["ApiSecretMarket"] = "wisdom-market-secret",
    };
    public override IReadOnlyDictionary<(string, string), decimal> Prices { get; } = new Dictionary<(string, string), decimal>();
    public override IReadOnlyDictionary<string, string> HarnessReplies { get; } = new Dictionary<string, string>();
}

public sealed class LoginWisdomContractTests(LoginWisdomHost host, ITestOutputHelper output) : IClassFixture<LoginWisdomHost>
{
    public static readonly Dictionary<string, Deviation> Deviations = new()
    {
        ["login_refused_with_a_message"] = LoginDeviations.Refused("Wisdom Capital did not accept the sign-in: Invalid App Key"),
        ["login_refused_with_an_ok_status_and_an_error_type"] = LoginDeviations.Refused("Wisdom Capital did not accept the sign-in: Invalid user"),
        ["login_ok_body_with_an_error_status"] = LoginDeviations.Refused("Wisdom Capital did not accept the sign-in: Gateway trouble"),
        ["login_session_without_a_token_field"] = new(LoginDeviations.Words, f => ContractRunner.Expect(f, 502, ContractRunner.Error("Wisdom Capital accepted the sign-in but sent no access token. Start the sign-in again."))),
        ["login_market_data_refused"] = LoginDeviations.Refused("Wisdom Capital accepted the sign-in but refused the market data login: Invalid market data key"),
        ["login_market_data_ok_status_error_type"] = LoginDeviations.Refused("Wisdom Capital accepted the sign-in but refused the market data login: Market data login not enabled"),
    };

    public static IEnumerable<object[]> Names() => ContractRunner.Names("login_wisdom.fixtures.json");

    [PostgresTheory]
    [MemberData(nameof(Names))]
    public Task Matches_the_python_implementation(string name) => ContractRunner.RunAsync(host, name, Deviations, output);
}

public sealed class LoginFivepaisaHost : BrokerContractHost
{
    public override string BrokerName => "fivepaisaxts";
    public override string FixtureFile => "login_fivepaisaxts.fixtures.json";
    public override IReadOnlyDictionary<string, string> Keys { get; } = new Dictionary<string, string>
    {
        ["ApiKey"] = "wisdom-app-key", ["ApiSecret"] = "wisdom-app-secret", ["ApiKeyMarket"] = "wisdom-market-key", ["ApiSecretMarket"] = "wisdom-market-secret",
    };
    public override IReadOnlyDictionary<(string, string), decimal> Prices { get; } = new Dictionary<(string, string), decimal>();
    public override IReadOnlyDictionary<string, string> HarnessReplies { get; } = new Dictionary<string, string>();
}

public sealed class LoginFivepaisaContractTests(LoginFivepaisaHost host, ITestOutputHelper output) : IClassFixture<LoginFivepaisaHost>
{
    public static readonly Dictionary<string, Deviation> Deviations = new()
    {
        ["login_refused_with_a_message"] = LoginDeviations.Refused("5paisa XTS did not accept the sign-in: Invalid App Key"),
        ["login_refused_with_an_ok_status_and_an_error_type"] = LoginDeviations.Refused("5paisa XTS did not accept the sign-in: Invalid user"),
        ["login_ok_body_with_an_error_status"] = LoginDeviations.Refused("5paisa XTS did not accept the sign-in: Gateway trouble"),
        ["login_session_without_a_token_field"] = new(LoginDeviations.Words, f => ContractRunner.Expect(f, 502, ContractRunner.Error("5paisa XTS accepted the sign-in but sent no access token. Start the sign-in again."))),
        ["login_market_data_refused"] = LoginDeviations.Refused("5paisa XTS accepted the sign-in but refused the market data login: Invalid market data key"),
        ["login_market_data_ok_status_error_type"] = LoginDeviations.Refused("5paisa XTS accepted the sign-in but refused the market data login: Market data login not enabled"),
    };

    public static IEnumerable<object[]> Names() => ContractRunner.Names("login_fivepaisaxts.fixtures.json");

    [PostgresTheory]
    [MemberData(nameof(Names))]
    public Task Matches_the_python_implementation(string name) => ContractRunner.RunAsync(host, name, Deviations, output);
}

public sealed class LoginIiflHost : BrokerContractHost
{
    public override string BrokerName => "iifl";
    public override string FixtureFile => "login_iifl.fixtures.json";
    public override IReadOnlyDictionary<string, string> Keys { get; } = new Dictionary<string, string>
    {
        ["ApiKey"] = "wisdom-app-key", ["ApiSecret"] = "wisdom-app-secret", ["ApiKeyMarket"] = "wisdom-market-key", ["ApiSecretMarket"] = "wisdom-market-secret",
    };
    public override IReadOnlyDictionary<(string, string), decimal> Prices { get; } = new Dictionary<(string, string), decimal>();
    public override IReadOnlyDictionary<string, string> HarnessReplies { get; } = new Dictionary<string, string>();
}

public sealed class LoginIiflContractTests(LoginIiflHost host, ITestOutputHelper output) : IClassFixture<LoginIiflHost>
{
    public static readonly Dictionary<string, Deviation> Deviations = new()
    {
        ["login_refused_with_a_message"] = LoginDeviations.Refused("IIFL did not accept the sign-in: Invalid App Key"),
        ["login_refused_with_an_ok_status_and_an_error_type"] = LoginDeviations.Refused("IIFL did not accept the sign-in: Invalid user"),
        ["login_ok_body_with_an_error_status"] = LoginDeviations.Refused("IIFL did not accept the sign-in: Gateway trouble"),
        ["login_session_without_a_token_field"] = new(LoginDeviations.Words, f => ContractRunner.Expect(f, 502, ContractRunner.Error("IIFL accepted the sign-in but sent no access token. Start the sign-in again."))),
        ["login_market_data_refused"] = LoginDeviations.Refused("IIFL accepted the sign-in but refused the market data login: Invalid market data key"),
        ["login_market_data_ok_status_error_type"] = LoginDeviations.Refused("IIFL accepted the sign-in but refused the market data login: Market data login not enabled"),
    };

    public static IEnumerable<object[]> Names() => ContractRunner.Names("login_iifl.fixtures.json");

    [PostgresTheory]
    [MemberData(nameof(Names))]
    public Task Matches_the_python_implementation(string name) => ContractRunner.RunAsync(host, name, Deviations, output);
}
