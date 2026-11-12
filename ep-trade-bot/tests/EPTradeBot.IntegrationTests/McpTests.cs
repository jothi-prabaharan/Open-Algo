using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using System.Web;
using EPTradeBot.Core.Entities.Core;
using EPTradeBot.Core.Entities.Legacy.Core;
using EPTradeBot.Core.Mcp;
using EPTradeBot.Infrastructure.Mcp;
using EPTradeBot.Infrastructure.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace EPTradeBot.IntegrationTests;

/// <summary>Remote MCP end to end: registration, the trader's approval, the tokens, and what a token lets an application do.</summary>
public sealed class McpTests
{
    private sealed class Clock(DateTimeOffset at) : TimeProvider
    {
        public DateTimeOffset At { get; set; } = at;

        public override DateTimeOffset GetUtcNow() => At;
    }

    private const string Redirect = "https://app.example.com/callback";

    private sealed record Setup(TestHost Host, Guid Account, string ReadKey, string ClientId, Clock Clock);

    private static async Task<Setup> NewAsync(Action<Microsoft.AspNetCore.Hosting.IWebHostBuilder>? more = null)
    {
        var clock = new Clock(new DateTimeOffset(2026, 11, 11, 10, 0, 0, TimeSpan.FromHours(5.5)));
        var host = new TestHost("Development", b =>
        {
            b.ConfigureServices(s => s.AddSingleton<TimeProvider>(clock));
            more?.Invoke(b);
        });
        var account = await host.CreateAccountAsync(AccountType.PAPER, "paper", 100_000, 1000, 10_000_000);
        var key = (await host.Get<ApiKeyService>().IssueAsync(account, "read")).Key;
        await using (var db = host.NewDb())
        {
            db.Set<SymToken>().Add(new SymToken { Broker = "sandbox", Symbol = "SBIN", Brsymbol = "SBIN", Exchange = "NSE", Brexchange = "NSE", Token = "3045", Lotsize = 1 });
            await db.SaveChangesAsync();
        }

        host.SetQuote("NSE", "SBIN", 500);
        using var anonymous = host.Anonymous();
        var registered = await anonymous.PostAsJsonAsync("/oauth/register", new { client_name = "Test App", redirect_uris = new[] { Redirect } });
        Assert.Equal(HttpStatusCode.Created, registered.StatusCode);
        return new Setup(host, account, key, (await registered.Content.ReadFromJsonAsync<JsonObject>())!["client_id"]!.GetValue<string>(), clock);
    }

    private static string Verifier() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(48)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static string AuthorizeUrl(Setup s, string challenge, string scope = "mcp:read mcp:write", string state = "xyz", string redirect = Redirect, string responseType = "code", string method = "S256") =>
        $"/oauth/authorize?response_type={responseType}&client_id={s.ClientId}&redirect_uri={Uri.EscapeDataString(redirect)}&scope={Uri.EscapeDataString(scope)}&state={state}&code_challenge={challenge}&code_challenge_method={method}";

    private static FormUrlEncodedContent Form(params (string, string)[] pairs) => new(pairs.Select(p => KeyValuePair.Create(p.Item1, p.Item2)));

    /// <summary>Approves as the trader would, on the page, and returns the code the application is sent back with.</summary>
    private static async Task<(HttpResponseMessage Response, string? Code)> ApproveAsync(Setup s, string challenge, string scope = "mcp:read mcp:write", bool allowWrite = true, string mode = "semi_auto",
        string adminToken = TestHost.AdminToken, string decision = "allow")
    {
        using var browser = s.Host.NoRedirects();
        var response = await browser.PostAsync("/oauth/authorize", Form(
            ("client_id", s.ClientId), ("redirect_uri", Redirect), ("scope", scope), ("state", "xyz"), ("code_challenge", challenge), ("code_challenge_method", "S256"), ("response_type", "code"),
            ("account_id", s.Account.ToString()), ("order_mode", mode), ("admin_token", adminToken), ("decision", decision), ("allow_write", allowWrite ? "1" : "0")));
        string? code = null;
        if (response.StatusCode == HttpStatusCode.Redirect) code = HttpUtility.ParseQueryString(response.Headers.Location!.Query)["code"];
        return (response, code);
    }

    private static async Task<(HttpStatusCode Status, JsonObject Body)> TokenAsync(Setup s, params (string, string)[] form)
    {
        using var client = s.Host.Anonymous();
        var response = await client.PostAsync("/oauth/token", Form(form));
        return (response.StatusCode, (JsonObject)JsonNode.Parse(await response.Content.ReadAsStringAsync())!);
    }

    private static async Task<JsonObject> GrantAsync(Setup s, string scope = "mcp:read mcp:write", bool allowWrite = true, string mode = "semi_auto")
    {
        var verifier = Verifier();
        var (_, code) = await ApproveAsync(s, McpOAuthService.Challenge(verifier), scope, allowWrite, mode);
        Assert.NotNull(code);
        var (status, tokens) = await TokenAsync(s, ("grant_type", "authorization_code"), ("client_id", s.ClientId), ("code", code!), ("redirect_uri", Redirect), ("code_verifier", verifier));
        Assert.Equal(HttpStatusCode.OK, status);
        return tokens;
    }

    private static async Task<(HttpStatusCode Status, JsonObject? Body, HttpResponseMessage Raw)> McpAsync(Setup s, string? access, object message, string? origin = null)
    {
        using var client = s.Host.Anonymous();
        var request = new HttpRequestMessage(HttpMethod.Post, "/mcp") { Content = JsonContent.Create(message) };
        if (access is not null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", access);
        if (origin is not null) request.Headers.Add("Origin", origin);
        var response = await client.SendAsync(request);
        var text = await response.Content.ReadAsStringAsync();
        return (response.StatusCode, text.Length > 0 && text.TrimStart().StartsWith('{') ? (JsonObject)JsonNode.Parse(text)! : null, response);
    }

    private static object Call(string tool, object? args = null, int id = 1) => new { jsonrpc = "2.0", id, method = "tools/call", @params = new { name = tool, arguments = args ?? new { } } };

    private static JsonObject Payload(JsonObject rpc) => JsonNode.Parse(rpc["result"]!["content"]![0]!["text"]!.GetValue<string>())!.AsObject();

    private static async Task<JsonArray> TradesAsync(Setup s)
    {
        using var client = s.Host.Anonymous();
        var response = await client.PostAsJsonAsync("/api/v1/tradebook", new { apikey = s.ReadKey });
        return (JsonArray)JsonNode.Parse(await response.Content.ReadAsStringAsync())!["data"]!;
    }

    // ---- discovery and registration ----------------------------------------------------------------------

    [PostgresFact]
    public async Task The_server_describes_itself_for_clients_that_discover_it()
    {
        var s = await NewAsync(b => b.UseSetting("Mcp:PublicBaseUrl", "https://bot.example.com/"));
        await using var _ = s.Host;
        using var client = s.Host.Anonymous();
        var resource = JsonNode.Parse(await client.GetStringAsync("/.well-known/oauth-protected-resource"))!;
        Assert.Equal("https://bot.example.com/mcp", resource["resource"]!.GetValue<string>());
        Assert.Equal("https://bot.example.com", resource["authorization_servers"]![0]!.GetValue<string>());
        var server = JsonNode.Parse(await client.GetStringAsync("/.well-known/oauth-authorization-server"))!;
        Assert.Equal("https://bot.example.com/oauth/token", server["token_endpoint"]!.GetValue<string>());
        Assert.Equal("https://bot.example.com/oauth/register", server["registration_endpoint"]!.GetValue<string>());
        Assert.Equal(["S256"], server["code_challenge_methods_supported"]!.AsArray().Select(x => x!.GetValue<string>()).ToArray());
        Assert.Equal(["none"], server["token_endpoint_auth_methods_supported"]!.AsArray().Select(x => x!.GetValue<string>()).ToArray());
    }

    [PostgresTheory]
    [InlineData("http://app.example.com/cb")]
    [InlineData("https://app.example.com/cb#frag")]
    [InlineData("javascript:alert(1)")]
    [InlineData("https://user:pw@app.example.com/cb")]
    [InlineData("not a url")]
    public async Task An_application_cannot_register_an_unsafe_return_address(string uri)
    {
        var s = await NewAsync();
        await using var _ = s.Host;
        using var client = s.Host.Anonymous();
        var response = await client.PostAsJsonAsync("/oauth/register", new { client_name = "Bad", redirect_uris = new[] { uri } });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("invalid_redirect_uri", JsonNode.Parse(await response.Content.ReadAsStringAsync())!["error"]!.GetValue<string>());
        Assert.Equal(HttpStatusCode.Created, (await client.PostAsJsonAsync("/oauth/register", new { client_name = "Desktop", redirect_uris = new[] { "http://127.0.0.1:8123/cb" } })).StatusCode);
    }

    [PostgresFact]
    public async Task The_number_of_registered_applications_is_limited()
    {
        var s = await NewAsync(b => b.UseSetting("Mcp:MaxClients", "2"));
        await using var _ = s.Host;
        using var client = s.Host.Anonymous();
        Assert.Equal(HttpStatusCode.Created, (await client.PostAsJsonAsync("/oauth/register", new { client_name = "Two", redirect_uris = new[] { Redirect } })).StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, (await client.PostAsJsonAsync("/oauth/register", new { client_name = "Three", redirect_uris = new[] { Redirect } })).StatusCode);
    }

    // ---- the trader's approval ---------------------------------------------------------------------------

    [PostgresFact]
    public async Task The_approval_page_names_the_application_and_the_account_and_asks_for_the_admin_token()
    {
        var s = await NewAsync();
        await using var _ = s.Host;
        using var browser = s.Host.NoRedirects();
        var response = await browser.GetAsync(AuthorizeUrl(s, McpOAuthService.Challenge(Verifier())));
        var html = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("Allow Test App to use EP Trade bot?", html);
        Assert.Contains("paper (sandbox)", html);
        Assert.Contains("type=\"password\"", html);
        Assert.Contains("until I approve them", html);
        Assert.DoesNotContain("<script", html);
        Assert.Equal("no-store", response.Headers.CacheControl!.ToString());
        Assert.Equal("DENY", response.Headers.GetValues("X-Frame-Options").Single());
        Assert.Contains("form-action 'self' https://app.example.com", response.Headers.GetValues("Content-Security-Policy").Single());
    }

    [PostgresFact]
    public async Task A_name_in_the_request_is_shown_as_text_never_as_markup()
    {
        var s = await NewAsync();
        await using var _ = s.Host;
        using var anonymous = s.Host.Anonymous();
        var registered = await anonymous.PostAsJsonAsync("/oauth/register", new { client_name = "<img src=x onerror=alert(1)>", redirect_uris = new[] { Redirect } });
        var id = (await registered.Content.ReadFromJsonAsync<JsonObject>())!["client_id"]!.GetValue<string>();
        using var browser = s.Host.NoRedirects();
        var html = await browser.GetStringAsync($"/oauth/authorize?response_type=code&client_id={id}&redirect_uri={Uri.EscapeDataString(Redirect)}&code_challenge={McpOAuthService.Challenge(Verifier())}&code_challenge_method=S256&state=\"><b>");
        Assert.DoesNotContain("<img src=x", html);
        Assert.Contains("&lt;img src=x", html);
        Assert.DoesNotContain("\"><b>", html);
    }

    [PostgresFact]
    public async Task A_request_for_an_unknown_application_or_address_is_never_redirected_anywhere()
    {
        var s = await NewAsync();
        await using var _ = s.Host;
        using var browser = s.Host.NoRedirects();
        var challenge = McpOAuthService.Challenge(Verifier());

        var wrongAddress = await browser.GetAsync(AuthorizeUrl(s, challenge, redirect: "https://evil.example.com/steal"));
        Assert.Equal(HttpStatusCode.BadRequest, wrongAddress.StatusCode);
        Assert.Null(wrongAddress.Headers.Location);
        Assert.Contains("does not match the one this application registered", await wrongAddress.Content.ReadAsStringAsync());

        var unknown = await browser.GetAsync($"/oauth/authorize?response_type=code&client_id=nope&redirect_uri={Uri.EscapeDataString(Redirect)}&code_challenge={challenge}&code_challenge_method=S256");
        Assert.Equal(HttpStatusCode.BadRequest, unknown.StatusCode);
        Assert.Null(unknown.Headers.Location);

        // The same for the answer to the form: with a wrong address it is shown as a page, and no code is made.
        var (posted, code) = await ApproveAsync(new Setup(s.Host, s.Account, s.ReadKey, "nope", s.Clock), challenge);
        Assert.Equal(HttpStatusCode.BadRequest, posted.StatusCode);
        Assert.Null(code);
    }

    [PostgresTheory]
    [InlineData("response_type", "token", "unsupported_response_type")]
    [InlineData("method", "plain", "invalid_request")]
    [InlineData("scope", "mcp:read admin", "invalid_scope")]
    public async Task A_request_that_is_wrong_in_another_way_is_sent_back_with_the_error(string what, string value, string error)
    {
        var s = await NewAsync();
        await using var _ = s.Host;
        using var browser = s.Host.NoRedirects();
        var challenge = McpOAuthService.Challenge(Verifier());
        var url = what switch
        {
            "response_type" => AuthorizeUrl(s, challenge, responseType: value),
            "method" => AuthorizeUrl(s, challenge, method: value),
            _ => AuthorizeUrl(s, challenge, scope: value),
        };
        var response = await browser.GetAsync(url);
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        var query = HttpUtility.ParseQueryString(response.Headers.Location!.Query);
        Assert.Equal(error, query["error"]);
        Assert.Equal("xyz", query["state"]);
        Assert.StartsWith(Redirect, response.Headers.Location.ToString());
    }

    [PostgresFact]
    public async Task Without_pkce_there_is_no_approval()
    {
        var s = await NewAsync();
        await using var _ = s.Host;
        using var browser = s.Host.NoRedirects();
        var response = await browser.GetAsync($"/oauth/authorize?response_type=code&client_id={s.ClientId}&redirect_uri={Uri.EscapeDataString(Redirect)}&state=a");
        Assert.Equal("invalid_request", HttpUtility.ParseQueryString(response.Headers.Location!.Query)["error"]);
    }

    [PostgresFact]
    public async Task Only_the_trader_can_approve_and_saying_no_sends_the_application_back_with_access_denied()
    {
        var s = await NewAsync();
        await using var _ = s.Host;
        var challenge = McpOAuthService.Challenge(Verifier());

        var (wrong, code) = await ApproveAsync(s, challenge, adminToken: "not-the-token");
        Assert.Equal(HttpStatusCode.BadRequest, wrong.StatusCode);
        Assert.Contains("admin token was not right", await wrong.Content.ReadAsStringAsync());
        Assert.Null(code);

        var (denied, _) = await ApproveAsync(s, challenge, decision: "deny");
        Assert.Equal("access_denied", HttpUtility.ParseQueryString(denied.Headers.Location!.Query)["error"]);
        await using var db = s.Host.NewDb();
        Assert.Empty(await db.McpAuthorizationCodes.ToListAsync());
    }

    // ---- tokens ------------------------------------------------------------------------------------------

    [PostgresFact]
    public async Task An_approved_application_gets_tokens_and_uses_them_on_mcp()
    {
        var s = await NewAsync();
        await using var _ = s.Host;
        var tokens = await GrantAsync(s);

        Assert.Equal("Bearer", tokens["token_type"]!.GetValue<string>());
        Assert.Equal(3600, tokens["expires_in"]!.GetValue<int>());
        Assert.Equal("mcp:read mcp:write", tokens["scope"]!.GetValue<string>());

        var access = tokens["access_token"]!.GetValue<string>();
        var (status, init, _) = await McpAsync(s, access, new { jsonrpc = "2.0", id = 1, method = "initialize", @params = new { protocolVersion = "2025-03-26" } });
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal("eptrade-bot", init!["result"]!["serverInfo"]!["name"]!.GetValue<string>());

        var (_, funds, _) = await McpAsync(s, access, Call("get_funds"));
        Assert.False(funds!["result"]!["isError"]!.GetValue<bool>(), funds.ToJsonString());
        Assert.Equal("success", Payload(funds)["data"]!["status"]!.GetValue<string>());
        var (note, none, _) = await McpAsync(s, access, new { jsonrpc = "2.0", method = "notifications/initialized" });
        Assert.Equal(HttpStatusCode.Accepted, note);
        Assert.Null(none);
    }

    [PostgresFact]
    public async Task A_code_works_once_and_only_for_the_application_address_and_verifier_it_was_made_for()
    {
        var s = await NewAsync();
        await using var _ = s.Host;
        var verifier = Verifier();
        var (_, code) = await ApproveAsync(s, McpOAuthService.Challenge(verifier));

        var badVerifier = await TokenAsync(s, ("grant_type", "authorization_code"), ("client_id", s.ClientId), ("code", code!), ("redirect_uri", Redirect), ("code_verifier", Verifier()));
        Assert.Equal("invalid_grant", badVerifier.Body["error"]!.GetValue<string>());
        // A wrong try spends the code: it cannot be guessed at.
        var good = await TokenAsync(s, ("grant_type", "authorization_code"), ("client_id", s.ClientId), ("code", code!), ("redirect_uri", Redirect), ("code_verifier", verifier));
        Assert.Equal("invalid_grant", good.Body["error"]!.GetValue<string>());

        var (_, second) = await ApproveAsync(s, McpOAuthService.Challenge(verifier));
        var wrongAddress = await TokenAsync(s, ("grant_type", "authorization_code"), ("client_id", s.ClientId), ("code", second!), ("redirect_uri", "https://app.example.com/other"), ("code_verifier", verifier));
        Assert.Equal("invalid_grant", wrongAddress.Body["error"]!.GetValue<string>());

        var (_, mine) = await ApproveAsync(s, McpOAuthService.Challenge(verifier));
        var otherApp = await TokenAsync(s, ("grant_type", "authorization_code"), ("client_id", "an-application-that-was-not-approved"), ("code", mine!), ("redirect_uri", Redirect), ("code_verifier", verifier));
        Assert.Equal("invalid_grant", otherApp.Body["error"]!.GetValue<string>());

        var (_, third) = await ApproveAsync(s, McpOAuthService.Challenge(verifier));
        var ok = await TokenAsync(s, ("grant_type", "authorization_code"), ("client_id", s.ClientId), ("code", third!), ("redirect_uri", Redirect), ("code_verifier", verifier));
        Assert.Equal(HttpStatusCode.OK, ok.Status);
        var again = await TokenAsync(s, ("grant_type", "authorization_code"), ("client_id", s.ClientId), ("code", third!), ("redirect_uri", Redirect), ("code_verifier", verifier));
        Assert.Equal("invalid_grant", again.Body["error"]!.GetValue<string>());
    }

    [PostgresFact]
    public async Task Tokens_are_never_cached_by_anything_between()
    {
        var s = await NewAsync();
        await using var _ = s.Host;
        var verifier = Verifier();
        var (_, code) = await ApproveAsync(s, McpOAuthService.Challenge(verifier));
        using var client = s.Host.Anonymous();
        var response = await client.PostAsync("/oauth/token", Form(("grant_type", "authorization_code"), ("client_id", s.ClientId), ("code", code!), ("redirect_uri", Redirect), ("code_verifier", verifier)));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("no-store", response.Headers.CacheControl!.ToString());
        Assert.Contains("no-cache", response.Headers.Pragma.ToString());
    }

    [PostgresFact]
    public async Task Two_exchanges_of_one_code_at_once_produce_one_set_of_tokens()
    {
        var s = await NewAsync();
        await using var _ = s.Host;
        var verifier = Verifier();
        var (_, code) = await ApproveAsync(s, McpOAuthService.Challenge(verifier));
        var results = await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => TokenAsync(s, ("grant_type", "authorization_code"), ("client_id", s.ClientId), ("code", code!), ("redirect_uri", Redirect), ("code_verifier", verifier))));
        Assert.Equal(1, results.Count(r => r.Status == HttpStatusCode.OK));
        await using var db = s.Host.NewDb();
        Assert.Single(await db.McpTokens.ToListAsync());
    }

    [PostgresFact]
    public async Task A_code_that_was_not_used_in_time_expires()
    {
        var s = await NewAsync();
        await using var _ = s.Host;
        var verifier = Verifier();
        var (_, code) = await ApproveAsync(s, McpOAuthService.Challenge(verifier));
        s.Clock.At = s.Clock.At.AddMinutes(11);
        var late = await TokenAsync(s, ("grant_type", "authorization_code"), ("client_id", s.ClientId), ("code", code!), ("redirect_uri", Redirect), ("code_verifier", verifier));
        Assert.Equal("invalid_grant", late.Body["error"]!.GetValue<string>());
    }

    [PostgresFact]
    public async Task Refreshing_replaces_both_tokens_and_a_refresh_token_used_twice_ends_the_whole_grant()
    {
        var s = await NewAsync();
        await using var _ = s.Host;
        var first = await GrantAsync(s);
        var refresh1 = first["refresh_token"]!.GetValue<string>();

        var (status, second) = await TokenAsync(s, ("grant_type", "refresh_token"), ("client_id", s.ClientId), ("refresh_token", refresh1));
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.NotEqual(first["access_token"]!.GetValue<string>(), second["access_token"]!.GetValue<string>());
        Assert.Equal(HttpStatusCode.Unauthorized, (await McpAsync(s, first["access_token"]!.GetValue<string>(), Call("get_funds"))).Status);
        Assert.Equal(HttpStatusCode.OK, (await McpAsync(s, second["access_token"]!.GetValue<string>(), Call("get_funds"))).Status);

        // The first refresh token comes back: a copy of it was kept somewhere, so the grant ends for everyone holding it.
        var (replayStatus, replay) = await TokenAsync(s, ("grant_type", "refresh_token"), ("client_id", s.ClientId), ("refresh_token", refresh1));
        Assert.Equal(HttpStatusCode.BadRequest, replayStatus);
        Assert.Contains("already used", replay["error_description"]!.GetValue<string>());
        Assert.Equal(HttpStatusCode.Unauthorized, (await McpAsync(s, second["access_token"]!.GetValue<string>(), Call("get_funds"))).Status);
        var (_, afterEnd) = await TokenAsync(s, ("grant_type", "refresh_token"), ("client_id", s.ClientId), ("refresh_token", second["refresh_token"]!.GetValue<string>()));
        Assert.Equal("invalid_grant", afterEnd["error"]!.GetValue<string>());
    }

    [PostgresFact]
    public async Task A_refresh_token_belongs_to_its_application()
    {
        var s = await NewAsync();
        await using var _ = s.Host;
        var tokens = await GrantAsync(s);
        var (_, body) = await TokenAsync(s, ("grant_type", "refresh_token"), ("client_id", "someone-else"), ("refresh_token", tokens["refresh_token"]!.GetValue<string>()));
        Assert.Equal("invalid_grant", body["error"]!.GetValue<string>());
        var (status, unsupported) = await TokenAsync(s, ("grant_type", "password"), ("client_id", s.ClientId));
        Assert.Equal((HttpStatusCode.BadRequest, "unsupported_grant_type"), (status, unsupported["error"]!.GetValue<string>()));
    }

    [PostgresFact]
    public async Task Tokens_are_kept_only_as_hashes_and_never_in_the_clear()
    {
        var s = await NewAsync();
        await using var _ = s.Host;
        var tokens = await GrantAsync(s);
        await using var db = s.Host.NewDb();
        var row = await db.McpTokens.SingleAsync();
        Assert.DoesNotContain(tokens["access_token"]!.GetValue<string>(), row.AccessHash);
        Assert.Equal(64, row.AccessHash.Length);
        Assert.NotEqual(row.AccessHash, row.RefreshHash);
    }

    // ---- /mcp --------------------------------------------------------------------------------------------

    [PostgresFact]
    public async Task Without_a_token_or_with_a_bad_or_ended_one_mcp_answers_401_and_says_where_to_sign_in()
    {
        var s = await NewAsync(b => b.UseSetting("Mcp:PublicBaseUrl", "https://bot.example.com"));
        await using var _ = s.Host;
        var (none, _, noneRaw) = await McpAsync(s, null, Call("get_funds"));
        Assert.Equal(HttpStatusCode.Unauthorized, none);
        Assert.Contains("resource_metadata=\"https://bot.example.com/.well-known/oauth-protected-resource\"", noneRaw.Headers.WwwAuthenticate.ToString());

        var (bad, _, badRaw) = await McpAsync(s, "mcp_nonsense", Call("get_funds"));
        Assert.Equal(HttpStatusCode.Unauthorized, bad);
        Assert.Contains("invalid_token", badRaw.Headers.WwwAuthenticate.ToString());

        var tokens = await GrantAsync(s);
        var access = tokens["access_token"]!.GetValue<string>();
        Assert.Equal(HttpStatusCode.OK, (await McpAsync(s, access, Call("get_funds"))).Status);
        s.Clock.At = s.Clock.At.AddMinutes(61);
        Assert.Equal(HttpStatusCode.Unauthorized, (await McpAsync(s, access, Call("get_funds"))).Status);
    }

    [PostgresFact]
    public async Task The_trader_can_end_one_access_or_everything_an_application_holds_and_a_switched_off_account_ends_access_too()
    {
        var s = await NewAsync();
        await using var _ = s.Host;
        var one = (await GrantAsync(s))["access_token"]!.GetValue<string>();
        var two = (await GrantAsync(s))["access_token"]!.GetValue<string>();

        var list = JsonNode.Parse(await s.Host.Admin.GetStringAsync("/api/v1/mcp/tokens"))!.AsArray();
        Assert.Equal(2, list.Count);
        Assert.DoesNotContain("access_token", list.ToJsonString());

        Assert.Equal(HttpStatusCode.NoContent, (await s.Host.Admin.DeleteAsync($"/api/v1/mcp/tokens/{list[0]!["id"]}")).StatusCode);
        var alive = new[] { one, two }.Count(t => McpAsync(s, t, Call("get_funds")).Result.Status == HttpStatusCode.OK);
        Assert.Equal(1, alive);

        Assert.Equal(HttpStatusCode.NoContent, (await s.Host.Admin.DeleteAsync($"/api/v1/mcp/clients/{s.ClientId}")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await McpAsync(s, one, Call("get_funds"))).Status);
        Assert.Equal(HttpStatusCode.Unauthorized, (await McpAsync(s, two, Call("get_funds"))).Status);

        var s2 = await NewAsync();
        await using var _2 = s2.Host;
        var access = (await GrantAsync(s2))["access_token"]!.GetValue<string>();
        await using (var db = s2.Host.NewDb())
            await db.BrokerAccounts.Where(a => a.Id == s2.Account).ExecuteUpdateAsync(u => u.SetProperty(a => a.IsEnabled, false));
        Assert.Equal(HttpStatusCode.Unauthorized, (await McpAsync(s2, access, Call("get_funds"))).Status);
    }

    [PostgresFact]
    public async Task A_read_only_grant_sees_no_write_tools_and_a_write_is_refused_with_403_naming_the_scope()
    {
        var s = await NewAsync();
        await using var _ = s.Host;
        var access = (await GrantAsync(s, scope: "mcp:read mcp:write", allowWrite: false))["access_token"]!.GetValue<string>();

        var (_, list, _) = await McpAsync(s, access, new { jsonrpc = "2.0", id = 1, method = "tools/list" });
        var names = list!["result"]!["tools"]!.AsArray().Select(t => t!["name"]!.GetValue<string>()).ToArray();
        Assert.Contains("get_funds", names);
        Assert.DoesNotContain("place_order", names);

        var (status, body, raw) = await McpAsync(s, access, Call("place_order", new { symbol = "SBIN", quantity = 1, action = "BUY" }));
        Assert.Equal(HttpStatusCode.Forbidden, status);
        Assert.Contains("insufficient_scope", raw.Headers.WwwAuthenticate.ToString());
        Assert.Contains("mcp:write", raw.Headers.WwwAuthenticate.ToString());
        Assert.Equal(-32001, body!["error"]!["code"]!.GetValue<int>());
        Assert.Empty(await TradesAsync(s));
    }

    [PostgresFact]
    public async Task What_the_trader_ticked_is_what_is_granted_even_if_the_application_asked_for_more()
    {
        var s = await NewAsync();
        await using var _ = s.Host;
        // Asked for write, trader allowed read only: the token holds read only.
        var tokens = await GrantAsync(s, scope: "mcp:read mcp:write", allowWrite: false);
        Assert.Equal("mcp:read", tokens["scope"]!.GetValue<string>());
    }

    [PostgresFact]
    public async Task A_semi_auto_grants_orders_wait_for_approval_and_an_auto_grants_orders_are_sent()
    {
        var s = await NewAsync();
        await using var _ = s.Host;
        var semi = (await GrantAsync(s, mode: "semi_auto"))["access_token"]!.GetValue<string>();
        var (_, held, _) = await McpAsync(s, semi, Call("place_order", new { symbol = "SBIN", quantity = 10, action = "BUY" }));
        Assert.False(held!["result"]!["isError"]!.GetValue<bool>(), held.ToJsonString());
        Assert.Equal("semi_auto", Payload(held)["data"]!["mode"]!.GetValue<string>());
        Assert.Empty(await TradesAsync(s));
        var waiting = JsonNode.Parse(await s.Host.Admin.GetStringAsync("/api/v1/action-center/orders?status=pending"))!["data"]!.AsArray();
        var pending = Assert.Single(waiting);
        Assert.Equal("python mcp", pending!["order_data"]!["strategy"]!.GetValue<string>());

        var auto = (await GrantAsync(s, mode: "auto"))["access_token"]!.GetValue<string>();
        var (_, sent, _) = await McpAsync(s, auto, Call("place_order", new { symbol = "SBIN", quantity = 10, action = "BUY" }));
        Assert.False(sent!["result"]!["isError"]!.GetValue<bool>(), sent.ToJsonString());
        Assert.Single(await TradesAsync(s));
    }

    [PostgresFact]
    public async Task An_order_over_the_accounts_limit_is_refused_through_mcp_like_any_other()
    {
        var s = await NewAsync();
        await using var _ = s.Host;
        await s.Host.Admin.PutAsJsonAsync($"/api/v1/accounts/{s.Account}/risk", new { max_daily_loss = 100_000, max_order_quantity = 5, max_position_value = 10_000_000 });
        var auto = (await GrantAsync(s, mode: "auto"))["access_token"]!.GetValue<string>();
        var (_, refused, _) = await McpAsync(s, auto, Call("place_order", new { symbol = "SBIN", quantity = 50, action = "BUY" }));
        Assert.True(refused!["result"]!["isError"]!.GetValue<bool>());
        Assert.Equal("api_error", Payload(refused)["error"]!["error_type"]!.GetValue<string>());
        Assert.False(Payload(refused)["error"]!["retry_safe"]!.GetValue<bool>());
        Assert.Empty(await TradesAsync(s));
    }

    [PostgresFact]
    public async Task Browser_origins_are_refused_unless_allowed_and_an_allowed_one_gets_cors_headers()
    {
        var s = await NewAsync(b => b.UseSetting("Mcp:AllowedOrigins:0", "https://claude.ai"));
        await using var _ = s.Host;
        var access = (await GrantAsync(s))["access_token"]!.GetValue<string>();

        Assert.Equal(HttpStatusCode.Forbidden, (await McpAsync(s, access, Call("get_funds"), origin: "https://evil.example.com")).Status);
        var (status, _, raw) = await McpAsync(s, access, Call("get_funds"), origin: "https://claude.ai");
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal("https://claude.ai", raw.Headers.GetValues("Access-Control-Allow-Origin").Single());

        using var client = s.Host.Anonymous();
        var pre = new HttpRequestMessage(HttpMethod.Options, "/mcp");
        pre.Headers.Add("Origin", "https://claude.ai");
        var allowed = await client.SendAsync(pre);
        Assert.Equal(HttpStatusCode.NoContent, allowed.StatusCode);
        Assert.Contains("Authorization", allowed.Headers.GetValues("Access-Control-Allow-Headers").Single());
        var bad = new HttpRequestMessage(HttpMethod.Options, "/mcp");
        bad.Headers.Add("Origin", "https://evil.example.com");
        Assert.Equal(HttpStatusCode.Forbidden, (await client.SendAsync(bad)).StatusCode);
    }

    [PostgresFact]
    public async Task The_streaming_channel_is_not_offered_and_the_answer_says_which_methods_are()
    {
        var s = await NewAsync();
        await using var _ = s.Host;
        using var client = s.Host.Anonymous();
        var response = await client.GetAsync("/mcp");
        Assert.Equal(HttpStatusCode.MethodNotAllowed, response.StatusCode);
        Assert.Contains("POST", response.Content.Headers.Allow);
    }

    [PostgresFact]
    public async Task Every_call_is_recorded_without_its_arguments_and_the_records_need_the_admin_token()
    {
        var s = await NewAsync();
        await using var _ = s.Host;
        var access = (await GrantAsync(s))["access_token"]!.GetValue<string>();
        await McpAsync(s, access, Call("get_quote", new { symbol = "SBIN" }));
        await McpAsync(s, access, Call("get_funds"));

        var calls = JsonNode.Parse(await s.Host.Admin.GetStringAsync("/api/v1/mcp/calls"))!.AsArray();
        Assert.Equal(["get_funds", "get_quote"], calls.Select(c => c!["tool"]!.GetValue<string>()).Order().ToArray());
        Assert.All(calls, c => Assert.Equal(s.ClientId, c!["client_id"]!.GetValue<string>()));
        Assert.DoesNotContain("SBIN", calls.ToJsonString());
        using var anonymous = s.Host.Anonymous();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/v1/mcp/calls")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/v1/mcp/tokens")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/v1/mcp/clients")).StatusCode);
    }

    // ---- stdio, against the real API ---------------------------------------------------------------------

    [PostgresFact]
    public async Task The_stdio_server_reaches_the_platform_with_an_api_key_and_the_keys_own_order_mode_applies()
    {
        var s = await NewAsync();
        await using var _ = s.Host;
        var (record, semiKey) = await s.Host.Get<ApiKeyService>().IssueAsync(s.Account, "mcp");
        (await s.Host.Admin.PutAsJsonAsync($"/api/v1/accounts/{s.Account}/apikeys/{record.Id}/order-mode", new { mode = "semi_auto" })).EnsureSuccessStatusCode();

        var api = new HttpPlatformApi(s.Host.Anonymous(), semiKey);
        var server = new McpServer(McpTools.All, new McpPolicy(), new McpRateLimiter());
        var input = new StringReader(string.Join("\n",
            new { jsonrpc = "2.0", id = 1, method = "initialize", @params = new { protocolVersion = "2025-03-26" } }.ToJson(),
            Call("get_funds", id: 2).ToJson(),
            Call("place_order", new { symbol = "SBIN", quantity = 10, action = "BUY" }, id: 3).ToJson()) + "\n");
        var output = new StringWriter { NewLine = "\n" };

        await new StdioTransport(server, new McpSession { Api = api }).RunAsync(input, output, default);

        var lines = output.ToString().TrimEnd('\n').Split('\n').Select(l => JsonNode.Parse(l)!.AsObject()).ToList();
        Assert.Equal(3, lines.Count);
        Assert.False(lines[1]["result"]!["isError"]!.GetValue<bool>(), lines[1].ToJsonString());
        Assert.Equal("semi_auto", Payload(lines[2])["data"]!["mode"]!.GetValue<string>());
        Assert.Empty(await TradesAsync(s));
        Assert.Single(JsonNode.Parse(await s.Host.Admin.GetStringAsync("/api/v1/action-center/orders?status=pending"))!["data"]!.AsArray());

        // A key that is not valid gets the platform's own words, not a code.
        var wrong = new HttpPlatformApi(s.Host.Anonymous(), "not-a-key");
        var refused = await wrong.CallAsync("funds", [], default);
        Assert.Equal("error", refused["status"]!.GetValue<string>());
        Assert.Contains("apikey", refused["error"]!.GetValue<string>(), StringComparison.OrdinalIgnoreCase);
    }
}

internal static class McpTestExtensions
{
    public static string ToJson(this object o) => System.Text.Json.JsonSerializer.Serialize(o);
}
