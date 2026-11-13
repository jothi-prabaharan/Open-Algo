using System.Net.Http.Json;
using System.Text.Json.Nodes;
using EPTradeBot.Core.Entities.Core;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;

namespace EPTradeBot.IntegrationTests;

/// <summary>Signing in to a broker through EP Trade bot: only a sign-in this server started, for this account, once, within ten minutes, completes.</summary>
public sealed class LoginApiTests
{
    private const string KiteToken = """{"status":"success","data":{"access_token":"kite-new-token"}}""";

    private sealed class SteppingClock(DateTimeOffset at) : TimeProvider
    {
        public DateTimeOffset At { get; set; } = at;
        public override DateTimeOffset GetUtcNow() => At;
    }

    private static TestHost Host(FakeBroker broker, SteppingClock? clock = null, bool keys = true) => new("Production", b =>
    {
        b.UseSetting("Brokers:Enabled:0", "zerodha");
        b.UseSetting("Brokers:Enabled:1", "angel");
        b.UseSetting("Brokers:Enabled:2", "upstox");
        if (keys)
        {
            b.UseSetting("Brokers:Keys:zerodha:ApiKey", "kite-key");
            b.UseSetting("Brokers:Keys:zerodha:ApiSecret", "kite-secret");
        }

        b.ConfigureServices(s =>
        {
            s.ConfigureHttpClientDefaults(h => h.ConfigurePrimaryHttpMessageHandler(() => broker));
            if (clock is not null) s.AddSingleton<TimeProvider>(clock);
        });
    });

    private static async Task<(int Status, JsonObject Body)> PostAsync(TestHost host, string path, object? body = null)
    {
        var response = await host.Admin.PostAsJsonAsync(path, body ?? new { });
        return ((int)response.StatusCode, (JsonObject)JsonNode.Parse(await response.Content.ReadAsStringAsync())!);
    }

    [PostgresFact]
    public async Task Start_gives_the_brokers_page_carrying_a_state_and_complete_accepts_that_state_once()
    {
        var broker = new FakeBroker();
        broker.Reset(new Dictionary<string, (int, string)> { ["POST /session/token"] = (200, KiteToken) });
        await using var host = Host(broker);
        var account = await host.CreateAccountAsync(AccountType.LIVE, "kite", broker: "zerodha");

        var (status, started) = await PostAsync(host, $"/api/v1/accounts/{account}/broker-login/start");
        Assert.Equal(200, status);
        Assert.Equal("redirect", started["method"]!.GetValue<string>());
        var state = started["state"]!.GetValue<string>();
        var url = started["login_url"]!.GetValue<string>();
        Assert.StartsWith("https://kite.zerodha.com/connect/login?v=3&api_key=kite-key&redirect_params=", url);
        Assert.Contains(Uri.EscapeDataString("state=" + state), url);
        Assert.Empty(broker.Sent);

        (status, _) = await PostAsync(host, $"/api/v1/accounts/{account}/broker-login/complete", new { request_token = "rt", state });
        Assert.Equal(200, status);

        // The same answer a second time is refused, and nothing more goes to the broker.
        (status, var again) = await PostAsync(host, $"/api/v1/accounts/{account}/broker-login/complete", new { request_token = "rt", state });
        Assert.Equal(400, status);
        Assert.Contains("Start the sign-in again", again["message"]!.GetValue<string>());
        Assert.Single(broker.Sent);
    }

    [PostgresFact]
    public async Task Upstox_start_names_the_redirect_address_and_complete_exchanges_the_code()
    {
        var broker = new FakeBroker();
        broker.Reset(new Dictionary<string, (int, string)> { ["POST /v2/login/authorization/token"] = (200, """{"access_token":"upstox-new-token"}""") });
        await using var host = new TestHost("Production", b =>
        {
            b.UseSetting("Brokers:Enabled:0", "upstox");
            b.UseSetting("Brokers:Keys:upstox:ApiKey", "up-key");
            b.UseSetting("Brokers:Keys:upstox:ApiSecret", "up-secret");
            b.UseSetting("Brokers:Keys:upstox:RedirectUrl", "https://bot.example/upstox/callback");
            b.ConfigureServices(s => s.ConfigureHttpClientDefaults(h => h.ConfigurePrimaryHttpMessageHandler(() => broker)));
        });
        var account = await host.CreateAccountAsync(AccountType.LIVE, "up", broker: "upstox");

        var (status, started) = await PostAsync(host, $"/api/v1/accounts/{account}/broker-login/start");
        Assert.Equal(200, status);
        var state = started["state"]!.GetValue<string>();
        Assert.Equal(
            $"https://api.upstox.com/v2/login/authorization/dialog?response_type=code&client_id=up-key&redirect_uri={Uri.EscapeDataString("https://bot.example/upstox/callback")}&state={Uri.EscapeDataString(state)}",
            started["login_url"]!.GetValue<string>());

        (status, _) = await PostAsync(host, $"/api/v1/accounts/{account}/broker-login/complete", new { code = "c1", state });
        Assert.Equal(200, status);
        Assert.Single(broker.Sent);
    }

    [PostgresFact]
    public async Task An_answer_without_a_state_started_here_is_refused_and_reaches_no_broker()
    {
        var broker = new FakeBroker();
        broker.Reset(new Dictionary<string, (int, string)> { ["POST /session/token"] = (200, KiteToken) });
        await using var host = Host(broker);
        var mine = await host.CreateAccountAsync(AccountType.LIVE, "mine", broker: "zerodha", clientId: "A1");
        var other = await host.CreateAccountAsync(AccountType.LIVE, "other", broker: "zerodha", clientId: "A2");

        var (_, started) = await PostAsync(host, $"/api/v1/accounts/{mine}/broker-login/start");
        var state = started["state"]!.GetValue<string>();

        foreach (var (account, body) in new (Guid, object)[]
        {
            (mine, new { request_token = "rt" }),
            (mine, new { request_token = "rt", state = "not-a-state" }),
            (other, new { request_token = "rt", state }),
        })
        {
            var (status, answer) = await PostAsync(host, $"/api/v1/accounts/{account}/broker-login/complete", body);
            Assert.Equal(400, status);
            Assert.Contains("not started from EP Trade bot", answer["message"]!.GetValue<string>());
        }

        Assert.Empty(broker.Sent);
    }

    [PostgresFact]
    public async Task A_state_is_good_for_ten_minutes()
    {
        var clock = new SteppingClock(new DateTimeOffset(2026, 11, 3, 9, 0, 0, TimeSpan.FromHours(5.5)));
        var broker = new FakeBroker();
        broker.Reset(new Dictionary<string, (int, string)> { ["POST /session/token"] = (200, KiteToken) });
        await using var host = Host(broker, clock);
        var account = await host.CreateAccountAsync(AccountType.LIVE, "kite", broker: "zerodha");

        var (_, first) = await PostAsync(host, $"/api/v1/accounts/{account}/broker-login/start");
        clock.At = clock.At.AddMinutes(9);
        var (status, _) = await PostAsync(host, $"/api/v1/accounts/{account}/broker-login/complete", new { request_token = "rt", state = first["state"]!.GetValue<string>() });
        Assert.Equal(200, status);

        var (_, second) = await PostAsync(host, $"/api/v1/accounts/{account}/broker-login/start");
        clock.At = clock.At.AddMinutes(11);
        (status, _) = await PostAsync(host, $"/api/v1/accounts/{account}/broker-login/complete", new { request_token = "rt", state = second["state"]!.GetValue<string>() });
        Assert.Equal(400, status);
        Assert.Single(broker.Sent);
    }

    [PostgresFact]
    public async Task A_credentials_broker_asks_for_its_fields_and_a_sandbox_account_has_nothing_to_sign_in_to()
    {
        await using var host = Host(new FakeBroker());
        var angel = await host.CreateAccountAsync(AccountType.LIVE, "angel", broker: "angel");
        var (status, started) = await PostAsync(host, $"/api/v1/accounts/{angel}/broker-login/start");
        Assert.Equal(200, status);
        Assert.Equal("credentials", started["method"]!.GetValue<string>());
        Assert.Equal(["clientcode", "password", "totp"], started["fields"]!.AsArray().Select(x => x!.GetValue<string>()));

        var paper = await host.CreateAccountAsync(AccountType.PAPER, "paper");
        (status, var refused) = await PostAsync(host, $"/api/v1/accounts/{paper}/broker-login/start");
        Assert.Equal(400, status);
        Assert.Contains("sandbox", refused["message"]!.GetValue<string>());
    }

    [PostgresFact]
    public async Task Missing_application_keys_are_named_and_nothing_is_sent()
    {
        var broker = new FakeBroker();
        await using var host = Host(broker, keys: false);
        var account = await host.CreateAccountAsync(AccountType.LIVE, "kite", broker: "zerodha");
        var (status, answer) = await PostAsync(host, $"/api/v1/accounts/{account}/broker-login/start");
        Assert.Equal(409, status);
        Assert.Contains("API key is not set", answer["message"]!.GetValue<string>());
        Assert.Empty(broker.Sent);
    }

    [PostgresFact]
    public async Task Signing_in_needs_the_admin_token()
    {
        await using var host = Host(new FakeBroker());
        var account = await host.CreateAccountAsync(AccountType.LIVE, "kite", broker: "zerodha");
        using var anonymous = host.Anonymous();
        Assert.Equal(System.Net.HttpStatusCode.Unauthorized, (await anonymous.PostAsJsonAsync($"/api/v1/accounts/{account}/broker-login/start", new { })).StatusCode);
    }
}
