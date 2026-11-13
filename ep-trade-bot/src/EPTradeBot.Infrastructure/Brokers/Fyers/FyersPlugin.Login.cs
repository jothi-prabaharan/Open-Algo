using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using EPTradeBot.Core.Brokers;
using static EPTradeBot.Infrastructure.Brokers.JsonRead;

namespace EPTradeBot.Infrastructure.Brokers.Fyers;

/// <summary>
/// Fyers sign-in (broker/fyers/api/auth_api.py): the trader signs in on Fyers' page, which sends back an auth code that is exchanged, with a
/// SHA-256 hash of the app id and secret, for an access token. The app id is sent with the token on every call, so there is no feed token.
/// </summary>
public sealed partial class FyersPlugin
{
    public LoginMethod LoginMethod => LoginMethod.Redirect;

    public IReadOnlyList<string> LoginFields { get; } = [];

    public string? LoginUrl(IReadOnlyDictionary<string, string> keys, string state) =>
        Set(keys, "ApiKey") is { } key && Set(keys, "RedirectUrl") is { } redirect
            ? $"https://api-t1.fyers.in/api/v3/generate-authcode?client_id={Uri.EscapeDataString(key)}&redirect_uri={Uri.EscapeDataString(redirect)}&response_type=code&state={Uri.EscapeDataString(state)}"
            : null;

    public async Task<BrokerLoginResult> LoginAsync(IReadOnlyDictionary<string, string> keys, IReadOnlyDictionary<string, string> input, CancellationToken cancellationToken)
    {
        if (Set(keys, "ApiKey") is not { } apiKey || Set(keys, "ApiSecret") is not { } secret)
            return BrokerLoginResult.Refused("The Fyers app id and secret are not set on this server. Add them to the server's settings, then sign in again.", 409);
        if (!input.TryGetValue("request_token", out var code) || string.IsNullOrWhiteSpace(code))
            return BrokerLoginResult.Refused("Fyers did not send the sign-in back. Start the sign-in again.");

        var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes($"{apiKey}:{secret}")));
        using var request = new HttpRequestMessage(HttpMethod.Post, sessions.BaseUrl(Name, BaseUrl) + "/api/v3/validate-authcode")
        {
            Content = JsonContent.Create(new JsonObject { ["grant_type"] = "authorization_code", ["appIdHash"] = hash, ["code"] = code }),
        };
        request.Headers.TryAddWithoutValidation("Accept", "application/json");
        using var response = await SendAsync(request, cancellationToken);
        var body = await ReadAsync(response, cancellationToken) as JsonObject;
        if (!response.IsSuccessStatusCode || Str(body?["s"]) != "ok")
        {
            var reason = Str(body?["message"]);
            return BrokerLoginResult.Refused(reason is { Length: > 0 } ? $"Fyers did not accept the sign-in: {reason}" : "Fyers did not accept the sign-in. Start it again from the beginning.");
        }

        return Str(body?["access_token"]) is { Length: > 0 } token
            ? BrokerLoginResult.Success(token, null)
            : BrokerLoginResult.Refused("Fyers accepted the sign-in but sent no access token. Start the sign-in again.", 502);
    }

    private static string? Set(IReadOnlyDictionary<string, string> keys, string name) => keys.TryGetValue(name, out var v) && !string.IsNullOrWhiteSpace(v) ? v : null;
}
