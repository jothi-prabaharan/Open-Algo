using System.Net.Http.Json;
using System.Text.Json.Nodes;
using EPTradeBot.Core.Brokers;
using static EPTradeBot.Infrastructure.Brokers.JsonRead;

namespace EPTradeBot.Infrastructure.Brokers.Paytm;

/// <summary>
/// Paytm Money sign-in (broker/paytm/api/auth_api.py): the trader signs in on Paytm Money's page, which returns a request token that is
/// exchanged for an access token and a public access token (the feed token).
/// </summary>
public sealed partial class PaytmPlugin
{
    public LoginMethod LoginMethod => LoginMethod.Redirect;

    public IReadOnlyList<string> LoginFields { get; } = [];

    public string? LoginUrl(IReadOnlyDictionary<string, string> keys, string state) =>
        keys.TryGetValue("ApiKey", out var key) && !string.IsNullOrWhiteSpace(key)
            ? $"https://login.paytmmoney.com/merchant-login?apiKey={Uri.EscapeDataString(key)}&state={Uri.EscapeDataString(state)}"
            : null;

    public async Task<BrokerLoginResult> LoginAsync(IReadOnlyDictionary<string, string> keys, IReadOnlyDictionary<string, string> input, CancellationToken cancellationToken)
    {
        if (!keys.TryGetValue("ApiKey", out var apiKey) || string.IsNullOrWhiteSpace(apiKey) || !keys.TryGetValue("ApiSecret", out var secret) || string.IsNullOrWhiteSpace(secret))
            return BrokerLoginResult.Refused("The Paytm Money API key and secret are not set on this server. Add them to the server's settings, then sign in again.", 409);
        if (!input.TryGetValue("request_token", out var requestToken) || string.IsNullOrWhiteSpace(requestToken))
            return BrokerLoginResult.Refused("Paytm Money did not send the sign-in back. Start the sign-in again.");

        using var request = new HttpRequestMessage(HttpMethod.Post, sessions.BaseUrl(Name, DefaultBaseUrl) + "/accounts/v2/gettoken")
        {
            Content = JsonContent.Create(new JsonObject { ["api_key"] = apiKey, ["api_secret_key"] = secret, ["request_token"] = requestToken }),
        };
        using var response = await SendAsync(request, cancellationToken);
        var body = await ReadAsync(response, cancellationToken) as JsonObject;
        if (!response.IsSuccessStatusCode)
        {
            var reasons = string.Join("; ", (body?["errors"] as JsonArray ?? []).OfType<JsonObject>().Select(e => Str(e["message"])).Where(m => !string.IsNullOrEmpty(m)));
            return BrokerLoginResult.Refused(reasons.Length > 0 ? $"Paytm Money did not accept the sign-in: {reasons}" : "Paytm Money did not accept the sign-in. Start it again from the beginning.");
        }

        if (Str(body?["access_token"]) is not { Length: > 0 } token) return BrokerLoginResult.Refused("Paytm Money accepted the sign-in but sent no access token. Start the sign-in again.", 502);
        // Without a public access token the access token serves the feed too, as OpenAlgo does.
        return BrokerLoginResult.Success(token, Str(body?["public_access_token"]) is { Length: > 0 } feed ? feed : token);
    }
}
