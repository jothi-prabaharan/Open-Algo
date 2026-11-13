using System.Text.Json.Nodes;
using EPTradeBot.Core.Brokers;
using static EPTradeBot.Infrastructure.Brokers.JsonRead;

namespace EPTradeBot.Infrastructure.Brokers.Upstox;

/// <summary>
/// Upstox sign-in (broker/upstox/api/auth_api.py): the trader signs in on Upstox's page, which sends back an authorisation code that is
/// exchanged, with the same redirect address the page was opened with, for an access token. There is no separate feed token.
/// </summary>
public sealed partial class UpstoxPlugin
{
    public LoginMethod LoginMethod => LoginMethod.Redirect;

    public IReadOnlyList<string> LoginFields { get; } = [];

    public string? LoginUrl(IReadOnlyDictionary<string, string> keys, string state) =>
        Set(keys, "ApiKey") is { } key && Set(keys, "RedirectUrl") is { } redirect
            ? $"https://api.upstox.com/v2/login/authorization/dialog?response_type=code&client_id={Uri.EscapeDataString(key)}&redirect_uri={Uri.EscapeDataString(redirect)}&state={Uri.EscapeDataString(state)}"
            : null;

    public async Task<BrokerLoginResult> LoginAsync(IReadOnlyDictionary<string, string> keys, IReadOnlyDictionary<string, string> input, CancellationToken cancellationToken)
    {
        if (Set(keys, "ApiKey") is not { } apiKey || Set(keys, "ApiSecret") is not { } secret || Set(keys, "RedirectUrl") is not { } redirect)
            return BrokerLoginResult.Refused("The Upstox API key, secret and redirect address are not all set on this server. Add them to the server's settings, then sign in again.", 409);
        if (!input.TryGetValue("code", out var code) || string.IsNullOrWhiteSpace(code))
            return BrokerLoginResult.Refused("Upstox did not send the sign-in back. Start the sign-in again.");

        using var request = new HttpRequestMessage(HttpMethod.Post, sessions.BaseUrl(Name, Api) + "/v2/login/authorization/token")
        {
            Content = new FormUrlEncodedContent([new("code", code), new("client_id", apiKey), new("client_secret", secret), new("redirect_uri", redirect), new("grant_type", "authorization_code")]),
        };
        request.Headers.TryAddWithoutValidation("Accept", "*/*");
        using var response = await SendAsync(request, cancellationToken);
        var body = await ReadAsync(response, cancellationToken) as JsonObject;
        if (!response.IsSuccessStatusCode)
        {
            var reasons = string.Join("; ", (body?["errors"] as JsonArray ?? []).OfType<JsonObject>().Select(e => Str(e["message"])).Where(m => !string.IsNullOrEmpty(m)));
            return BrokerLoginResult.Refused(reasons.Length > 0 ? $"Upstox did not accept the sign-in: {reasons}" : "Upstox did not accept the sign-in. Start it again from the beginning.");
        }

        return Str(body?["access_token"]) is { Length: > 0 } token
            ? BrokerLoginResult.Success(token, null)
            : BrokerLoginResult.Refused("Upstox accepted the sign-in but sent no access token. Start the sign-in again.", 502);
    }

    private static string? Set(IReadOnlyDictionary<string, string> keys, string name) => keys.TryGetValue(name, out var v) && !string.IsNullOrWhiteSpace(v) ? v : null;
}
