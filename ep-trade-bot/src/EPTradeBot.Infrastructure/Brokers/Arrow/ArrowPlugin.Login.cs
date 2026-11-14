using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using EPTradeBot.Core.Brokers;
using static EPTradeBot.Infrastructure.Brokers.JsonRead;

namespace EPTradeBot.Infrastructure.Brokers.Arrow;

/// <summary>
/// Arrow sign-in (broker/arrow/api/auth_api.py): the trader signs in on Arrow's page, which returns a request token. It is exchanged at /auth/app/authenticate-token with a
/// checksum, the SHA-256 of "appID:appSecret:request-token" (colon separated).
/// </summary>
public sealed partial class ArrowPlugin : IBrokerLogin
{
    public LoginMethod LoginMethod => LoginMethod.Redirect;

    public IReadOnlyList<string> LoginFields { get; } = [];

    public string? LoginUrl(IReadOnlyDictionary<string, string> keys, string state) =>
        keys.TryGetValue("ApiKey", out var key) && !string.IsNullOrWhiteSpace(key) ? $"https://app.arrow.trade/app/login?appID={Uri.EscapeDataString(key)}" : null;

    public async Task<BrokerLoginResult> LoginAsync(IReadOnlyDictionary<string, string> keys, IReadOnlyDictionary<string, string> input, CancellationToken cancellationToken)
    {
        if (!keys.TryGetValue("ApiKey", out var appId) || string.IsNullOrWhiteSpace(appId) || !keys.TryGetValue("ApiSecret", out var secret) || string.IsNullOrWhiteSpace(secret))
            return BrokerLoginResult.Refused("The Arrow app ID and app secret are not set on this server. Add them to the server's settings, then sign in again.", 409);
        var token = input.TryGetValue("request_token", out var t) && !string.IsNullOrWhiteSpace(t) ? t : input.TryGetValue("code", out var c) ? c : null;
        if (string.IsNullOrWhiteSpace(token)) return BrokerLoginResult.Refused("Arrow did not send the sign-in back. Start the sign-in again.");

        var checksum = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{appId}:{secret}:{token}"))).ToLowerInvariant();
        using var request = new HttpRequestMessage(HttpMethod.Post, sessions.BaseUrl(Name, DefaultBaseUrl) + "/auth/app/authenticate-token")
        {
            Content = JsonContent.Create(new JsonObject { ["appID"] = appId, ["token"] = token, ["checkSum"] = checksum }),
        };
        HttpResponseMessage response;
        try
        {
            response = await Client().SendAsync(request, cancellationToken);
        }
        catch (HttpRequestException)
        {
            return BrokerLoginResult.Refused("Could not reach Arrow to sign in. Check your internet connection and try again.", 502);
        }

        using (response)
        {
            var body = await JsonRead.ReadAsync(response, cancellationToken) as JsonObject ?? [];
            if (!response.IsSuccessStatusCode)
                return BrokerLoginResult.Refused(Str(body["message"]) is { Length: > 0 } why ? $"Arrow did not accept the sign-in: {why}" : "Arrow did not accept the sign-in. Start it again from the beginning.");
            if (Str(body["status"]) != "success") return BrokerLoginResult.Refused(Str(body["message"]) is { Length: > 0 } m ? $"Arrow did not accept the sign-in: {m}" : "Arrow did not accept the sign-in. Start it again from the beginning.");
            return Str((body["data"] as JsonObject)?["token"]) is { Length: > 0 } jwt
                ? BrokerLoginResult.Success(jwt, null)
                : BrokerLoginResult.Refused("Arrow accepted the sign-in but sent no access token. Start the sign-in again.", 502);
        }
    }
}
