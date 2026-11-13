using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using EPTradeBot.Core.Brokers;
using static EPTradeBot.Infrastructure.Brokers.JsonRead;

namespace EPTradeBot.Infrastructure.Brokers.Rupeezy;

/// <summary>
/// Rupeezy sign-in (broker/rupeezy/api/auth_api.py): the trader signs in on Rupeezy's page, which returns an auth code. The code is exchanged at
/// /user/session with a checksum of the application id, the code and the API secret.
/// </summary>
public sealed partial class RupeezyPlugin
{
    public LoginMethod LoginMethod => LoginMethod.Redirect;

    public IReadOnlyList<string> LoginFields { get; } = [];

    public string? LoginUrl(IReadOnlyDictionary<string, string> keys, string state) =>
        keys.TryGetValue("ApiKey", out var key) && !string.IsNullOrWhiteSpace(key) ? $"https://flow.rupeezy.in?applicationId={key}" : null;

    public async Task<BrokerLoginResult> LoginAsync(IReadOnlyDictionary<string, string> keys, IReadOnlyDictionary<string, string> input, CancellationToken cancellationToken)
    {
        if (!keys.TryGetValue("ApiKey", out var applicationId) || string.IsNullOrWhiteSpace(applicationId) || !keys.TryGetValue("ApiSecret", out var secret) || string.IsNullOrWhiteSpace(secret))
            return BrokerLoginResult.Refused("The Rupeezy application ID and API secret are not set on this server. Add them to the server's settings, then sign in again.", 409);
        var code = input.TryGetValue("code", out var c) && !string.IsNullOrWhiteSpace(c) ? c : input.TryGetValue("request_token", out var r) ? r : null;
        if (string.IsNullOrWhiteSpace(code)) return BrokerLoginResult.Refused("Rupeezy did not send the sign-in back. Start the sign-in again.");

        var checksum = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(applicationId + code + secret))).ToLowerInvariant();
        using var request = new HttpRequestMessage(HttpMethod.Post, sessions.BaseUrl(Name, DefaultBaseUrl) + "/user/session")
        {
            Content = JsonContent.Create(new JsonObject { ["token"] = code, ["applicationId"] = applicationId, ["checksum"] = checksum }),
        };
        request.Headers.TryAddWithoutValidation("x-api-key", secret);
        using var response = await Client().SendAsync(request, cancellationToken);
        var body = await ReadAsync(response, cancellationToken) as JsonObject;
        if (!response.IsSuccessStatusCode)
            return BrokerLoginResult.Refused(Str(body?["message"]) is { Length: > 0 } why ? $"Rupeezy did not accept the sign-in: {why}" : "Rupeezy did not accept the sign-in. Start it again from the beginning.");
        if (Str((body?["data"] as JsonObject)?["access_token"]) is not { Length: > 0 } token)
            return BrokerLoginResult.Refused(Str(body?["message"]) is { Length: > 0 } msg ? $"Rupeezy did not accept the sign-in: {msg}" : "Rupeezy accepted the sign-in but sent no access token. Start the sign-in again.", 502);
        return BrokerLoginResult.Success(token, null);
    }
}
